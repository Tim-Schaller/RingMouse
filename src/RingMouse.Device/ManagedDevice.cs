using Microsoft.Extensions.Logging;
using RingMouse.Core.Config;
using RingMouse.HidPlusPlus;
using RingMouse.HidPlusPlus.Discovery;
using RingMouse.HidPlusPlus.Features;

namespace RingMouse.Device;

internal interface IDeviceHost
{
    DeviceConfiguration Configuration { get; }
    DeviceServiceOptions Options { get; }
    ILogger Logger { get; }
    CancellationToken Stopping { get; }
    void OnButton(ButtonEvent e);
    void OnRawXY(RawXYEvent e);
    void OnSnapshot(ManagedDevice source, DeviceSnapshot snapshot);
}

/// <summary>
/// Ein logisches HID++-Gerät mit Lebenszyklus: Connecting → Ready ⇄ Unreachable.
/// Konfiguration (Diversion, Analytics aus, DPI, Akku) wird bei jedem Reconnect, nach Standby/Entsperren,
/// bei 0x1D4B "Reconfiguration needed" und wenn der Watchdog eine verlorene Umleitung findet neu angewendet.
/// </summary>
internal sealed class ManagedDevice : IDisposable
{
    private readonly IDeviceHost _host;
    private readonly HidppEndpoint _endpoint;
    private readonly HidppDevice _device;
    private readonly SemaphoreSlim _configLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly List<(DateTime Due, string Reason)> _pending = [];
    // Änderungen nur unter _configLock UND _stateLock (Snapshot() liest von anderen Threads unter _stateLock).
    private readonly HashSet<ushort> _diverted = [];
    private readonly HashSet<ushort> _rawXY = [];
    private HashSet<ushort> _pressed = [];
    private HashSet<ushort> _failed = [];

    private bool _identityLoaded;
    private string _name;
    private DeviceKind _kind = DeviceKind.Unknown;
    private ProtocolVersion? _protocol;
    private string? _unitId;
    private ReprogControlsV4Feature? _reprog;
    private BatteryFeature? _battery;
    private AdjustableDpiFeature? _dpiFeature;
    private byte? _wirelessIndex;
    private IReadOnlyList<ControlInfo> _controls = [];
    private IReadOnlyList<int> _supportedDpi = [];
    private int? _dpi;
    private int? _sessionDpi;
    private BatteryReading? _batteryReading;
    private DateTimeOffset? _batteryTime;
    private DeviceState _state = DeviceState.Connecting;
    private DateTime _nextWatchdog = DateTime.MaxValue;
    private DateTime _nextBatteryPoll = DateTime.MaxValue;
    private int _busy;
    private int _disposed;

    public ManagedDevice(IDeviceHost host, HidppEndpoint endpoint, HidppDevice device, string connection, ushort productId)
    {
        _host = host;
        _endpoint = endpoint;
        _device = device;
        _name = endpoint.DisplayName;
        Connection = connection;
        ProductId = productId;
        Key = $"{productId:X4}-{StableHash(endpoint.Key):X8}-{device.DeviceIndex:X2}";
        _device.Timeout = host.Options.RequestTimeout;
        _device.Notification += OnNotification;
    }

    public string Key { get; }
    public string Connection { get; }
    public ushort ProductId { get; }
    public byte DeviceIndex => _device.DeviceIndex;
    public DeviceState State => _state;
    public string Name => _name;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    private ILogger Log => _host.Logger;

    // ------------------------------------------------------------------ Planung (Loop-Thread)

    public void RequestConfigure(TimeSpan delay, string reason)
    {
        lock (_stateLock) _pending.Add((DateTime.UtcNow + delay, reason));
    }

    public void Tick(DateTime now, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        string? configureReason = null;
        lock (_stateLock)
        {
            var due = _pending.Where(p => p.Due <= now).ToList();
            if (due.Count > 0)
            {
                configureReason = string.Join(" + ", due.Select(d => d.Reason).Distinct());
                _pending.RemoveAll(p => p.Due <= now);
            }
        }

        var watchdogDue = now >= _nextWatchdog;
        var batteryDue = _state == DeviceState.Ready && _battery is not null && now >= _nextBatteryPoll;
        if (configureReason is null && !watchdogDue && !batteryDue) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            if (configureReason is not null) RequestConfigure(TimeSpan.FromMilliseconds(500), configureReason); // später erneut
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (configureReason is not null)
                {
                    await ConfigureAsync(configureReason, ct).ConfigureAwait(false);
                }
                else if (watchdogDue)
                {
                    _nextWatchdog = now + _host.Options.WatchdogInterval;
                    await WatchdogAsync(ct).ConfigureAwait(false);
                }
                else if (batteryDue)
                {
                    _nextBatteryPoll = now + _host.Configuration.BatteryPollInterval;
                    await PollBatteryAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Beenden
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "{Device}: unerwarteter Fehler im Gerätezyklus", _name);
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        }, CancellationToken.None);
    }

    // ------------------------------------------------------------------ Konfiguration

    public async Task ConfigureAsync(string reason, CancellationToken ct)
    {
        await _configLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wasReady = _state == DeviceState.Ready;
            try
            {
                _protocol = await _device.GetProtocolVersionAsync(ct, _host.Options.PingTimeout).ConfigureAwait(false);
            }
            catch (HidppException ex) when (ex is HidppTimeoutException or HidppTransportException)
            {
                MarkUnreachable($"keine Antwort ({reason})");
                return;
            }

            if (!_protocol.Value.IsHidpp20)
            {
                Log.LogInformation("{Device}: HID++ {Version} (1.0-Gerät) – wird nicht verwaltet", _name, _protocol);
                MarkUnreachable("HID++ 1.0 nicht unterstützt");
                return;
            }

            if (!_identityLoaded) await LoadIdentityAsync(ct).ConfigureAwait(false);

            var config = _host.Configuration;
            var settings = config.SettingsFor(_name, ProductId, _unitId);
            if (!settings.Enabled)
            {
                await ResetDiversionsAsync(ct).ConfigureAwait(false);
                SetState(DeviceState.Ready);
                ArmTimers(config);
                Log.LogInformation("{Device}: laut Config deaktiviert – Tasten nativ", _name);
                Publish();
                return;
            }

            await ApplyReportingAsync(config, ct).ConfigureAwait(false);
            await ApplyDpiAsync(settings, ct).ConfigureAwait(false);
            if (_battery is not null)
            {
                _battery.Curve = config.VoltageCurve ?? BatteryVoltageCurve.Default;
                await ReadBatteryAsync(ct).ConfigureAwait(false);
            }

            SetState(DeviceState.Ready);
            ArmTimers(config);

            Log.Log(wasReady ? LogLevel.Debug : LogLevel.Information,
                "{Device} konfiguriert ({Reason}): umgeleitet [{Diverted}] Raw-XY [{RawXY}] fehlgeschlagen [{Failed}] DPI {Dpi} Akku {Battery}",
                _name, reason, Format(_diverted), Format(_rawXY), Format(_failed), _dpi, _batteryReading?.Percent);
            Publish();
        }
        catch (HidppException ex) when (ex is HidppTimeoutException or HidppTransportException)
        {
            MarkUnreachable($"{reason}: {ex.Message}");
        }
        catch (HidppErrorException ex) when (_identityLoaded)
        {
            // Einzelne Einstellung abgelehnt – der Rest gilt; der Watchdog prüft weiter.
            Log.LogWarning("{Device}: Gerät meldet Fehler bei der Konfiguration ({Reason}): {Error}", _name, reason, ex.Message);
            SetState(DeviceState.Ready);
            ArmTimers(_host.Configuration);
            Publish();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Erkennung gescheitert (z.B. direkt nach dem Verbinden noch nicht bereit) oder unerwarteter Fehler:
            // nicht als "bereit" stehen lassen, sondern per Watchdog erneut versuchen.
            var first = _state != DeviceState.Unreachable;
            if (ex is HidppErrorException)
                Log.Log(first ? LogLevel.Warning : LogLevel.Debug, "{Device}: Erkennung fehlgeschlagen ({Reason}): {Error}", _name, reason, ex.Message);
            else
                Log.Log(first ? LogLevel.Error : LogLevel.Debug, ex, "{Device}: Konfiguration fehlgeschlagen ({Reason})", _name, reason);
            MarkUnreachable($"Konfiguration fehlgeschlagen: {ex.Message}");
        }
        finally
        {
            _configLock.Release();
        }
    }

    /// <summary>Watchdog und Akku-Abfrage nach jeder (auch teilweise) erfolgreichen Konfiguration scharf schalten.</summary>
    private void ArmTimers(DeviceConfiguration config)
    {
        var now = DateTime.UtcNow;
        _nextWatchdog = now + _host.Options.WatchdogInterval;
        _nextBatteryPoll = now + config.BatteryPollInterval;
    }

    private async Task LoadIdentityAsync(CancellationToken ct)
    {
        var features = await _device.EnumerateFeaturesAsync(ct).ConfigureAwait(false);
        _name = await Try(() => DeviceIdentity.GetNameAsync(_device, ct)).ConfigureAwait(false) ?? _endpoint.DisplayName;
        try
        {
            _kind = await DeviceIdentity.GetKindAsync(_device, ct).ConfigureAwait(false);
        }
        catch (HidppErrorException)
        {
            _kind = DeviceKind.Unknown;
        }
        _unitId = (await Try(() => DeviceIdentity.GetFirmwareInfoAsync(_device, ct)).ConfigureAwait(false))?.UnitIdText;

        _reprog = await ReprogControlsV4Feature.TryCreateAsync(_device, ct).ConfigureAwait(false);
        _controls = _reprog is null ? [] : await _reprog.GetAllControlsAsync(ct).ConfigureAwait(false);
        _battery = await BatteryFeature.DetectAsync(_device, ct).ConfigureAwait(false);
        _dpiFeature = await AdjustableDpiFeature.TryCreateAsync(_device, ct).ConfigureAwait(false);
        if (_dpiFeature is not null)
        {
            try
            {
                _supportedDpi = await _dpiFeature.GetDpiListAsync(0, ct).ConfigureAwait(false);
            }
            catch (HidppErrorException)
            {
                _supportedDpi = [];
            }
        }
        _wirelessIndex = features.FirstOrDefault(f => f.FeatureId == FeatureIds.WirelessDeviceStatus)?.Index;
        _identityLoaded = true;

        Log.LogInformation(
            "{Device} erkannt: {Connection}, HID++ {Protocol}, Typ {Kind}, Unit {Unit}, {Features} Features, Tasten [{Controls}], Akku {Battery}, DPI {DpiRange}",
            _name, Connection, _protocol, _kind, _unitId, features.Count,
            string.Join(", ", _controls.Select(c => $"{ControlIds.Format(c.ControlId)}{(c.IsDivertable ? "*" : "")}")),
            _battery?.Source.ToString() ?? "–",
            _supportedDpi.Count > 0 ? $"{_supportedDpi[0]}–{_supportedDpi[^1]}" : "–");
    }

    /// <summary>
    /// Pro Taste den aktuellen Zustand lesen und nur Nötiges schreiben: gewünschte Umleitungen setzen,
    /// fremde/verwaiste Umleitungen (z.B. von Options+) aufheben, Analytics-Events abschalten.
    /// </summary>
    private async Task ApplyReportingAsync(DeviceConfiguration config, CancellationToken ct)
    {
        var failed = new HashSet<ushort>();
        if (_reprog is null)
        {
            foreach (var cid in config.DivertControls) failed.Add(cid);
            _failed = failed;
            return;
        }

        foreach (var control in _controls)
        {
            var cid = control.ControlId;
            var wanted = config.DivertControls.Contains(cid);
            var analyticsCapable = control.Flags.HasFlag(ControlFlags.AnalyticsKeyEvents);
            if (!control.IsDivertable && !analyticsCapable)
            {
                if (wanted) failed.Add(cid);
                continue;
            }

            ControlReporting current;
            try
            {
                current = await _reprog.GetReportingAsync(cid, ct).ConfigureAwait(false);
            }
            catch (HidppErrorException)
            {
                if (wanted) failed.Add(cid);
                continue;
            }

            try
            {
                if (wanted && !control.IsDivertable)
                {
                    failed.Add(cid);
                }
                else if (wanted)
                {
                    var raw = config.RawXYControls.Contains(cid) && control.SupportsRawXY;
                    if (!current.Diverted || current.RawXY != raw || current.PersistentlyDiverted)
                    {
                        var result = await _reprog.SetReportingAsync(cid,
                            ReportingFlags.DivertValid | ReportingFlags.Divert | ReportingFlags.PersistValid | ReportingFlags.RawXYValid |
                            (raw ? ReportingFlags.RawXY : 0), 0, ct).ConfigureAwait(false);
                        if (!result.Diverted)
                        {
                            failed.Add(cid);
                            continue;
                        }
                    }
                    lock (_stateLock)
                    {
                        _diverted.Add(cid);
                        if (raw) _rawXY.Add(cid);
                        else _rawXY.Remove(cid);
                    }
                }
                else if (config.ClearForeignDiversions && control.IsDivertable &&
                         (current.Diverted || current.PersistentlyDiverted || current.RawXY || current.ForceRawXY))
                {
                    await _reprog.ClearAllDiversionAsync(cid, ct).ConfigureAwait(false);
                    lock (_stateLock)
                    {
                        _diverted.Remove(cid);
                        _rawXY.Remove(cid);
                    }
                    Log.LogInformation("{Device}: fremde Umleitung von {Cid} aufgehoben ({State})", _name, ControlIds.Format(cid), current.StateText);
                }

                if (config.DisableAnalytics && current.AnalyticsKeyEvents)
                    await _reprog.SetAnalyticsReportingAsync(cid, false, ct).ConfigureAwait(false);
            }
            catch (HidppErrorException ex)
            {
                if (wanted) failed.Add(cid);
                Log.LogWarning("{Device}: Reporting für {Cid} nicht setzbar: {Error}", _name, ControlIds.Format(cid), ex.Message);
            }
        }

        lock (_stateLock)
        {
            foreach (var cid in config.DivertControls.Where(c => _controls.All(x => x.ControlId != c)))
                _diverted.Remove(cid); // Taste gibt es auf diesem Gerät nicht – kein Fehler
            _failed = failed;
        }
    }

    private async Task ApplyDpiAsync(DeviceSettings settings, CancellationToken ct)
    {
        if (_dpiFeature is null) return;
        var state = await _dpiFeature.GetDpiAsync(0, ct).ConfigureAwait(false);
        _dpi = state.CurrentDpi;
        var target = _sessionDpi ?? settings.Dpi;
        if (target is not { } t) return;
        var snapped = AdjustableDpiFeature.Snap(t, _supportedDpi);
        if (snapped == _dpi) return;
        _dpi = await _dpiFeature.SetDpiAsync(snapped, 0, ct).ConfigureAwait(false);
        Log.LogInformation("{Device}: DPI {Old} → {New}", _name, state.CurrentDpi, _dpi);
    }

    private async Task ReadBatteryAsync(CancellationToken ct)
    {
        if (_battery is null) return;
        try
        {
            UpdateBattery(await _battery.ReadAsync(ct).ConfigureAwait(false), publish: false);
        }
        catch (HidppErrorException ex)
        {
            Log.LogDebug("{Device}: Akku nicht lesbar: {Error}", _name, ex.Message);
        }
    }

    private async Task PollBatteryAsync(CancellationToken ct)
    {
        await _configLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state != DeviceState.Ready) return;
            await ReadBatteryAsync(ct).ConfigureAwait(false);
            Publish();
        }
        catch (HidppException ex) when (ex is HidppTimeoutException or HidppTransportException)
        {
            MarkUnreachable("Akku-Abfrage ohne Antwort");
        }
        finally
        {
            _configLock.Release();
        }
    }

    // ------------------------------------------------------------------ Watchdog

    private async Task WatchdogAsync(CancellationToken ct)
    {
        if (_state != DeviceState.Ready)
        {
            await ConfigureAsync("Watchdog: erneuter Versuch", ct).ConfigureAwait(false);
            return;
        }

        string? problem = null;
        var unreachable = false;
        await _configLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_reprog is not null)
            {
                foreach (var cid in _diverted.ToList())
                {
                    var r = await _reprog.GetReportingAsync(cid, ct).ConfigureAwait(false);
                    if (!r.Diverted || r.RawXY != _rawXY.Contains(cid))
                    {
                        problem = $"Umleitung von {ControlIds.Format(cid)} war weg ({r.StateText})";
                        break;
                    }
                }
            }

            if (problem is null && _dpiFeature is not null)
            {
                var target = _sessionDpi ?? _host.Configuration.SettingsFor(_name, ProductId, _unitId).Dpi;
                if (target is { } t)
                {
                    var current = (await _dpiFeature.GetDpiAsync(0, ct).ConfigureAwait(false)).CurrentDpi;
                    if (current != AdjustableDpiFeature.Snap(t, _supportedDpi)) problem = $"DPI war {current} statt {t}";
                }
            }
        }
        catch (HidppException ex) when (ex is HidppTimeoutException or HidppTransportException)
        {
            unreachable = true;
        }
        catch (HidppErrorException ex)
        {
            Log.LogDebug("{Device}: Watchdog-Fehler: {Error}", _name, ex.Message);
        }
        finally
        {
            _configLock.Release();
        }

        if (unreachable)
        {
            MarkUnreachable("Watchdog ohne Antwort");
            return;
        }

        if (problem is not null)
        {
            Log.LogWarning("{Device}: {Problem} – wird neu gesetzt (läuft Options+?)", _name, problem);
            await ConfigureAsync("Watchdog", ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ Events vom Gerät (Lese-Thread!)

    private void OnNotification(HidppDevice device, HidppMessage m, long timestamp)
    {
        try
        {
            if (_reprog is { } reprog)
            {
                if (ReprogControlsV4Feature.TryParseDivertedButtons(m, reprog.FeatureIndex, out var pressed))
                {
                    HandleButtons(pressed, timestamp);
                    return;
                }
                if (ReprogControlsV4Feature.TryParseRawXY(m, reprog.FeatureIndex, out var dx, out var dy))
                {
                    _host.OnRawXY(new RawXYEvent(Key, dx, dy, timestamp));
                    return;
                }
                if (HidppEvents.IsEvent(m, reprog.FeatureIndex, ReprogControlsV4Feature.EventAnalytics)) return;
            }

            if (_battery is { } battery && battery.TryDecodeEvent(m, out var reading))
            {
                UpdateBattery(reading, publish: true);
                return;
            }

            if (_wirelessIndex is { } wi && WirelessStatusEvent.TryParse(m, wi, out var status))
            {
                Log.LogInformation("{Device}: Wireless-Status {Status}", _name, status);
                ReleaseAllButtons(timestamp);
                RequestConfigure(TimeSpan.FromMilliseconds(250), "Reconnect (0x1D4B)");
                return;
            }

            if (m.SoftwareId == 0 && _state != DeviceState.Ready)
                RequestConfigure(TimeSpan.FromMilliseconds(200), "Lebenszeichen vom Gerät");
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "{Device}: Fehler beim Verarbeiten von [{Message}]", _name, m);
        }
    }

    private void HandleButtons(ushort[] pressed, long timestamp)
    {
        var events = new List<ButtonEvent>(2);
        lock (_stateLock)
        {
            var now = pressed.ToHashSet();
            foreach (var cid in _pressed)
                if (!now.Contains(cid)) events.Add(new ButtonEvent(Key, cid, false, timestamp));
            foreach (var cid in now)
                if (!_pressed.Contains(cid)) events.Add(new ButtonEvent(Key, cid, true, timestamp));
            _pressed = now;
        }
        foreach (var e in events) _host.OnButton(e);
    }

    public void ReleaseAllButtons(long timestamp)
    {
        List<ushort> released;
        lock (_stateLock)
        {
            released = [.. _pressed];
            _pressed = [];
        }
        foreach (var cid in released) _host.OnButton(new ButtonEvent(Key, cid, false, timestamp));
    }

    private void UpdateBattery(BatteryReading reading, bool publish)
    {
        lock (_stateLock)
        {
            _batteryReading = reading;
            _batteryTime = DateTimeOffset.Now;
            _nextBatteryPoll = DateTime.UtcNow + _host.Configuration.BatteryPollInterval;
        }
        if (publish) Publish();
    }

    // ------------------------------------------------------------------ Zustand

    public void MarkUnreachable(string reason)
    {
        var wasReachable = _state != DeviceState.Unreachable;
        SetState(DeviceState.Unreachable);
        _nextWatchdog = DateTime.UtcNow + _host.Options.WatchdogInterval;
        ReleaseAllButtons(System.Diagnostics.Stopwatch.GetTimestamp());
        if (wasReachable)
        {
            Log.LogInformation("{Device} nicht erreichbar: {Reason}", _name, reason);
            Publish();
        }
    }

    private void SetState(DeviceState state) => _state = state;

    public async Task<int?> SetDpiAsync(int dpi, bool rememberForSession, CancellationToken ct)
    {
        if (_dpiFeature is null) return null;
        await _configLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapped = AdjustableDpiFeature.Snap(dpi, _supportedDpi);
            _dpi = await _dpiFeature.SetDpiAsync(snapped, 0, ct).ConfigureAwait(false);
            if (rememberForSession) _sessionDpi = snapped;
            Log.LogInformation("{Device}: DPI auf {Dpi} gesetzt", _name, _dpi);
        }
        finally
        {
            _configLock.Release();
        }
        Publish();
        return _dpi;
    }

    public int? CurrentDpi => _dpi;
    public bool HasDpi => _dpiFeature is not null;

    /// <summary>Beim Beenden: eigene Umleitungen aufheben, damit die Tasten wieder nativ funktionieren.</summary>
    public async Task ResetAllAsync(CancellationToken ct)
    {
        await _configLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ResetDiversionsAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _configLock.Release();
        }
    }

    /// <summary>Wie <see cref="ResetAllAsync"/>, aber der Aufrufer hält _configLock bereits.</summary>
    private async Task ResetDiversionsAsync(CancellationToken ct)
    {
        if (_reprog is null) return;
        foreach (var cid in _diverted.ToList())
        {
            try
            {
                await _reprog.SetDivertAsync(cid, false, _rawXY.Contains(cid) ? false : null, ct).ConfigureAwait(false);
            }
            catch (HidppException)
            {
                // Gerät weg/schläft – temporäre Umleitungen verfallen dort ohnehin beim Reconnect
            }
        }
        lock (_stateLock)
        {
            _diverted.Clear();
            _rawXY.Clear();
        }
    }

    public DeviceSnapshot Snapshot()
    {
        lock (_stateLock)
        {
            return new DeviceSnapshot
            {
                Key = Key,
                Name = _name,
                ProductId = ProductId,
                UnitId = _unitId,
                Connection = Connection,
                State = _state,
                Kind = _kind,
                Protocol = _protocol,
                Battery = _batteryReading,
                BatteryTimestamp = _batteryTime,
                Dpi = _dpi,
                SupportedDpi = _supportedDpi,
                Controls = _controls,
                DivertedControls = new HashSet<ushort>(_diverted),
                FailedControls = new HashSet<ushort>(_failed),
            };
        }
    }

    private void Publish()
    {
        if (!IsDisposed) _host.OnSnapshot(this, Snapshot());
    }

    private static string Format(IEnumerable<ushort> cids) => string.Join(", ", cids.Select(ControlIds.Format));

    private static async Task<T?> Try<T>(Func<Task<T?>> action) where T : class
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (HidppErrorException)
        {
            return null;
        }
    }

    private static uint StableHash(string s)
    {
        var hash = 2166136261u;
        foreach (var c in s.ToUpperInvariant()) hash = (hash ^ c) * 16777619u;
        return hash;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _device.Notification -= OnNotification;
        _device.Dispose();
    }
}

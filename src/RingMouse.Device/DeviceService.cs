using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RingMouse.HidPlusPlus;
using RingMouse.HidPlusPlus.Discovery;
using RingMouse.HidPlusPlus.Receivers;
using RingMouse.HidPlusPlus.Transport;

namespace RingMouse.Device;

/// <summary>Ein geöffneter Endpoint (direktes Gerät oder Receiver) mit seinen logischen Geräten.</summary>
internal sealed class EndpointSession(HidppEndpoint endpoint, HidppChannel channel) : IDisposable
{
    public HidppEndpoint Endpoint { get; } = endpoint;
    public HidppChannel Channel { get; } = channel;
    public HidppReceiver? Receiver { get; set; }
    public ConcurrentDictionary<byte, ManagedDevice> Devices { get; } = new();
    public DateTime OpenedAt { get; } = DateTime.UtcNow;

    public void Dispose()
    {
        // Kanal zuerst: offene Anfragen scheitern dann sauber mit HidppTransportException.
        Channel.Dispose();
        foreach (var d in Devices.Values) d.Dispose();
        Devices.Clear();
        Receiver?.Dispose();
    }
}

/// <summary>
/// Verwaltet alle Logitech-HID++-Geräte: findet sie (beim Start, bei HID-Arrival, periodisch), öffnet Kanäle,
/// erkennt Receiver, hält jedes Gerät konfiguriert (auch nach Reconnect/Standby/Entsperren) und meldet
/// Tasten-, Raw-XY- und Zustandsereignisse.
/// </summary>
public sealed class DeviceService : IDeviceHost, IAsyncDisposable
{
    private readonly IHidTransport _transport;
    private readonly IHidDeviceWatcher? _watcher;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, EndpointSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _retryAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _quickCloses = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _rescanLock = new();
    private readonly ConcurrentQueue<Action> _events = new();
    private int _eventsDraining;
    private DateTime _lastLoopError = DateTime.MinValue;
    private DateTime _nextRescan = DateTime.MinValue;
    private string _rescanReason = "Start";
    private Task? _loop;

    // Wächter der Hauptschleife (eigener Thread, unabhängig von Threadpool und .NET-Timern)
    private long _loopBeat = Environment.TickCount64;
    private volatile string _loopStep = "Start";
    private CancellationTokenSource? _iteration;
    private int _loopGeneration;
    private Thread? _loopWatchdog;
    private Task<(IReadOnlyList<HidDeviceInfo> Collections, IReadOnlyList<HidppEndpoint> Endpoints)>? _enumeration;

    // Diagnose: damit ein verlorenes Gerät im Log nachvollziehbar ist
    private DateTime _lastScan = DateTime.MinValue;
    private DateTime _lastArrival = DateTime.MinValue;
    private DateTime _lastArrivalLog = DateTime.MinValue;
    private DateTime _lastStatusLog = DateTime.UtcNow;
    private DateTime _lastMissingLog = DateTime.MinValue;
    private DateTime? _missingSince;
    private volatile DeviceConfiguration _configuration = DeviceConfiguration.Empty;
    private int _stopped;

    public DeviceService(IHidTransport transport, IHidDeviceWatcher? watcher, ILogger? logger = null, DeviceServiceOptions? options = null)
    {
        _transport = transport;
        _watcher = watcher;
        _logger = logger ?? NullLogger.Instance;
        Options = options ?? new DeviceServiceOptions();
    }

    public DeviceServiceOptions Options { get; }
    public DeviceConfiguration Configuration => _configuration;
    ILogger IDeviceHost.Logger => _logger;
    CancellationToken IDeviceHost.Stopping => _cts.Token;

    /// <summary>Taste gedrückt/losgelassen – wird auf dem HID-Lese-Thread ausgelöst (schnell zurückkehren!).</summary>
    public event Action<ButtonEvent>? ButtonChanged;

    /// <summary>Raw-XY-Bewegung einer umgeleiteten Taste – Lese-Thread.</summary>
    public event Action<RawXYEvent>? RawXY;

    /// <summary>Zustand/Akku/DPI eines Geräts hat sich geändert – Threadpool.</summary>
    public event Action<DeviceSnapshot>? DeviceChanged;

    /// <summary>Gerät ist verschwunden (Endpoint entfernt).</summary>
    public event Action<DeviceSnapshot>? DeviceRemoved;

    /// <summary>Alle HID++-Rohframes (für das Raw-Log).</summary>
    public event FrameTraceHandler? FrameTraced;

    public string? LastActiveDeviceKey { get; private set; }

    public IReadOnlyList<DeviceSnapshot> Devices =>
        _sessions.Values.SelectMany(s => s.Devices.Values).Select(d => d.Snapshot()).ToList();

    public void Start()
    {
        if (_loop is not null) return;
        RequestRescan("Start", TimeSpan.Zero);
        Beat("Start");
        _loop = Task.Run(() => LoopAsync(0, _cts.Token));
        _loopWatchdog = new Thread(WatchdogThread) { IsBackground = true, Name = "RingMouse Geräte-Wächter" };
        _loopWatchdog.Start();
        // Im Hintergrund anmelden: CM_Register_Notification darf den Aufrufer (UI-Thread) nie aufhalten
        if (_watcher is not null) _ = Task.Run(StartWatcher);
    }

    private void StartWatcher()
    {
        _watcher!.InterfaceArrived += _ => OnInterfaceArrived();
        _watcher.InterfaceRemoved += path => OnInterfaceRemoved(path);
        try
        {
            _watcher.Start();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Geräte-Benachrichtigungen nicht verfügbar – nur periodischer Scan");
        }
    }

    private void OnInterfaceArrived()
    {
        _lastArrival = DateTime.Now;
        // Ohne verbundenes Gerät sichtbar protokollieren (höchstens alle 10 s – Anmeldungen kommen gebündelt)
        if (_sessions.IsEmpty && DateTime.UtcNow - _lastArrivalLog > TimeSpan.FromSeconds(10))
        {
            _lastArrivalLog = DateTime.UtcNow;
            _logger.LogInformation("HID-Gerät angemeldet – suche Logitech-Geräte");
        }
        RequestRescan("HID-Gerät angemeldet", TimeSpan.FromMilliseconds(800));
    }

    private void Beat(string step)
    {
        _loopStep = step;
        Volatile.Write(ref _loopBeat, Environment.TickCount64);
    }

    /// <summary>
    /// Wächter-Thread: steht die Hauptschleife länger als 45 s (Wartevorgang ohne Zeitlimit, hängender Treiber,
    /// stehender Timer), wird der Durchlauf abgebrochen und die Schleife notfalls neu gestartet – ein verlorenes Gerät
    /// soll nie bis zum nächsten Programmstart verloren bleiben.
    /// </summary>
    private void WatchdogThread()
    {
        var stop = _cts.Token.WaitHandle;
        var stallMs = (long)Options.LoopStallTimeout.TotalMilliseconds;
        var check = TimeSpan.FromMilliseconds(Math.Clamp(stallMs / 4, 50, 10_000));
        while (!stop.WaitOne(check))
        {
            var idle = Environment.TickCount64 - Volatile.Read(ref _loopBeat);
            if (idle < stallMs) continue;

            var step = _loopStep;
            _logger.LogError("Geräteverwaltung hängt seit {Seconds} s bei \"{Step}\" – Durchlauf wird abgebrochen", idle / 1000, step);
            try
            {
                Volatile.Read(ref _iteration)?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Durchlauf gerade fertig geworden
            }

            if (stop.WaitOne(check)) return;
            if (Environment.TickCount64 - Volatile.Read(ref _loopBeat) < stallMs) continue; // hat sich gefangen

            var generation = Interlocked.Increment(ref _loopGeneration);
            Beat("Neustart der Schleife");
            _loop = Task.Run(() => LoopAsync(generation, _cts.Token));
            _logger.LogWarning("Geräteverwaltung: Schleife neu gestartet (hing bei \"{Step}\")", step);
        }
    }

    public void UpdateConfiguration(DeviceConfiguration configuration)
    {
        _configuration = configuration;
        foreach (var d in AllDevices()) d.RequestConfigure(TimeSpan.Zero, "Config geändert");
    }

    /// <summary>Alle Geräte neu konfigurieren, z.B. nach Standby oder Entsperren (mehrere Versuche, BLE braucht Zeit).</summary>
    public void RequestReconfigureAll(string reason, params TimeSpan[] delays)
    {
        if (delays.Length == 0) delays = [TimeSpan.Zero];
        foreach (var d in AllDevices())
            foreach (var delay in delays)
                d.RequestConfigure(delay, reason);
        RequestRescan(reason, delays.Min());
    }

    public void RequestRescan(string reason, TimeSpan delay)
    {
        lock (_rescanLock)
        {
            var due = DateTime.UtcNow + delay;
            if (due < _nextRescan)
            {
                _nextRescan = due;
                _rescanReason = reason;
            }
        }
    }

    /// <summary>
    /// DPI setzen. Läuft im Threadpool: das synchrone WriteFile kann bei schlafendem Gerät bis zu 1,5 s dauern
    /// und darf den UI-Thread nicht blockieren.
    /// </summary>
    public Task<int?> SetDpiAsync(string? deviceKey, int dpi, CancellationToken ct = default) =>
        Task.Run(() => SetDpiCoreAsync(deviceKey, dpi, ct), ct);

    private async Task<int?> SetDpiCoreAsync(string? deviceKey, int dpi, CancellationToken ct)
    {
        int? result = null;
        foreach (var d in TargetDevices(deviceKey))
        {
            try
            {
                result = await d.SetDpiAsync(dpi, rememberForSession: true, ct).ConfigureAwait(false) ?? result;
            }
            catch (HidppException ex)
            {
                _logger.LogWarning("{Device}: DPI nicht setzbar: {Error}", d.Name, ex.Message);
            }
        }
        return result;
    }

    /// <summary>Schaltet zum nächsten Wert der Liste (bezogen auf den aktuellen DPI-Wert). Läuft im Threadpool.</summary>
    public Task<int?> CycleDpiAsync(string? deviceKey, IReadOnlyList<int> values, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            if (values.Count == 0) return null;
            var device = TargetDevices(deviceKey).FirstOrDefault();
            if (device is null) return null;
            var current = device.CurrentDpi ?? values[0];
            var index = values.Select((v, i) => (v, i)).MinBy(p => Math.Abs(p.v - current)).i;
            var next = values.Count == 1 ? values[0] : values[(index + 1) % values.Count];
            return await SetDpiCoreAsync(device.Key, next, ct).ConfigureAwait(false);
        }, ct);

    private IEnumerable<ManagedDevice> TargetDevices(string? deviceKey)
    {
        var all = AllDevices().Where(d => d.HasDpi && d.State == DeviceState.Ready).ToList();
        var key = deviceKey ?? LastActiveDeviceKey;
        var match = all.Where(d => d.Key == key).ToList();
        return match.Count > 0 ? match : all.Take(1);
    }

    private IEnumerable<ManagedDevice> AllDevices() => _sessions.Values.SelectMany(s => s.Devices.Values);

    // ------------------------------------------------------------------ Hauptschleife

    private async Task LoopAsync(int generation, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Options.TickInterval);
        try
        {
            do
            {
                if (generation != Volatile.Read(ref _loopGeneration)) return; // vom Wächter ersetzt
                using var iteration = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Volatile.Write(ref _iteration, iteration);
                try
                {
                    await RunOnceAsync(iteration.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning("Geräteverwaltung: Durchlauf abgebrochen (hing bei \"{Step}\")", _loopStep);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Ein Fehler in einem Durchlauf darf die Geräteverwaltung nie dauerhaft beenden.
                    LogLoopError(ex);
                }
                finally
                {
                    Interlocked.CompareExchange(ref _iteration, null, iteration);
                }
                Beat("Warte auf nächsten Takt");
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Beenden
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        Beat("Takt");
        var now = DateTime.UtcNow;
        string? reason = null;
        lock (_rescanLock)
        {
            if (now >= _nextRescan)
            {
                reason = _rescanReason;
                _nextRescan = now + Options.RescanInterval;
                _rescanReason = "periodischer Scan";
            }
        }
        if (reason is not null) await RescanAsync(reason, ct).ConfigureAwait(false);

        Beat("Geräte-Takt");
        if (now - _lastStatusLog >= TimeSpan.FromMinutes(30))
        {
            _lastStatusLog = now;
            var devices = AllDevices().Select(d => $"{d.Name}: {d.State}").ToList();
            _logger.LogInformation("Status: {Count} Gerät(e) [{Devices}], letzter Scan {Scan:HH:mm:ss}, letzte HID-Anmeldung {Arrival:HH:mm:ss}",
                devices.Count, string.Join(", ", devices), _lastScan.ToLocalTime(), _lastArrival);
        }

        foreach (var d in AllDevices())
        {
            try
            {
                d.Tick(now, ct);
            }
            catch (Exception ex)
            {
                LogLoopError(ex);
            }
        }
    }

    /// <summary>Höchstens eine Fehlermeldung pro Minute, damit ein wiederkehrender Fehler das Log nicht flutet.</summary>
    private void LogLoopError(Exception ex)
    {
        var now = DateTime.UtcNow;
        var level = now - _lastLoopError >= TimeSpan.FromMinutes(1) ? LogLevel.Error : LogLevel.Debug;
        if (level == LogLevel.Error) _lastLoopError = now;
        _logger.Log(level, ex, "Fehler in der Geräteverwaltung – läuft weiter");
    }

    private async Task RescanAsync(string reason, CancellationToken ct)
    {
        Beat("HID-Scan");
        IReadOnlyList<HidDeviceInfo> collections;
        IReadOnlyList<HidppEndpoint> endpoints;
        try
        {
            if (await EnumerateAsync(ct).ConfigureAwait(false) is not { } found) return;
            (collections, endpoints) = found;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "HID-Enumeration fehlgeschlagen");
            return;
        }
        _lastScan = DateTime.UtcNow;
        ReportMissing(collections, endpoints);

        var present = endpoints.Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, session) in _sessions.ToArray())
        {
            if (!present.Contains(key)) RemoveSession(key, "nicht mehr vorhanden", session);
            else if (session.Channel.IsClosed) RemoveSession(key, "Kanal geschlossen", session); // tote Sitzung → neu öffnen
        }

        foreach (var endpoint in endpoints)
        {
            if (_sessions.ContainsKey(endpoint.Key)) continue;
            if (_retryAfter.TryGetValue(endpoint.Key, out var retry) && DateTime.UtcNow < retry) continue;
            _logger.LogDebug("Öffne {Device} ({Reason})", endpoint.DisplayName, reason);
            try
            {
                Beat($"Öffne {endpoint.DisplayName}");
                await OpenSessionAsync(endpoint, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "{Device}: Öffnen fehlgeschlagen – neuer Versuch in 30 s", endpoint.DisplayName);
                RemoveSession(endpoint.Key, "Fehler beim Öffnen");
                _retryAfter[endpoint.Key] = DateTime.UtcNow.AddSeconds(30);
            }
        }
    }

    /// <summary>
    /// HID-Enumeration im Hintergrund mit Zeitlimit: ein Treiber, der eine Abfrage nicht beantwortet (z.B. Bluetooth-Gerät
    /// mitten im Verbindungsaufbau), blockiert so nie die Schleife. Läuft eine hängende Enumeration noch, wird nicht gescannt.
    /// </summary>
    private async Task<(IReadOnlyList<HidDeviceInfo> Collections, IReadOnlyList<HidppEndpoint> Endpoints)?> EnumerateAsync(CancellationToken ct)
    {
        if (_enumeration is { IsCompleted: false })
        {
            _logger.LogDebug("HID-Enumeration hängt noch – Scan übersprungen");
            return null;
        }
        var task = Task.Run(() =>
        {
            var collections = _transport.Enumerate(HidppDiscovery.LogitechVendorId);
            return (collections, HidppDiscovery.FindEndpoints(collections));
        });
        _enumeration = task;
        try
        {
            return await task.WaitAsync(Options.EnumerationTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("HID-Enumeration hängt seit {Seconds} s (Treiber antwortet nicht) – nächster Versuch später",
                Options.EnumerationTimeout.TotalSeconds);
            return null;
        }
    }

    /// <summary>Protokolliert, wenn kein HID++-Gerät (mehr) verbunden ist – mit dem, was der Scan stattdessen sieht.</summary>
    private void ReportMissing(IReadOnlyList<HidDeviceInfo> collections, IReadOnlyList<HidppEndpoint> endpoints)
    {
        if (endpoints.Count > 0 || !_sessions.IsEmpty)
        {
            if (_missingSince is { } since && endpoints.Count > 0)
                _logger.LogInformation("Logitech-HID++-Gerät wieder gefunden (fehlte seit {Since:HH:mm:ss})", since.ToLocalTime());
            _missingSince = null;
            return;
        }
        var now = DateTime.UtcNow;
        _missingSince ??= now;
        if (now - _lastMissingLog < TimeSpan.FromMinutes(10)) return;
        _lastMissingLog = now;
        var seen = collections.Count == 0
            ? "keine Logitech-Collections"
            : string.Join("; ", collections.Select(c => $"{c.CollectionTag} UP 0x{c.UsagePage:X4} In {c.InputReportLength} IDs [{string.Join(",", c.InputReportIds.Select(i => i.ToString("X2")))}]"));
        _logger.LogInformation("Kein Logitech-HID++-Gerät verbunden (seit {Since:HH:mm:ss}) – Scan sieht: {Seen}", _missingSince.Value.ToLocalTime(), seen);
    }

    private async Task OpenSessionAsync(HidppEndpoint endpoint, CancellationToken ct)
    {
        HidppChannel channel;
        // Öffnen (CreateFile + Puffer-IOCTL) ebenfalls mit Zeitlimit – bei hängendem Treiber später erneut
        var open = Task.Run(() => endpoint.Open(_transport, start: false));
        try
        {
            channel = await open.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Kommt das Öffnen doch noch zurück, den Kanal wieder schließen
            _ = open.ContinueWith(t => t.Result.Dispose(), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            if (ex is OperationCanceledException) throw;
            _logger.LogWarning("{Device}: Öffnen hängt (Treiber antwortet nicht) – neuer Versuch in 30 s", endpoint.DisplayName);
            _retryAfter[endpoint.Key] = DateTime.UtcNow.AddSeconds(30);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("{Device}: HID++-Collection nicht zu öffnen ({Error}) – neuer Versuch in 30 s", endpoint.DisplayName, ex.Message);
            _retryAfter[endpoint.Key] = DateTime.UtcNow.AddSeconds(30);
            return;
        }

        // Erst Handler anmelden und die Sitzung eintragen, dann die Lese-Threads starten: ein sofortiges Closed
        // (Gerät startet neu, BLE-Verbindung flattert) ginge sonst verloren und die tote Sitzung bliebe stehen.
        var session = new EndpointSession(endpoint, channel);
        channel.FrameTraced += ForwardFrame;
        channel.Closed += (_, error) => OnChannelClosed(endpoint.Key, session, error);
        _sessions[endpoint.Key] = session;
        _retryAfter.TryRemove(endpoint.Key, out _);
        channel.Start();
        if (channel.IsClosed) return; // gleich wieder zu – OnChannelClosed hat aufgeräumt

        Beat($"Erkenne {endpoint.DisplayName}");
        EndpointIdentity identity;
        try
        {
            identity = await HidppDiscovery.IdentifyAsync(channel, endpoint.Bus, Options.SoftwareId, ct).ConfigureAwait(false);
        }
        catch (HidppException)
        {
            identity = EndpointIdentity.None;
        }

        if (identity.IsReceiver)
        {
            var receiver = new HidppReceiver(channel, Options.SoftwareId);
            session.Receiver = receiver;
            channel.MessageReceived += (_, m, _) => OnReceiverMessage(session, m);
            try
            {
                await receiver.EnableWirelessNotificationsAsync(ct).ConfigureAwait(false);
            }
            catch (HidppException ex)
            {
                _logger.LogWarning("{Receiver}: Wireless-Notifications nicht aktivierbar: {Error}", endpoint.DisplayName, ex.Message);
            }
            foreach (var slot in identity.Slots)
            {
                var device = AddDevice(session, slot.DeviceIndex, $"{endpoint.BusText}-Receiver, Index {slot.DeviceIndex}", endpoint.ProductId);
                device.RequestConfigure(TimeSpan.Zero, "am Receiver gefunden");
            }
            try
            {
                await receiver.TriggerConnectionNotificationsAsync(ct).ConfigureAwait(false);
            }
            catch (HidppException)
            {
                // nicht kritisch
            }
            _logger.LogInformation("Receiver {Receiver} (PID {Pid:X4}) geöffnet, {Count} Gerät(e) antworten", endpoint.DisplayName,
                endpoint.ProductId, identity.Slots.Count(s => s.Protocol is not null));
        }
        else
        {
            var device = AddDevice(session, HidppMessage.DirectDeviceIndex, endpoint.BusText, endpoint.ProductId);
            device.RequestConfigure(TimeSpan.Zero, identity.Responds ? "Gerät gefunden" : "Gerät gefunden (antwortet noch nicht)");
            _logger.LogInformation("{Device} ({Bus}, PID {Pid:X4}) geöffnet{Sleep}", endpoint.DisplayName, endpoint.BusText,
                endpoint.ProductId, identity.Responds ? "" : " – antwortet noch nicht (schläft?)");
        }
    }

    private ManagedDevice AddDevice(EndpointSession session, byte index, string connection, ushort productId) =>
        session.Devices.GetOrAdd(index, i =>
            new ManagedDevice(this, session.Endpoint, new HidppDevice(session.Channel, i, Options.SoftwareId), connection, productId));

    private void OnReceiverMessage(EndpointSession session, HidppMessage message)
    {
        if (!HidppReceiver.TryParseConnectionEvent(message, out var connection)) return;
        _logger.LogInformation("{Receiver}: {Connection}", session.Endpoint.DisplayName, connection);
        if (connection.Connected && connection.LinkEstablished)
        {
            var device = AddDevice(session, connection.DeviceIndex,
                $"{session.Endpoint.BusText}-Receiver, Index {connection.DeviceIndex}", connection.WirelessPid != 0 ? connection.WirelessPid : session.Endpoint.ProductId);
            device.RequestConfigure(TimeSpan.FromMilliseconds(300), "Receiver: verbunden");
        }
        else if (connection.Connected && session.Devices.TryGetValue(connection.DeviceIndex, out var sleeping))
        {
            sleeping.MarkUnreachable("Funkverbindung zum Receiver getrennt");
        }
        else if (!connection.Connected && session.Devices.TryRemove(connection.DeviceIndex, out var removed))
        {
            var snapshot = removed.Snapshot();
            removed.Dispose();
            RaiseRemoved(snapshot);
        }
    }

    private void OnInterfaceRemoved(string path)
    {
        foreach (var (key, session) in _sessions)
        {
            if (session.Endpoint.ContainsPath(path))
            {
                RemoveSession(key, "HID-Interface abgemeldet", session);
                break;
            }
        }
        RequestRescan("HID-Gerät abgemeldet", TimeSpan.FromSeconds(2));
    }

    private void OnChannelClosed(string key, EndpointSession session, Exception? error)
    {
        if (Volatile.Read(ref _stopped) != 0) return;
        if (error is not null)
        {
            _logger.LogInformation("Kanal zu {Key} geschlossen: {Error}", key, error.Message);
            // Schließt der Kanal immer wieder kurz nach dem Öffnen, nicht im 2-s-Takt neu öffnen.
            if (DateTime.UtcNow - session.OpenedAt >= TimeSpan.FromSeconds(10)) _quickCloses.TryRemove(key, out _);
            else if (_quickCloses.AddOrUpdate(key, 1, (_, n) => n + 1) >= 3) _retryAfter[key] = DateTime.UtcNow.AddSeconds(30);
        }
        RemoveSession(key, "Kanal geschlossen", session);
        RequestRescan("Kanal geschlossen", TimeSpan.FromSeconds(2));
    }

    /// <param name="expected">Nur diese Sitzung entfernen (nicht eine inzwischen neu geöffnete mit gleichem Schlüssel).</param>
    private void RemoveSession(string key, string reason, EndpointSession? expected = null)
    {
        EndpointSession? session;
        if (expected is not null)
        {
            if (!_sessions.TryRemove(KeyValuePair.Create(key, expected))) return;
            session = expected;
        }
        else if (!_sessions.TryRemove(key, out session))
        {
            return;
        }
        var snapshots = session.Devices.Values.Select(d =>
        {
            d.ReleaseAllButtons(System.Diagnostics.Stopwatch.GetTimestamp());
            return d.Snapshot();
        }).ToList();
        _logger.LogInformation("{Device} entfernt ({Reason})", session.Endpoint.DisplayName, reason);
        try
        {
            session.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fehler beim Schließen von {Device}", session.Endpoint.DisplayName);
        }
        foreach (var s in snapshots) RaiseRemoved(s);
    }

    // ------------------------------------------------------------------ IDeviceHost

    void IDeviceHost.OnButton(ButtonEvent e)
    {
        if (e.IsDown) LastActiveDeviceKey = e.DeviceKey;
        try
        {
            ButtonChanged?.Invoke(e);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler im Tasten-Handler");
        }
    }

    void IDeviceHost.OnRawXY(RawXYEvent e)
    {
        try
        {
            RawXY?.Invoke(e);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler im Raw-XY-Handler");
        }
    }

    void IDeviceHost.OnSnapshot(ManagedDevice source, DeviceSnapshot snapshot) =>
        Post(() =>
        {
            // Nach dem Entfernen keine verspäteten Zustandsmeldungen mehr – sonst taucht das Gerät als Geist wieder auf.
            if (!source.IsDisposed) DeviceChanged?.Invoke(snapshot);
        });

    private void RaiseRemoved(DeviceSnapshot snapshot) => Post(() => DeviceRemoved?.Invoke(snapshot));

    /// <summary>DeviceChanged/DeviceRemoved im Threadpool zustellen – nacheinander und in Auftragsreihenfolge.</summary>
    private void Post(Action raise)
    {
        _events.Enqueue(raise);
        if (Interlocked.CompareExchange(ref _eventsDraining, 1, 0) == 0)
            ThreadPool.QueueUserWorkItem(static service => ((DeviceService)service!).DrainEvents(), this);
    }

    private void DrainEvents()
    {
        while (true)
        {
            while (_events.TryDequeue(out var raise))
            {
                try
                {
                    raise();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Fehler im Geräte-Handler");
                }
            }
            Volatile.Write(ref _eventsDraining, 0);
            // Kam zwischen leerer Queue und Freigabe noch etwas an, selbst weitermachen.
            if (_events.IsEmpty || Interlocked.CompareExchange(ref _eventsDraining, 1, 0) != 0) return;
        }
    }

    private void ForwardFrame(HidppChannel channel, FrameDirection direction, byte[] frame, long timestamp) =>
        FrameTraced?.Invoke(channel, direction, frame, timestamp);

    // ------------------------------------------------------------------ Beenden

    /// <summary>
    /// Hebt alle eigenen Umleitungen auf (Tasten wieder nativ) und schließt die Kanäle. Läuft im Threadpool, damit
    /// ein Aufruf vom UI-Thread dort nicht an synchronen HID-Schreibvorgängen hängt.
    /// </summary>
    public Task StopAsync(TimeSpan timeout) => Task.Run(() => StopCoreAsync(timeout));

    private async Task StopCoreAsync(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _cts.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // egal
            }
        }

        using var resetCts = new CancellationTokenSource(timeout);
        var resets = AllDevices().Select(async d =>
        {
            try
            {
                await d.ResetAllAsync(resetCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Gerät weg – temporäre Umleitungen verfallen beim nächsten Reconnect
            }
        });
        try
        {
            await Task.WhenAll(resets).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch
        {
            // Timeout
        }

        foreach (var key in _sessions.Keys.ToList())
        {
            if (_sessions.TryRemove(key, out var s)) s.Dispose();
        }
        _watcher?.Dispose();
        _logger.LogInformation("DeviceService beendet, Umleitungen zurückgesetzt");
    }

    public async ValueTask DisposeAsync() => await StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
}

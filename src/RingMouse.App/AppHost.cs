using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using RingMouse.Actions;
using RingMouse.App.Battery;
using RingMouse.App.Input;
using RingMouse.App.Logging;
using RingMouse.App.Ring;
using RingMouse.App.Settings;
using RingMouse.App.Tray;
using RingMouse.Core;
using RingMouse.Core.Config;
using RingMouse.Core.Profiles;
using RingMouse.Core.State;
using RingMouse.Device;
using RingMouse.HidPlusPlus.Features;
using RingMouse.HidPlusPlus.Transport.Windows;
using RingMouse.Platform;
using RingMouse.Platform.Autostart;
using RingMouse.Platform.Display;
using RingMouse.Platform.Input;
using RingMouse.Platform.Windows;

namespace RingMouse.App;

/// <summary>Schnittstelle, über die das Einstellungsfenster auf die laufende App zugreift.</summary>
internal interface ISettingsHost
{
    RingMouseConfig CurrentConfig { get; }
    IReadOnlyList<DeviceSnapshot> Devices { get; }
    AutostartService Autostart { get; }
    void SaveConfig(RingMouseConfig config);

    /// <summary>Gespeicherte config.json sichern; liefert den Pfad der Sicherung (null ohne Datei).</summary>
    string? BackupConfig();
    void OpenConfigFile();
    void OpenLogs();
    void OpenConfigFolder();
    void ReconnectAll();
    Task<int?> SetDpiNowAsync(string deviceKey, int dpi);

    /// <summary>Ring kurz in Originalgröße am Mauszeiger zeigen (mit den Einstellungen aus dem Fenster).</summary>
    void PreviewRing(RingDefinition ring, RingSettings settings, bool isSubmenu);
}

/// <summary>Composition Root: verdrahtet Config, Geräte, Ring, Aktionen, Tray und Systemereignisse.</summary>
internal sealed class AppHost : ISettingsHost
{
    private readonly Application _app;
    private readonly string[] _args;
    private readonly Dictionary<string, DeviceSnapshot> _devicesByKey = new();
    private AppLogging _logging = null!;
    private Microsoft.Extensions.Logging.ILogger _log = null!;
    private ConfigStore _configStore = null!;
    private StateStore _state = null!;
    private LowLevelInputHooks _hooks = null!;
    private HookCoordinator _hookCoordinator = null!;
    private ActionExecutor _actions = null!;
    private DeviceService _devices = null!;
    private RingController _ring = null!;
    private InputRouter _router = null!;
    private TrayController _tray = null!;
    private BatteryNotifier _battery = null!;
    private DispatcherTimer? _optionsPlusTimer;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWait;
    private EventWaitHandle? _exitEvent;
    private RegisteredWaitHandle? _exitWait;
    private SettingsWindow? _settings;
    private bool _optionsPlusWarned;
    private int _shutdown;

    public AppHost(Application app, string[] args)
    {
        _app = app;
        _args = args;
        Autostart = new AutostartService();
    }

    public AutostartService Autostart { get; private set; }
    public RingMouseConfig CurrentConfig => _configStore.Current;
    public IReadOnlyList<DeviceSnapshot> Devices => _devicesByKey.Values.ToList();
    private Dispatcher Dispatcher => _app.Dispatcher;

    // UI-Wächter: meldet im Log, wenn der UI-Thread hängt – mit dem Schritt, in dem er zuletzt war
    private volatile string _uiStep = "Start";
    private long _uiPong = Environment.TickCount64;
    private readonly ManualResetEventSlim _uiWatchdogStop = new();

    private void Step(string step) => _uiStep = step;

    private void StartUiWatchdog()
    {
        var thread = new Thread(() =>
        {
            var hung = false;
            while (!_uiWatchdogStop.Wait(5000))
            {
                try
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => Volatile.Write(ref _uiPong, Environment.TickCount64));
                }
                catch
                {
                    return; // Dispatcher beendet
                }
                var silence = Environment.TickCount64 - Volatile.Read(ref _uiPong);
                if (silence > 12_000 && !hung)
                {
                    hung = true;
                    _log.LogError("UI-Thread reagiert seit {Seconds} s nicht (Schritt: {Step})", silence / 1000, _uiStep);
                }
                else if (silence <= 12_000 && hung)
                {
                    hung = false;
                    _log.LogWarning("UI-Thread reagiert wieder (war bei: {Step})", _uiStep);
                }
            }
        })
        {
            IsBackground = true,
            Name = "RingMouse UI-Wächter",
        };
        thread.Start();
    }

    public void Start()
    {
        _logging = new AppLogging();
        _log = _logging.Create("RingMouse");
        StartupTrace.Write("Logger bereit");
        _logging.VerifyFileOutput();
        StartUiWatchdog();
        _log.LogInformation(
            "RingMouse {Version} startet: {Exe} · Benutzer {User} · elevated={Elevated} · uiAccess={UiAccess} · Admin-Konto={Admin} · Daten {Root}",
            typeof(AppHost).Assembly.GetName().Version, Environment.ProcessPath, Environment.UserName, ProcessRights.IsElevated,
            ProcessRights.HasUiAccess, ProcessRights.UserIsAdministrator, AppPaths.Root);
        Autostart = new AutostartService(_logging.Create("Autostart"));

        _configStore = new ConfigStore(AppPaths.ConfigFile, _logging.Create("Config"));
        var load = _configStore.LoadOrCreate();
        _configStore.WriteSchema(AppPaths.SchemaFile);
        var config = _configStore.Current;
        _logging.SetLevel(config.General.LogLevel);
        _logging.RawHidEnabled = config.Debug.RawHidLog;

        Step("Zustand/Hooks/Geräte anlegen");
        _state = new StateStore(AppPaths.StateFile, _logging.Create("State"));
        if (CursorHider.Initialize(AppPaths.CursorMarkerFile))
            _log.LogWarning("Mauszeiger war nach einem Abbruch noch ausgeblendet – wiederhergestellt");
        _hooks = new LowLevelInputHooks(_logging.Create("Hooks"));
        _hookCoordinator = new HookCoordinator(_hooks);
        _devices = new DeviceService(new WinHidTransport(), new WinHidDeviceWatcher(), _logging.Create("Device"));
        _actions = new ActionExecutor(_logging.Create("Actions"), new DpiController(_devices), () => Dispatcher.BeginInvoke(ShowSettings));
        _ring = new RingController(Dispatcher, _hookCoordinator, _actions, _logging.Create("Ring"), config);
        _router = new InputRouter(_ring, _actions, _hookCoordinator, _logging.Create("Input"), config);
        Step("Tray-Symbol anlegen");
        _tray = new TrayController(ShowSettings, OpenConfigFile, OpenLogs, SetRawLog, ReconnectAll, Exit);
        _tray.RawLogChecked = config.Debug.RawHidLog;
        _tray.Quiet = HasArg("--quiet") || HasArg("--selftest");
        _battery = new BatteryNotifier(_tray, _state, _logging.Create("Battery"));

        _actions.Failed += f => Dispatcher.BeginInvoke(() => _tray.Notify("Aktion nicht ausgeführt", f.Message, TrayNotice.Warning));
        _router.ElevationBlocked += name => Dispatcher.BeginInvoke(() => _tray.Notify("Fenster mit Adminrechten",
            $"{name} läuft mit höheren Rechten. Windows blockiert dorthin Eingaben von RingMouse (UIPI) – siehe README \"uiAccess\".",
            TrayNotice.Warning));

        _devices.ButtonChanged += _router.OnButton;
        _devices.RawXY += _ring.OnRawXY;
        _devices.DeviceChanged += s => Dispatcher.BeginInvoke(() => OnDeviceChanged(s));
        _devices.DeviceRemoved += s => Dispatcher.BeginInvoke(() => OnDeviceRemoved(s));
        _devices.FrameTraced += _logging.WriteRaw;

        Step("Config anwenden");
        ApplyConfig(config);
        Step("Config-Überwachung");
        _configStore.Changed += c => Dispatcher.BeginInvoke(() => ApplyConfig(c));
        _configStore.LoadFailed += r => Dispatcher.BeginInvoke(() => _tray.Notify("Config-Fehler – bisherige Einstellungen bleiben aktiv",
            r.ErrorMessage ?? string.Join("\n", r.Issues.Where(i => i.Severity == IssueSeverity.Error).Take(3)), TrayNotice.Warning));
        _configStore.StartWatching();
        if (!load.Success)
        {
            var message = load.ErrorMessage ?? string.Join("\n", load.Issues.Where(i => i.Severity == IssueSeverity.Error).Take(3));
            _log.LogError("config.json fehlerhaft – starte mit Standard-Config (Datei bleibt unverändert): {Error}", message);
            _tray.Notify("config.json fehlerhaft", $"Es wird die Standard-Config verwendet.\n{message}", TrayNotice.Error);
        }

        Step("Systemereignisse anmelden");
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Step("Einzelinstanz-Signale");
        ListenForSecondInstance();

        Step("Geräteverwaltung starten");
        _devices.Start();
        Step("Ring vorwärmen");
        _ring.Warmup();
        Step("Tray aktualisieren");
        UpdateTray();

        Step("Options+-Prüfung");
        _optionsPlusTimer = new DispatcherTimer(TimeSpan.FromSeconds(60), DispatcherPriority.Background, (_, _) => CheckOptionsPlus(), Dispatcher);
        _optionsPlusTimer.Start();
        CheckOptionsPlus();

        if (!_state.State.FirstRunCompleted)
        {
            _state.State.FirstRunCompleted = true;
            _state.Save();
            _tray.Notify("RingMouse läuft", "Die obere DPI-Taste öffnet den Ring. Einstellungen und Akku im Tray-Symbol.", TrayNotice.Info);
        }
        if (HasArg("--settings")) ShowSettings();
        if (HasArg("--selftest")) _ = RunSelfTestAsync();
        Step("läuft");
        _log.LogInformation("Start abgeschlossen");
        StartupTrace.Write("Start abgeschlossen");
    }

    private bool HasArg(string name) => _args.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// "--selftest": prüft den echten Pfad ohne Tastendruck – Gerät konfiguriert, Ring öffnet an der Mausposition,
    /// Vordergrundfenster behält den Fokus, Ring schließt beim Loslassen in der Mitte; danach sauberes Beenden.
    /// </summary>
    private async Task RunSelfTestAsync()
    {
        _log.LogInformation("SELFTEST: Start");
        _actions.DryRun = true; // bewegt der Benutzer währenddessen die Maus, darf keine Aktion (z.B. Sperren) auslösen
        for (var i = 0; i < 150 && !_devicesByKey.Values.Any(d => d.State == DeviceState.Ready); i++) await Task.Delay(100);
        var device = _devicesByKey.Values.FirstOrDefault(d => d.State == DeviceState.Ready);
        _log.LogInformation("SELFTEST: Gerät {Name} bereit={Ready}, umgeleitet [{Diverted}], Akku {Battery} %",
            device?.Name ?? "–", device is not null, device is null ? "" : string.Join(", ", device.DivertedControls.Select(ControlIds.Format)),
            device?.Battery?.EffectivePercent);

        var cid = device?.DivertedControls.FirstOrDefault(c => _configStore.Current.Buttons.ContainsKey(ControlIds.Format(c))) ?? 0x00FD;
        var before = ForegroundWindow.Capture();
        var cursor = RingMouse.Platform.Display.Monitors.CursorPosition();
        _router.OnButton(new ButtonEvent("selftest", cid, true, Stopwatch.GetTimestamp()));
        await Task.Delay(900);
        var during = ForegroundWindow.Capture();
        _log.LogInformation("SELFTEST: Ring sichtbar={Visible}, Fokus unverändert={Same} (vorher {Before}, jetzt {Now})",
            _ring.Window.IsVisible, before.Handle == during.Handle, before.ProcessName, during.ProcessName);

        // Zeiger exakt in die Ringmitte und sofort loslassen, damit Mausbewegungen während des Tests nichts auswählen
        var center = _ring.CenterPx;
        RingMouse.Platform.Display.Monitors.SetCursorPosition((int)Math.Round(center.X), (int)Math.Round(center.Y));
        _router.OnButton(new ButtonEvent("selftest", cid, false, Stopwatch.GetTimestamp()));
        await Task.Delay(400);
        _log.LogInformation("SELFTEST: nach Loslassen in der Mitte sichtbar={Visible} (erwartet: false), Ring {State}",
            _ring.Window.IsVisible, _ring.DiagnosticState);
        RingMouse.Platform.Display.Monitors.SetCursorPosition(cursor.X, cursor.Y);
        _log.LogInformation("SELFTEST: Ende – beende RingMouse");
        Exit();
    }

    private void ApplyConfig(RingMouseConfig config)
    {
        _logging.SetLevel(config.General.LogLevel);
        _logging.RawHidEnabled = config.Debug.RawHidLog;
        _tray.RawLogChecked = config.Debug.RawHidLog;

        var resolver = new ProfileResolver(config);
        _router.Update(resolver);
        _ring.Update(config);
        _battery.Update(config);

        var divert = resolver.ControlsToDivert();
        var rawXY = config.Ring.UseRawXY == RawXYUsage.Off ? new HashSet<ushort>() : resolver.RingControls();
        BatteryVoltageCurve? curve = null;
        if (config.Battery.VoltageCurve is { Count: >= 2 } points)
            curve = new BatteryVoltageCurve(points.Select(p => (p.Mv, p.Percent)));

        _devices.UpdateConfiguration(new DeviceConfiguration
        {
            DivertControls = divert,
            RawXYControls = rawXY,
            DisableAnalytics = config.General.DisableAnalyticsReporting,
            ClearForeignDiversions = true,
            BatteryPollInterval = TimeSpan.FromMinutes(Math.Clamp(config.Battery.PollMinutes, 1, 1440)),
            VoltageCurve = curve,
            SettingsFor = (name, pid, unit) => DeviceMatching.Resolve(config, name, pid, unit),
        });

        foreach (var issue in _configStore.CurrentIssues) _log.LogWarning("Config: {Issue}", issue);
        _log.LogInformation("Config angewendet: umgeleitet [{Divert}], Raw-XY [{RawXY}], {Profiles} Profil(e), Ring-Modus {Mode}",
            string.Join(", ", divert.Select(ControlIds.Format)), string.Join(", ", rawXY.Select(ControlIds.Format)),
            config.Profiles.Count, config.Ring.Mode);

        UpdateTray();
        CheckOptionsPlus();
        _settings?.OnExternalConfigChanged(config);
    }

    private void OnDeviceChanged(DeviceSnapshot snapshot)
    {
        _devicesByKey[snapshot.Key] = snapshot;
        _battery.OnDevice(snapshot);
        _router.UpdateFallback(_devicesByKey.Values);
        UpdateTray();
        _settings?.OnDevicesChanged();
    }

    private void OnDeviceRemoved(DeviceSnapshot snapshot)
    {
        _devicesByKey.Remove(snapshot.Key);
        _router.UpdateFallback(_devicesByKey.Values);
        UpdateTray();
        _settings?.OnDevicesChanged();
    }

    private void UpdateTray()
    {
        var filter = _configStore.Current.Battery.TrayDevice;
        var device = _devicesByKey.Values
            .Where(d => DeviceMatching.MatchesTrayFilter(filter, d.Name))
            .OrderByDescending(d => d.HasBattery)
            .ThenByDescending(d => d.State == DeviceState.Ready)
            .ThenByDescending(d => d.Kind == DeviceKind.Mouse)
            .FirstOrDefault();
        var last = device is null ? _battery.LastKnownAny(filter) : _battery.LastKnown(device.Key);
        _tray.UpdateDevice(device, last?.Percent, last?.Timestamp);
    }

    private void CheckOptionsPlus()
    {
        if (!_configStore.Current.General.WarnIfOptionsPlusRunning)
        {
            _tray.SetWarning(null);
            return;
        }
        var running = OptionsPlusDetector.RunningProcesses();
        if (running.Count > 0)
        {
            _tray.SetWarning("⚠ Logi Options+ läuft und überschreibt Tasten");
            if (_optionsPlusWarned) return;
            _optionsPlusWarned = true;
            _log.LogWarning("Logi Options+ läuft ({Processes}) – überschreibt Tastenumleitungen und aktiviert Analytics-Events", string.Join(", ", running));
            _tray.Notify("Logi Options+ läuft",
                "Options+ überschreibt die Tastenumleitungen von RingMouse. Bitte Options+ beenden bzw. deinstallieren.", TrayNotice.Warning);
        }
        else
        {
            if (_optionsPlusWarned) _log.LogInformation("Logi Options+ läuft nicht mehr");
            _optionsPlusWarned = false;
            _tray.SetWarning(null);
        }
    }

    // ------------------------------------------------------------------ Systemereignisse

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _log.LogInformation("Standby beendet – Geräte werden neu konfiguriert");
            _devices.RequestReconfigureAll("Standby beendet", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(15));
            _hookCoordinator.Reinstall();
        }
        else if (e.Mode == PowerModes.Suspend)
        {
            _log.LogInformation("Standby beginnt");
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        _log.LogInformation("Sitzung: {Reason}", e.Reason);
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect
            or SessionSwitchReason.SessionLogon)
        {
            _devices.RequestReconfigureAll($"Sitzung: {e.Reason}", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
            _hookCoordinator.Reinstall();
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
        Dispatcher.BeginInvoke(() =>
        {
            _ring.RefreshTheme();
            UpdateTray();
        });
    }

    private void ListenForSecondInstance()
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ActivateEventName);
        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent,
            (_, _) => Dispatcher.BeginInvoke(ShowSettings), null, Timeout.Infinite, executeOnlyOnce: false);

        // "RingMouse.exe --exit" (z.B. vor einem Update): sauber beenden wie Tray → Beenden
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ExitEventName);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _log.LogInformation("Beenden per --exit angefordert");
            Exit();
        }), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    // ------------------------------------------------------------------ ISettingsHost / Tray-Aktionen

    public void ShowSettings()
    {
        Step("Einstellungen öffnen");
        if (_settings is null)
        {
            _settings = new SettingsWindow(this);
            _settings.Closed += (_, _) => _settings = null;
        }
        _settings.Show();
        if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
        _settings.Activate();
        Step("läuft");
    }

    public void SaveConfig(RingMouseConfig config) => _configStore.Save(config);

    public string? BackupConfig() => _configStore.CreateBackup();

    public void OpenConfigFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.ConfigFile) { UseShellExecute = true })?.Dispose();
        }
        catch
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppPaths.ConfigFile}\"") { UseShellExecute = true })?.Dispose();
        }
    }

    public void OpenLogs() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogDirectory}\"") { UseShellExecute = true })?.Dispose();

    public void OpenConfigFolder() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Root}\"") { UseShellExecute = true })?.Dispose();

    public void ReconnectAll()
    {
        _log.LogInformation("Manuell: Geräte neu verbinden");
        _devices.RequestReconfigureAll("manuell", TimeSpan.Zero);
    }

    public Task<int?> SetDpiNowAsync(string deviceKey, int dpi) => _devices.SetDpiAsync(deviceKey, dpi);

    public void PreviewRing(RingDefinition ring, RingSettings settings, bool isSubmenu) => _ring.ShowPreview(ring, settings, isSubmenu);

    private void SetRawLog(bool enabled)
    {
        var config = ConfigSerializer.Clone(_configStore.Current);
        config.Debug.RawHidLog = enabled;
        _configStore.Save(config);
    }

    private async void Exit()
    {
        try
        {
            await ShutdownAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Fehler beim Beenden");
        }
        finally
        {
            _app.Shutdown();
        }
    }

    /// <summary>Tray → Beenden: Umleitungen asynchron zurücksetzen, UI bleibt reaktionsfähig. Aufruf auf dem UI-Thread.</summary>
    public async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0) return;
        BeginShutdown();
        try
        {
            await _devices.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Geräte konnten nicht sauber zurückgesetzt werden");
        }
        EndShutdown();
    }

    /// <summary>Abmelden/Herunterfahren/OnExit: synchron mit Zeitlimit. Aufruf auf dem UI-Thread.</summary>
    public void ShutdownBlocking()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0) return;
        BeginShutdown();
        try
        {
            // Nur der Geräte-Teil läuft im Threadpool – UI-Objekte bleiben auf ihrem Thread.
            Task.Run(() => _devices.StopAsync(TimeSpan.FromSeconds(2))).Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Geräte konnten beim Abmelden nicht zurückgesetzt werden");
        }
        EndShutdown();
    }

    /// <summary>UI-gebundene Aufräumarbeiten (UI-Thread).</summary>
    private void BeginShutdown()
    {
        _log.LogInformation("RingMouse wird beendet");
        _uiWatchdogStop.Set();
        CursorHider.Show();
        _optionsPlusTimer?.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _activateWait?.Unregister(null);
        _activateEvent?.Dispose();
        _exitWait?.Unregister(null);
        _exitEvent?.Dispose();
        if (_settings is not null)
        {
            _settings.SuppressClosePrompt = true;
            _settings.Close();
        }
        _configStore.Dispose();
    }

    private void EndShutdown()
    {
        TryRun(_hooks.Dispose);
        TryRun(_actions.Dispose);
        TryRun(_tray.Dispose);
        TryRun(_state.Save);
        _logging.Dispose();

        void TryRun(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Fehler beim Aufräumen");
            }
        }
    }

    private sealed class DpiController(DeviceService devices) : IDpiController
    {
        public Task<int?> SetDpiAsync(int dpi) => devices.SetDpiAsync(null, dpi);
        public Task<int?> CycleDpiAsync(IReadOnlyList<int> values) => devices.CycleDpiAsync(null, values);
    }
}

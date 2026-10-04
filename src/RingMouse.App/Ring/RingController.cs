using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using RingMouse.Actions;
using RingMouse.App.Input;
using RingMouse.Core.Config;
using RingMouse.Core.Profiles;
using RingMouse.Core.Ring;
using RingMouse.Device;
using RingMouse.Platform.Display;
using RingMouse.Platform.Input;
using RingMouse.Platform.Windows;

namespace RingMouse.App.Ring;

/// <summary>
/// Verbindet den Ring-Zustandsautomaten (Core) mit Overlay-Fenster, Zeigerposition, Raw-XY, Hooks und
/// Aktionen. Alle Zustandsänderungen laufen auf dem UI-Thread; Eingänge von HID-/Hook-Threads werden
/// mit höchster Dispatcher-Priorität übergeben.
/// </summary>
internal sealed class RingController
{
    private const int VK_ESCAPE = 0x1B;
    private const int VK_BACK = 0x08;

    private readonly Dispatcher _dispatcher;
    private readonly HookCoordinator _hooks;
    private readonly ActionExecutor _actions;
    private readonly ILogger _log;
    private readonly RingWindow _window;
    private readonly HashSet<string> _uipiLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool[] _swallowUp = new bool[5];
    private readonly object _swallowLock = new(); // _swallowUp wird vom Hook-Thread und vom UI-Thread berührt

    private RingMouseConfig _config;
    private RingTheme _theme;
    private RingInteraction _interaction = new(_ => null);

    // _active/_trigger werden von HID-/Hook-Threads (RequestOpen) und vom UI-Thread (Open/CloseRing) geschrieben.
    // Die Generation verhindert, dass das Schließen des alten Rings die Auslösetaste eines schon angeforderten
    // neuen Rings löscht – dessen Loslassen ginge sonst verloren und der Ring bliebe im Halten-Modus hängen.
    private readonly object _triggerLock = new();
    private int _requestedOpen;
    private int _shownOpen;
    private volatile bool _active;
    private volatile ushort _trigger;
    private (int X, int Y) _originalCursor;
    private (double X, double Y) _center;
    private double _scale = 1;
    private bool _warped;
    private ForegroundWindowInfo _target = ForegroundWindowInfo.None;
    private long _openTimestamp;
    private bool _latencyLogged;
    private bool _renderingHooked;
    private int _rawDx;
    private int _rawDy;
    private double _rawX;
    private double _rawY;
    private bool _rawActive;
    private int _shownDepth;
    private long _lastRawMs;
    private DispatcherTimer? _hookReleaseTimer;

    public RingController(Dispatcher dispatcher, HookCoordinator hooks, ActionExecutor actions, ILogger log, RingMouseConfig config)
    {
        _dispatcher = dispatcher;
        _hooks = hooks;
        _actions = actions;
        _log = log;
        _config = config;
        _theme = RingTheme.Create(config.Ring);
        _window = new RingWindow();
    }

    public bool IsOpen => _active;

    /// <summary>Für Diagnose/Tests: Fenster und aktueller Ring.</summary>
    internal RingWindow Window => _window;

    /// <summary>Für den Selbsttest: Ringmitte (physische Pixel) und Zustand.</summary>
    internal (double X, double Y) CenterPx => _center;

    internal string DiagnosticState => _interaction.IsOpen ? $"{_interaction.Phase} \"{_interaction.CurrentName}\"" : "closed";

    public void Update(RingMouseConfig config)
    {
        _config = config;
        IconCatalog.ClearCache();
        RefreshTheme();
    }

    public void RefreshTheme() => _theme = RingTheme.Create(_config.Ring);

    private DispatcherTimer? _previewTimer;

    /// <summary>
    /// Probeansicht aus den Einstellungen: echter Ring in Originalgröße am Mauszeiger, ohne Hooks und ohne Auswahl,
    /// schließt nach 2,5 s von selbst. Mit den (noch ungespeicherten) Einstellungen <paramref name="settings"/>.
    /// </summary>
    public void ShowPreview(RingDefinition ring, RingSettings settings, bool isSubmenu)
    {
        if (_active || _interaction.IsOpen || ring.Segments.Count == 0) return;
        var cursor = Monitors.CursorPosition();
        var monitor = Monitors.FromPoint(cursor.X, cursor.Y);
        var center = RingGeometry.ClampCenter(cursor.X, cursor.Y, (settings.Radius + RingVisual.Margin) * monitor.Scale, monitor.WorkArea);
        var visual = RingVisual.Build(ring, RingTheme.Create(settings), settings, isSubmenu);
        var index = ring.Segments.FindIndex(s => s?.Action is not null);
        if (index >= 0)
        {
            visual.SetHighlight(index);
            var (px, py) = RingGeometry.PointAt(RingGeometry.SegmentCenterAngle(index, ring.Segments.Count), settings.Radius * 0.62);
            visual.UpdatePointer(px, py, visible: true);
        }
        _window.Present(visual, center, settings.Animation);

        _previewTimer ??= new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromMilliseconds(2500) };
        _previewTimer.Stop();
        _previewTimer.Tick -= OnPreviewTimeout;
        _previewTimer.Tick += OnPreviewTimeout;
        _previewTimer.Start();
    }

    private void OnPreviewTimeout(object? sender, EventArgs e)
    {
        _previewTimer?.Stop();
        if (!_active && !_interaction.IsOpen) _window.HideRing(_config.Ring.Animation); // echter Ring hat Vorrang
    }

    /// <summary>Fenster und Render-Pfad vorab initialisieren (erste Öffnung ohne Kaltstart-Verzögerung).</summary>
    public void Warmup()
    {
        var ring = _config.Rings.Values.FirstOrDefault(r => r.Segments.Count > 0);
        if (ring is null) return;
        _window.Warmup(RingVisual.Build(ring, _theme, _config.Ring, false));
    }

    // ------------------------------------------------------------------ Eingänge (HID-/Hook-Threads)

    /// <summary>true, wenn das Ereignis die Auslösetaste des offenen Rings betrifft (dann übernimmt der Ring).</summary>
    public bool TryHandleTrigger(ButtonEvent e)
    {
        lock (_triggerLock)
        {
            if (!_active || e.ControlId != _trigger) return false;
        }
        _dispatcher.BeginInvoke(DispatcherPriority.Send, () => OnTrigger(e));
        return true;
    }

    public void RequestOpen(string ringName, ButtonEvent e, ForegroundWindowInfo target, ResolvedProfile profile)
    {
        int generation;
        lock (_triggerLock)
        {
            generation = ++_requestedOpen;
            _active = true;
            _trigger = e.ControlId;
        }
        Interlocked.Exchange(ref _rawDx, 0);
        Interlocked.Exchange(ref _rawDy, 0);
        _dispatcher.BeginInvoke(DispatcherPriority.Send, () => Open(ringName, e, target, profile, generation));
    }

    public void RequestCancel() =>
        _dispatcher.BeginInvoke(DispatcherPriority.Send, () => Apply(_interaction.Cancel(RingCloseReason.Superseded)));

    public void OnRawXY(RawXYEvent e)
    {
        if (!_active) return;
        Interlocked.Add(ref _rawDx, e.Dx);
        Interlocked.Add(ref _rawDy, e.Dy);
    }

    // ------------------------------------------------------------------ UI-Thread

    private void Open(string ringName, ButtonEvent e, ForegroundWindowInfo target, ResolvedProfile profile, int generation)
    {
        if (_interaction.IsOpen) CloseRing(restoreCursor: false);
        lock (_triggerLock)
        {
            if (generation != _requestedOpen) return; // inzwischen wurde ein neuerer Ring angefordert
            _active = true;
            _trigger = e.ControlId;
        }
        _shownOpen = generation;
        _target = target;

        var cursor = Monitors.CursorPosition();
        _originalCursor = cursor;
        var monitor = Monitors.FromPoint(cursor.X, cursor.Y);
        _scale = monitor.Scale;
        var halfPx = (_config.Ring.Radius + RingVisual.Margin) * _scale;
        _center = RingGeometry.ClampCenter(cursor.X, cursor.Y, halfPx, monitor.WorkArea);
        _warped = Math.Abs(_center.X - cursor.X) >= 1 || Math.Abs(_center.Y - cursor.Y) >= 1;
        if (_warped) Monitors.SetCursorPosition((int)Math.Round(_center.X), (int)Math.Round(_center.Y));

        var tapCapturable = !target.IsLikelyElevated || ProcessRights.CanDriveElevatedWindows;
        if (!tapCapturable && target.ProcessName is { } name && _uipiLogged.Add(name))
            _log.LogWarning("Admin window {Process} in the foreground: RingMouse cannot capture clicks/Esc there (UIPI) – the ring only works in hold mode", name);

        _interaction = new RingInteraction(profile.Ring);
        var options = new RingInteractionOptions(_config.Ring.Mode, _config.Ring.Deadzone, _config.Ring.Radius,
            _config.Ring.TapThresholdMs, tapCapturable, _config.Ring.AutoCloseSeconds * 1000,
            SubmenuPushRadius: _config.Ring.SubmenuPush ? _config.Ring.Radius * RingInteractionOptions.SubmenuPushFactor : 0);
        if (_interaction.Open(ringName, e.ControlId, NowMs, options) is not RingCommand.Show show)
        {
            ClearTrigger();
            _log.LogWarning("Ring \"{Ring}\" does not exist or is empty (profile {Profile})", ringName, profile.Name);
            return;
        }

        _rawActive = false;
        _rawX = _rawY = 0;
        Interlocked.Exchange(ref _rawDx, 0);
        Interlocked.Exchange(ref _rawDy, 0);

        _window.Present(RingVisual.Build(show.Ring, _theme, _config.Ring, false), _center, _config.Ring.Animation);
        _shownDepth = _interaction.Depth;
        _openTimestamp = e.Timestamp;
        _latencyLogged = false;

        // _swallowUp nicht leeren: Maus-Ups zu Klicks, die der vorige Ring verschluckt hat, müssen weiter verschluckt werden.
        _hookReleaseTimer?.Stop();
        _hooks.SetRingHandlers(OnHookMouse, OnHookKeyboard);
        if (!_renderingHooked)
        {
            CompositionTarget.Rendering += OnRendering;
            _renderingHooked = true;
        }
        _log.LogDebug("Ring \"{Ring}\" opened for {Process} (profile {Profile}, mode {Mode})", ringName, target.ProcessName, profile.Name, _config.Ring.Mode);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // Läuft 60×/s. Ein ungefangener Fehler bliebe abonniert (Dauer-Sturm) und der Mauszeiger könnte dauerhaft
        // ausgeblendet bleiben. Darum hart abfangen und den Ring schließen (stellt Zeiger und Hooks wieder her).
        try
        {
            if (!_interaction.IsOpen) return;
            var now = NowMs;
            if (!_latencyLogged)
            {
                _latencyLogged = true;
                if (_config.Debug.LogRingLatency && _openTimestamp != 0)
                    _log.LogInformation("Ring visible {Ms:0.0} ms after button press", Stopwatch.GetElapsedTime(_openTimestamp).TotalMilliseconds);
            }
            UpdatePointer(now);
            Apply(_interaction.Tick(now));
            if (!_interaction.IsOpen || _window.Visual is not { } visual) return;

            // Rückmeldung pro Frame: Zeigerpunkt (wenn der echte Zeiger beim Halten steht) und Durchschiebe-Fortschritt
            var (ox, oy) = CurrentOffset();
            visual.UpdatePointer(ox, oy, _rawActive && _interaction.Phase == RingPhase.Holding);
            visual.SetPushProgress(_interaction.PushProgress(ox, oy));

            // Echten Mauszeiger ausblenden, solange der Punkt die Richtung zeigt – bleibt über Untermenü-Wechsel hinweg aus,
            // kommt zurück beim Tippen-Modus, beim Schließen und nach 5 s Halten ohne Bewegung.
            var hide = _config.Ring.HideCursor && _interaction.Phase == RingPhase.Holding &&
                       (_rawActive || CursorHider.IsHidden) && now - _lastRawMs < 5000;
            SetCursorHidden(hide);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error in the ring render loop – closing the ring");
            try
            {
                CloseRing(restoreCursor: true);
            }
            catch (Exception closeEx)
            {
                // Notfall: Render-Hook lösen (kein Dauer-Sturm) und Zeiger garantiert zeigen.
                _log.LogError(closeEx, "Closing the ring after a render error also failed");
                if (_renderingHooked)
                {
                    CompositionTarget.Rendering -= OnRendering;
                    _renderingHooked = false;
                }
                CursorHider.Show();
            }
        }
    }

    private void SetCursorHidden(bool hidden)
    {
        if (hidden == CursorHider.IsHidden) return;
        try
        {
            if (hidden) CursorHider.Hide();
            else CursorHider.Show();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Showing/hiding the mouse pointer failed");
        }
    }

    private void UpdatePointer(long now)
    {
        var dx = Interlocked.Exchange(ref _rawDx, 0);
        var dy = Interlocked.Exchange(ref _rawDy, 0);
        if ((dx != 0 || dy != 0) && _interaction.Phase == RingPhase.Holding)
        {
            _rawActive = true;
            _lastRawMs = now;
            _rawX += dx * _config.Ring.RawXYScale;
            _rawY += dy * _config.Ring.RawXYScale;
            var length = Math.Sqrt(_rawX * _rawX + _rawY * _rawY);
            var max = _config.Ring.Radius;
            if (length > max)
            {
                _rawX *= max / length;
                _rawY *= max / length;
            }
        }
        var (ox, oy) = CurrentOffset();
        Apply(_interaction.PointerMoved(ox, oy, now));
    }

    private (double X, double Y) CurrentOffset()
    {
        if (_rawActive && _interaction.Phase == RingPhase.Holding) return (_rawX, _rawY);
        var c = Monitors.CursorPosition();
        return ((c.X - _center.X) / _scale, (c.Y - _center.Y) / _scale);
    }

    private void OnTrigger(ButtonEvent e)
    {
        if (!_interaction.IsOpen) return;
        var now = NowMs;
        UpdatePointer(now);
        var (ox, oy) = CurrentOffset();
        Apply(e.IsDown ? _interaction.TriggerPressed(e.ControlId, now) : _interaction.TriggerReleased(e.ControlId, ox, oy, now));
    }

    private void OnClick(int x, int y, bool primary)
    {
        if (!_interaction.IsOpen) return;
        Apply(_interaction.Click((x - _center.X) / _scale, (y - _center.Y) / _scale, primary, NowMs));
    }

    private void Apply(RingCommand command)
    {
        switch (command)
        {
            case RingCommand.Highlight h:
                _window.Visual?.SetHighlight(h.Index);
                break;

            case RingCommand.SwitchRing s:
                _log.LogDebug("Ring level \"{Ring}\"", s.Name);
                var forward = _interaction.Depth > _shownDepth;
                _shownDepth = _interaction.Depth;
                _window.Swap(RingVisual.Build(s.Ring, _theme, _config.Ring, s.IsSubmenu), _config.Ring.Animation, forward);
                _rawActive = false;
                _rawX = _rawY = 0;
                // Neue Ebene startet neutral in der Mitte
                Monitors.SetCursorPosition((int)Math.Round(_center.X), (int)Math.Round(_center.Y));
                break;

            case RingCommand.Execute x:
                _window.Visual?.Pulse(x.Index);
                CloseRing(restoreCursor: true, animate: true);
                if (x.Segment.Action is { } action)
                    _actions.Enqueue(action, $"Ring {x.RingName}[{x.Index}] \"{x.Segment.Label}\"", _target);
                break;

            case RingCommand.Close c:
                _log.LogDebug("Ring closed: {Reason}", c.Reason);
                // Abgelöst durch einen neuen Ring: sofort weg, sonst kurz ausblenden
                CloseRing(restoreCursor: c.Reason != RingCloseReason.Superseded, animate: c.Reason != RingCloseReason.Superseded);
                break;
        }
    }

    /// <summary>Auslösetaste freigeben – außer ein neuerer Ring ist schon angefordert (dessen Taste bleibt aktiv).</summary>
    private void ClearTrigger()
    {
        lock (_triggerLock)
        {
            if (_requestedOpen != _shownOpen) return;
            _active = false;
            _trigger = 0;
        }
    }

    private void CloseRing(bool restoreCursor, bool animate = false)
    {
        SetCursorHidden(false);
        ClearTrigger();
        if (_interaction.IsOpen) _interaction.Cancel();
        if (_renderingHooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderingHooked = false;
        }
        _window.HideRing(animate && _config.Ring.Animation);

        bool anySwallow;
        lock (_swallowLock) anySwallow = AnySwallowPending();
        if (anySwallow)
        {
            // Die zugehörigen Maus-Ups noch verschlucken, dann Hooks lösen (spätestens nach 1,5 s)
            _hooks.SetRingHandlers(OnHookMouse, null);
            _hookReleaseTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Normal, (_, _) => ReleaseHooks(), _dispatcher);
            _hookReleaseTimer.Stop();
            _hookReleaseTimer.Start();
        }
        else
        {
            _hooks.SetRingHandlers(null, null);
        }

        if (restoreCursor && (_config.Ring.RestoreCursor || _warped))
            Monitors.SetCursorPosition(_originalCursor.X, _originalCursor.Y);
    }

    private void ReleaseHooks()
    {
        _hookReleaseTimer?.Stop();
        if (_active) return;
        lock (_swallowLock) Array.Clear(_swallowUp);
        _hooks.SetRingHandlers(null, null);
    }

    /// <summary>true, wenn noch ein Maus-Up verschluckt werden muss. Nur unter <see cref="_swallowLock"/> aufrufen.</summary>
    private bool AnySwallowPending()
    {
        foreach (var pending in _swallowUp)
            if (pending) return true;
        return false;
    }

    // ------------------------------------------------------------------ Hook-Callbacks (Hook-Thread!)

    private bool OnHookMouse(in MouseHookEvent e)
    {
        if (e.OwnInjection) return false;
        var index = e.Message switch
        {
            MouseMessage.LeftDown or MouseMessage.LeftUp => 0,
            MouseMessage.RightDown or MouseMessage.RightUp => 1,
            MouseMessage.MiddleDown or MouseMessage.MiddleUp => 2,
            MouseMessage.XDown or MouseMessage.XUp => e.XButton == 2 ? 4 : 3,
            _ => -1,
        };
        if (index < 0) return false;

        if (e.IsButtonDown)
        {
            if (!_active) return false;
            lock (_swallowLock) _swallowUp[index] = true;
            var (x, y, primary) = (e.X, e.Y, e.Message == MouseMessage.LeftDown);
            _dispatcher.BeginInvoke(DispatcherPriority.Send, () => OnClick(x, y, primary));
            return true;
        }

        if (e.IsButtonUp)
        {
            // Prüfen und Zurücksetzen müssen zusammen atomar sein (UI-Thread kann _swallowUp parallel leeren).
            bool swallowed, anyLeft;
            lock (_swallowLock)
            {
                swallowed = _swallowUp[index];
                if (swallowed) _swallowUp[index] = false;
                anyLeft = AnySwallowPending();
            }
            if (!swallowed) return false;
            if (!_active && !anyLeft) _dispatcher.BeginInvoke(ReleaseHooks);
            return true;
        }
        return false;
    }

    private bool OnHookKeyboard(in KeyboardHookEvent e)
    {
        if (!_active || e.OwnInjection) return false;
        if (e.VirtualKey == VK_ESCAPE)
        {
            if (e.IsDown) _dispatcher.BeginInvoke(DispatcherPriority.Send, () => Apply(_interaction.Escape()));
            return true;
        }
        if (e.VirtualKey == VK_BACK)
        {
            if (e.IsDown) _dispatcher.BeginInvoke(DispatcherPriority.Send, () => Apply(_interaction.Back()));
            return true;
        }
        return false;
    }

    private static long NowMs => Environment.TickCount64;
}

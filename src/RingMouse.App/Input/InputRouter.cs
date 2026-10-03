using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RingMouse.Actions;
using RingMouse.App.Ring;
using RingMouse.Core.Config;
using RingMouse.Core.Profiles;
using RingMouse.Device;
using RingMouse.HidPlusPlus.Features;
using RingMouse.Platform.Input;
using RingMouse.Platform.Windows;

namespace RingMouse.App.Input;

/// <summary>
/// Leitet Tastenereignisse (HID++ oder Hook-Fallback) anhand des Profils des Vordergrundprozesses weiter:
/// Ring öffnen, Aktion ausführen, Maustaste nachbilden oder Originalfunktion durchreichen.
/// </summary>
internal sealed class InputRouter
{
    private readonly RingController _ring;
    private readonly ActionExecutor _actions;
    private readonly HookCoordinator _hooks;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<(string Device, ushort Cid), MouseButtonKind> _held = new();
    private readonly HashSet<string> _elevationLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ushort> _fallbackSwallowed = [];
    private volatile ProfileResolver _resolver;
    private volatile IReadOnlyDictionary<MouseButtonKind, ushort> _fallback = new Dictionary<MouseButtonKind, ushort>();

    public InputRouter(RingController ring, ActionExecutor actions, HookCoordinator hooks, ILogger log, RingMouseConfig config)
    {
        _ring = ring;
        _actions = actions;
        _hooks = hooks;
        _log = log;
        _resolver = new ProfileResolver(config);
    }

    /// <summary>Vordergrundfenster hat höhere Rechte als RingMouse (einmal pro Prozess gemeldet).</summary>
    public event Action<string>? ElevationBlocked;

    public void Update(ProfileResolver resolver) => _resolver = resolver;

    /// <summary>Tastenereignis vom DeviceService (HID-Lese-Thread) oder vom Hook-Fallback (Hook-Thread).</summary>
    public void OnButton(ButtonEvent e)
    {
        if (_ring.TryHandleTrigger(e)) return;

        if (!e.IsDown)
        {
            if (_held.TryRemove((e.DeviceKey, e.ControlId), out var button)) InputInjector.SendMouseButton(button, false);
            return;
        }

        if (_ring.IsOpen) _ring.RequestCancel();

        var foreground = ForegroundWindow.Capture();
        CheckElevation(foreground);
        var profile = _resolver.Resolve(foreground.ProcessName);
        var action = profile.ActionFor(e.ControlId);

        switch (action)
        {
            case null or NativeAction:
                Passthrough(e);
                break;
            case OpenRingAction open:
                _ring.RequestOpen(open.Ring, e, foreground, profile);
                break;
            case MouseAction mouse:
                InputInjector.SendMouseButton(mouse.Button, true);
                _held[(e.DeviceKey, e.ControlId)] = mouse.Button;
                break;
            case NoneAction:
                break;
            default:
                _actions.Enqueue(action, $"Taste {ControlIds.Format(e.ControlId)} ({profile.Name})", foreground);
                break;
        }
    }

    /// <summary>Umgeleitete Standard-Maustaste ohne eigene Belegung: Originalfunktion per SendInput nachbilden.</summary>
    private void Passthrough(ButtonEvent e)
    {
        if (e.DeviceKey == HookDeviceKey) return; // Hook hat gar nicht erst verschluckt
        var button = Map(ControlIds.GetStandardMouseButton(e.ControlId));
        if (button is null) return;
        InputInjector.SendMouseButton(button.Value, true);
        _held[(e.DeviceKey, e.ControlId)] = button.Value;
    }

    private void CheckElevation(ForegroundWindowInfo fg)
    {
        if (!fg.IsLikelyElevated || ProcessRights.CanDriveElevatedWindows || fg.ProcessName is null) return;
        lock (_elevationLogged)
        {
            if (!_elevationLogged.Add(fg.ProcessName)) return;
        }
        _log.LogWarning(
            "Vordergrundfenster {Process} läuft mit höheren Rechten ({State}), RingMouse nicht – Windows blockiert Eingaben dorthin (UIPI). " +
            "Abhilfe: uiAccess-Installation (tools\\install-uiaccess.ps1) oder Autostart-Aufgabe mit höchsten Rechten (nur für Admin-Konten).",
            fg.ProcessName, fg.Elevation);
        ElevationBlocked?.Invoke(fg.ProcessName);
    }

    // ------------------------------------------------------------------ Hook-Fallback

    public const string HookDeviceKey = "ll-hook";

    /// <summary>Standardtasten, deren HID++-Umleitung auf einem Gerät fehlgeschlagen ist, per WH_MOUSE_LL abfangen.</summary>
    public void UpdateFallback(IEnumerable<DeviceSnapshot> devices)
    {
        var map = new Dictionary<MouseButtonKind, ushort>();
        foreach (var device in devices)
        {
            foreach (var cid in device.FailedControls)
            {
                var button = Map(ControlIds.GetStandardMouseButton(cid));
                if (button is MouseButtonKind.Middle or MouseButtonKind.Back or MouseButtonKind.Forward) map[button.Value] = cid;
            }
        }

        var changed = map.Count != _fallback.Count || map.Any(kv => !_fallback.TryGetValue(kv.Key, out var v) || v != kv.Value);
        _fallback = map;
        _hooks.SetFallback(map.Count > 0 ? OnFallbackMouse : null);
        if (changed)
            _log.LogInformation(map.Count > 0
                ? "Hook-Fallback aktiv für {Buttons} (HID++-Umleitung nicht möglich)"
                : "Hook-Fallback inaktiv{Buttons}", string.Join(", ", map.Select(kv => $"{kv.Key}={ControlIds.Format(kv.Value)}")));
    }

    private bool OnFallbackMouse(in MouseHookEvent e)
    {
        if (e.Injected) return false;
        MouseButtonKind? button = e.Message switch
        {
            MouseMessage.MiddleDown or MouseMessage.MiddleUp => MouseButtonKind.Middle,
            MouseMessage.XDown or MouseMessage.XUp => e.XButton switch { 1 => MouseButtonKind.Back, 2 => MouseButtonKind.Forward, _ => null },
            _ => null,
        };
        if (button is null || !_fallback.TryGetValue(button.Value, out var cid)) return false;

        if (e.IsButtonDown)
        {
            var action = _resolver.Resolve(ForegroundWindow.Capture().ProcessName).ActionFor(cid);
            if (action is null or NativeAction) return false; // Originalfunktion: gar nicht erst verschlucken
            lock (_fallbackSwallowed) _fallbackSwallowed.Add(cid);
        }
        else
        {
            lock (_fallbackSwallowed)
            {
                if (!_fallbackSwallowed.Remove(cid)) return false;
            }
        }

        OnButton(new ButtonEvent(HookDeviceKey, cid, e.IsButtonDown, Stopwatch.GetTimestamp()));
        return true;
    }

    private static MouseButtonKind? Map(StandardMouseButton button) => button switch
    {
        StandardMouseButton.Left => MouseButtonKind.Left,
        StandardMouseButton.Right => MouseButtonKind.Right,
        StandardMouseButton.Middle => MouseButtonKind.Middle,
        StandardMouseButton.Back => MouseButtonKind.Back,
        StandardMouseButton.Forward => MouseButtonKind.Forward,
        _ => null,
    };
}

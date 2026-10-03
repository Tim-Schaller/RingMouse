using RingMouse.Platform.Input;

namespace RingMouse.App.Input;

/// <summary>
/// Bündelt die Low-Level-Hooks: Klick-/Tastenerfassung solange der Ring offen ist und der dauerhafte
/// Maustasten-Fallback (für Standardtasten, deren HID++-Umleitung nicht klappt). Ohne Bedarf kein Hook.
/// </summary>
internal sealed class HookCoordinator(LowLevelInputHooks hooks)
{
    private volatile MouseHookHandler? _ringMouse;
    private volatile KeyboardHookHandler? _ringKeyboard;
    private volatile MouseHookHandler? _fallbackMouse;

    public void SetRingHandlers(MouseHookHandler? mouse, KeyboardHookHandler? keyboard)
    {
        // Läuft der Fallback-Hook schon länger, kann Windows ihn nach einem Timeout still entfernt haben –
        // beim Öffnen des Rings daher frisch setzen (sonst wären Klicks im Ring nicht erfassbar).
        var refresh = mouse is not null && _ringMouse is null && _fallbackMouse is not null;
        _ringMouse = mouse;
        _ringKeyboard = keyboard;
        Update();
        if (refresh) hooks.Reinstall();
    }

    public void SetFallback(MouseHookHandler? handler)
    {
        _fallbackMouse = handler;
        Update();
    }

    public void Reinstall() => hooks.Reinstall();

    private void Update()
    {
        hooks.SetMouseHandler(_ringMouse is not null || _fallbackMouse is not null ? OnMouse : null);
        hooks.SetKeyboardHandler(_ringKeyboard);
    }

    private bool OnMouse(in MouseHookEvent e)
    {
        if (_ringMouse is { } ring && ring(in e)) return true;
        return _fallbackMouse is { } fallback && fallback(in e);
    }
}

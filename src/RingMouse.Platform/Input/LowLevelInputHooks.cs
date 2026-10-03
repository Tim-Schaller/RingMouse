using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RingMouse.Platform.Native;

namespace RingMouse.Platform.Input;

public enum MouseMessage
{
    Move,
    LeftDown,
    LeftUp,
    RightDown,
    RightUp,
    MiddleDown,
    MiddleUp,
    XDown,
    XUp,
    Wheel,
    HorizontalWheel,
    Other,
}

public readonly record struct MouseHookEvent(MouseMessage Message, int X, int Y, int XButton, bool Injected, bool OwnInjection)
{
    public bool IsButtonDown => Message is MouseMessage.LeftDown or MouseMessage.RightDown or MouseMessage.MiddleDown or MouseMessage.XDown;
    public bool IsButtonUp => Message is MouseMessage.LeftUp or MouseMessage.RightUp or MouseMessage.MiddleUp or MouseMessage.XUp;
}

public readonly record struct KeyboardHookEvent(int VirtualKey, bool IsDown, bool Injected, bool OwnInjection);

/// <summary>Rückgabe true = Ereignis verschlucken.</summary>
public delegate bool MouseHookHandler(in MouseHookEvent e);

public delegate bool KeyboardHookHandler(in KeyboardHookEvent e);

/// <summary>
/// WH_MOUSE_LL/WH_KEYBOARD_LL auf einem eigenen Thread mit Message-Loop. Hooks werden nur installiert,
/// solange ein Handler gesetzt ist (Ring offen bzw. Maustasten-Fallback aktiv). Handler müssen sehr schnell
/// zurückkehren, sonst entfernt Windows den Hook stillschweigend.
/// </summary>
public sealed unsafe class LowLevelInputHooks : IDisposable
{
    private const uint WM_UPDATE_HOOKS = Win32.WM_APP + 0x31;
    private static LowLevelInputHooks? s_instance;

    private readonly ILogger? _logger;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private volatile MouseHookHandler? _mouseHandler;
    private volatile KeyboardHookHandler? _keyboardHandler;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private uint _threadId;
    private int _disposed;

    public LowLevelInputHooks(ILogger? logger = null)
    {
        if (s_instance is not null) throw new InvalidOperationException("Es darf nur eine Hook-Instanz geben.");
        s_instance = this;
        _logger = logger;
        _thread = new Thread(Run) { IsBackground = true, Name = "RingMouse LL-Hooks", Priority = ThreadPriority.Highest };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public bool MouseHookActive => _mouseHook != IntPtr.Zero;
    public bool KeyboardHookActive => _keyboardHook != IntPtr.Zero;

    public void SetMouseHandler(MouseHookHandler? handler)
    {
        _mouseHandler = handler;
        RequestUpdate();
    }

    public void SetKeyboardHandler(KeyboardHookHandler? handler)
    {
        _keyboardHandler = handler;
        RequestUpdate();
    }

    /// <summary>Hooks neu installieren (z.B. falls Windows sie nach einem Timeout entfernt hat).</summary>
    public void Reinstall()
    {
        Win32.PostThreadMessage(_threadId, WM_UPDATE_HOOKS, 1, IntPtr.Zero);
    }

    private void RequestUpdate()
    {
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, WM_UPDATE_HOOKS, IntPtr.Zero, IntPtr.Zero);
    }

    private void Run()
    {
        _threadId = Win32.GetCurrentThreadId();
        Win32.PeekMessage(out _, IntPtr.Zero, 0, 0, Win32.PM_NOREMOVE); // Message-Queue anlegen
        _ready.Set();

        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.hwnd == IntPtr.Zero && msg.message == WM_UPDATE_HOOKS)
            {
                UpdateHooks(force: msg.wParam != IntPtr.Zero);
                continue;
            }
            Win32.TranslateMessage(in msg);
            Win32.DispatchMessage(in msg);
        }

        Unhook(ref _mouseHook);
        Unhook(ref _keyboardHook);
    }

    private void UpdateHooks(bool force)
    {
        if (force)
        {
            Unhook(ref _mouseHook);
            Unhook(ref _keyboardHook);
        }

        var module = Win32.GetModuleHandle(null);
        if (_mouseHandler is not null && _mouseHook == IntPtr.Zero)
        {
            _mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, &MouseProc, module, 0);
            if (_mouseHook == IntPtr.Zero)
                _logger?.LogWarning("WH_MOUSE_LL konnte nicht installiert werden (Win32 {Error})", Marshal.GetLastPInvokeError());
        }
        else if (_mouseHandler is null)
        {
            Unhook(ref _mouseHook);
        }

        if (_keyboardHandler is not null && _keyboardHook == IntPtr.Zero)
        {
            _keyboardHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, &KeyboardProc, module, 0);
            if (_keyboardHook == IntPtr.Zero)
                _logger?.LogWarning("WH_KEYBOARD_LL konnte nicht installiert werden (Win32 {Error})", Marshal.GetLastPInvokeError());
        }
        else if (_keyboardHandler is null)
        {
            Unhook(ref _keyboardHook);
        }
    }

    private static void Unhook(ref IntPtr hook)
    {
        if (hook == IntPtr.Zero) return;
        Win32.UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var self = s_instance;
        if (nCode >= 0 && self?._mouseHandler is { } handler)
        {
            var data = (Win32.MSLLHOOKSTRUCT*)lParam;
            var message = (uint)wParam switch
            {
                0x0200 => MouseMessage.Move,
                0x0201 => MouseMessage.LeftDown,
                0x0202 => MouseMessage.LeftUp,
                0x0204 => MouseMessage.RightDown,
                0x0205 => MouseMessage.RightUp,
                0x0207 => MouseMessage.MiddleDown,
                0x0208 => MouseMessage.MiddleUp,
                0x020A => MouseMessage.Wheel,
                0x020B => MouseMessage.XDown,
                0x020C => MouseMessage.XUp,
                0x020E => MouseMessage.HorizontalWheel,
                _ => MouseMessage.Other,
            };
            var e = new MouseHookEvent(message, data->pt.X, data->pt.Y, (int)(data->mouseData >> 16),
                (data->flags & Win32.LLMHF_INJECTED) != 0, data->dwExtraInfo == InputInjector.ExtraInfoMarker);
            var swallow = false;
            try
            {
                swallow = handler(in e);
            }
            catch
            {
                // nie in den Hook-Aufrufer werfen
            }
            if (swallow) return 1;
        }
        return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var self = s_instance;
        if (nCode >= 0 && self?._keyboardHandler is { } handler)
        {
            var data = (Win32.KBDLLHOOKSTRUCT*)lParam;
            var isDown = (uint)wParam is 0x0100 or 0x0104; // WM_KEYDOWN / WM_SYSKEYDOWN
            var e = new KeyboardHookEvent((int)data->vkCode, isDown, (data->flags & Win32.LLKHF_INJECTED) != 0,
                data->dwExtraInfo == InputInjector.ExtraInfoMarker);
            var swallow = false;
            try
            {
                swallow = handler(in e);
            }
            catch
            {
                // ignorieren
            }
            if (swallow) return 1;
        }
        return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _mouseHandler = null;
        _keyboardHandler = null;
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        s_instance = null;
    }
}

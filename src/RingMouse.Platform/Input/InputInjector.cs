using System.Runtime.InteropServices;
using RingMouse.Core.Config;
using RingMouse.Core.Input;
using RingMouse.Platform.Native;

namespace RingMouse.Platform.Input;

/// <summary>
/// Synthetische Eingaben über SendInput. Alle Ereignisse tragen <see cref="ExtraInfoMarker"/>, damit eigene
/// Hooks sie von echten Eingaben unterscheiden können.
/// </summary>
public static unsafe class InputInjector
{
    /// <summary>dwExtraInfo-Kennung eigener Eingaben ("RMRF").</summary>
    public static readonly IntPtr ExtraInfoMarker = new(0x524D5246);

    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_TAB = 0x09;

    // Zusatztasten wie ein Mensch halten: manche Empfänger prüfen den Zustand von Win/Strg erst, wenn sie die eigentliche
    // Taste verarbeiten (z.B. Edge/Chromium beim Emoji-Panel Win+.). Kommt alles in einem Paket, ist Win dann schon
    // wieder oben – und es erscheint nur ".".
    private const int ModifierLeadMs = 30;
    private const int ModifierHoldMs = 60;

    /// <summary>
    /// Sendet Tastenkombinationen; Zeichen-Tasten werden über das Tastaturlayout des Vordergrundfensters aufgelöst.
    /// Kombinationen mit Zusatztasten werden zeitlich gestreckt (≈ 0,1 s) – nur aus dem Aktions-Thread aufrufen.
    /// </summary>
    public static int SendKeyStrokes(IReadOnlyList<KeyStroke> strokes, IntPtr keyboardLayout = default)
    {
        var hkl = keyboardLayout == IntPtr.Zero ? ForegroundKeyboardLayout() : keyboardLayout;
        var sent = 0;
        foreach (var stroke in strokes)
        {
            var chord = BuildChord(stroke, hkl);
            if (chord.ModifiersDown.Count == 0)
            {
                sent += Send(chord.Keys);
                continue;
            }
            sent += Send(chord.ModifiersDown);
            Thread.Sleep(ModifierLeadMs);
            sent += Send(chord.Keys);
            Thread.Sleep(ModifierHoldMs);
            sent += Send(chord.ModifiersUp);
        }
        return sent;
    }

    /// <summary>Tippt Text per Unicode-Eingabe (layoutunabhängig, Umlaute ok). \n = Enter, \t = Tab.</summary>
    public static int SendText(string text)
    {
        var inputs = new List<Win32.INPUT>(text.Length * 2);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\r':
                    continue;
                case '\n':
                    inputs.Add(Key(VK_RETURN, true, false, IntPtr.Zero));
                    inputs.Add(Key(VK_RETURN, false, false, IntPtr.Zero));
                    continue;
                case '\t':
                    inputs.Add(Key(VK_TAB, true, false, IntPtr.Zero));
                    inputs.Add(Key(VK_TAB, false, false, IntPtr.Zero));
                    continue;
                default:
                    inputs.Add(Unicode(c, true));
                    inputs.Add(Unicode(c, false));
                    break;
            }
        }
        return Send(inputs);
    }

    public static int SendMedia(MediaKey key)
    {
        ushort vk = key switch
        {
            MediaKey.PlayPause => 0xB3,
            MediaKey.Next => 0xB0,
            MediaKey.Previous => 0xB1,
            MediaKey.Stop => 0xB2,
            MediaKey.VolumeUp => 0xAF,
            MediaKey.VolumeDown => 0xAE,
            MediaKey.Mute => 0xAD,
            _ => 0,
        };
        if (vk == 0) return 0;
        return SendSpan([Key(vk, true, true, IntPtr.Zero), Key(vk, false, true, IntPtr.Zero)]);
    }

    public static int SendMouseButton(MouseButtonKind button, bool down)
    {
        var (flags, data) = MouseFlags(button, down);
        var input = new Win32.INPUT { type = Win32.INPUT_MOUSE };
        input.U.mi = new Win32.MOUSEINPUT { dwFlags = flags, mouseData = data, dwExtraInfo = ExtraInfoMarker };
        return SendSpan([input]);
    }

    public static int ClickMouseButton(MouseButtonKind button) =>
        SendMouseButton(button, true) + SendMouseButton(button, false);

    /// <summary>Lässt physisch gehaltene Zusatztasten los (verhindert z.B. Strg+Emoji statt Emoji).</summary>
    public static int ReleaseHeldModifiers()
    {
        ReadOnlySpan<ushort> modifiers = [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C];
        var inputs = new List<Win32.INPUT>();
        foreach (var vk in modifiers)
        {
            if ((Win32.GetAsyncKeyState(vk) & 0x8000) != 0)
                inputs.Add(Key(vk, false, vk is 0xA3 or 0xA5 or 0x5B or 0x5C, IntPtr.Zero));
        }
        return inputs.Count == 0 ? 0 : Send(inputs);
    }

    public static IntPtr ForegroundKeyboardLayout()
    {
        var hwnd = Win32.GetForegroundWindow();
        var thread = hwnd == IntPtr.Zero ? 0 : Win32.GetWindowThreadProcessId(hwnd, out _);
        return Win32.GetKeyboardLayout(thread);
    }

    /// <summary>Eine Tastenkombination: Zusatztasten drücken, Taste drücken/loslassen, Zusatztasten loslassen.</summary>
    private sealed record Chord(List<Win32.INPUT> ModifiersDown, List<Win32.INPUT> Keys, List<Win32.INPUT> ModifiersUp);

    private static Chord BuildChord(KeyStroke stroke, IntPtr hkl)
    {
        var chord = new Chord([], [], []);
        var modifiers = new List<ModifierKey>(stroke.Modifiers);
        ushort vk;
        var extended = stroke.Key.Extended;

        if (stroke.Key.IsCharacter)
        {
            var scan = Win32.VkKeyScanEx(stroke.Key.Character, hkl);
            if (scan == -1)
            {
                // Zeichen existiert im Layout nicht → ohne Zusatztasten als Unicode tippen
                if (modifiers.Count == 0)
                {
                    chord.Keys.Add(Unicode(stroke.Key.Character, true));
                    chord.Keys.Add(Unicode(stroke.Key.Character, false));
                }
                return chord;
            }
            vk = (ushort)(scan & 0xFF);
            var shiftState = (scan >> 8) & 0xFF;
            if ((shiftState & 0x01) != 0 && !modifiers.Contains(ModifierKey.Shift)) modifiers.Add(ModifierKey.Shift);
            if ((shiftState & 0x02) != 0 && !modifiers.Contains(ModifierKey.Ctrl)) modifiers.Add(ModifierKey.Ctrl);
            if ((shiftState & 0x04) != 0 && !modifiers.Contains(ModifierKey.Alt)) modifiers.Add(ModifierKey.Alt);
        }
        else
        {
            vk = stroke.Key.VirtualKey;
        }

        foreach (var m in modifiers)
            chord.ModifiersDown.Add(Key(KeyNames.ModifierVirtualKey(m), true, m == ModifierKey.Win, hkl));
        chord.Keys.Add(Key(vk, true, extended, hkl));
        chord.Keys.Add(Key(vk, false, extended, hkl));
        for (var i = modifiers.Count - 1; i >= 0; i--)
            chord.ModifiersUp.Add(Key(KeyNames.ModifierVirtualKey(modifiers[i]), false, modifiers[i] == ModifierKey.Win, hkl));
        return chord;
    }

    private static Win32.INPUT Key(ushort vk, bool down, bool extended, IntPtr hkl)
    {
        var input = new Win32.INPUT { type = Win32.INPUT_KEYBOARD };
        input.U.ki = new Win32.KEYBDINPUT
        {
            wVk = vk,
            wScan = (ushort)Win32.MapVirtualKeyEx(vk, 0 /* MAPVK_VK_TO_VSC */, hkl),
            dwFlags = (down ? 0 : Win32.KEYEVENTF_KEYUP) | (extended ? Win32.KEYEVENTF_EXTENDEDKEY : 0),
            dwExtraInfo = ExtraInfoMarker,
        };
        return input;
    }

    private static Win32.INPUT Unicode(char c, bool down)
    {
        var input = new Win32.INPUT { type = Win32.INPUT_KEYBOARD };
        input.U.ki = new Win32.KEYBDINPUT
        {
            wVk = 0,
            wScan = c,
            dwFlags = Win32.KEYEVENTF_UNICODE | (down ? 0 : Win32.KEYEVENTF_KEYUP),
            dwExtraInfo = ExtraInfoMarker,
        };
        return input;
    }

    private static (uint Flags, uint Data) MouseFlags(MouseButtonKind button, bool down) => button switch
    {
        MouseButtonKind.Left => (down ? Win32.MOUSEEVENTF_LEFTDOWN : Win32.MOUSEEVENTF_LEFTUP, 0),
        MouseButtonKind.Right => (down ? Win32.MOUSEEVENTF_RIGHTDOWN : Win32.MOUSEEVENTF_RIGHTUP, 0),
        MouseButtonKind.Middle => (down ? Win32.MOUSEEVENTF_MIDDLEDOWN : Win32.MOUSEEVENTF_MIDDLEUP, 0),
        MouseButtonKind.Back => (down ? Win32.MOUSEEVENTF_XDOWN : Win32.MOUSEEVENTF_XUP, Win32.XBUTTON1),
        MouseButtonKind.Forward => (down ? Win32.MOUSEEVENTF_XDOWN : Win32.MOUSEEVENTF_XUP, Win32.XBUTTON2),
        _ => (0, 0),
    };

    private static int Send(List<Win32.INPUT> inputs)
    {
        if (inputs.Count == 0) return 0;
        var span = CollectionsMarshal.AsSpan(inputs);
        fixed (Win32.INPUT* p = span)
        {
            return (int)Win32.SendInput((uint)span.Length, p, sizeof(Win32.INPUT));
        }
    }

    private static int SendSpan(ReadOnlySpan<Win32.INPUT> inputs)
    {
        fixed (Win32.INPUT* p = inputs)
        {
            return (int)Win32.SendInput((uint)inputs.Length, p, sizeof(Win32.INPUT));
        }
    }
}

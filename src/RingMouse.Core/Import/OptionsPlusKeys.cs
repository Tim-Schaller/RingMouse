using RingMouse.Core.Input;

namespace RingMouse.Core.Import;

/// <summary>Tastenkürzel aus den Formaten von Logi Options+ in RingMouse-Schreibweise ("Ctrl+Shift+S").</summary>
public static class OptionsPlusKeys
{
    private static readonly string[] s_modifierOrder = ["Ctrl", "Shift", "Alt", "Win"];

    /// <summary>
    /// Loupedeck-Parameter "keyboardKey", z.B. "Windows+Period___269225996___Win+.___win-190…" oder "Insert___…___Insert___".
    /// Mac-Kürzel (vierter Teil "mac-…") lassen sich nicht sinnvoll übertragen.
    /// </summary>
    public static bool TryFromKeyboardKey(string? parameter, out string keys, out bool macOnly)
    {
        keys = "";
        macOnly = false;
        if (string.IsNullOrWhiteSpace(parameter)) return false;
        var parts = parameter.Split("___");
        if (parts.Length >= 4 && parts[3].StartsWith("mac-", StringComparison.OrdinalIgnoreCase))
        {
            macOnly = true;
            return false;
        }
        return TryFromTokens(parts[0].Split('+'), out keys);
    }

    /// <summary>Anzeigename aus Options+ ("Ctrl + X", auch deutsch "Strg + X").</summary>
    public static bool TryFromDisplay(string? actionName, out string keys)
    {
        keys = "";
        if (string.IsNullOrWhiteSpace(actionName)) return false;
        var compact = actionName.Replace(" + ", "+").Trim(); // "Ctrl + C" → "Ctrl+C", "Ctrl + +" → "Ctrl++"
        if (!KeyChordParser.TryParse(compact, out var strokes, out _) || strokes.Count != 1) return false;
        keys = strokes[0].ToString();
        return true;
    }

    /// <summary>Options+ "keystroke": HID-Usage der Tastatur-Seite (0x07) plus Modifier-Usages 0xE0–0xE7.</summary>
    public static bool TryFromHid(int code, IEnumerable<int> modifiers, out string keys)
    {
        keys = "";
        var tokens = new List<string>();
        foreach (var m in modifiers)
        {
            var name = m switch
            {
                0xE0 or 0xE4 => "Ctrl",
                0xE1 or 0xE5 => "Shift",
                0xE2 or 0xE6 => "Alt",
                0xE3 or 0xE7 => "Win",
                _ => null,
            };
            if (name is null) return false;
            tokens.Add(name);
        }
        if (code != 0)
        {
            if (HidKey(code) is not { } key) return false;
            tokens.Add(key);
        }
        return tokens.Count > 0 && TryFromTokens(tokens, out keys);
    }

    private static bool TryFromTokens(IEnumerable<string> tokens, out string keys)
    {
        keys = "";
        var modifiers = new HashSet<string>();
        string? key = null;
        foreach (var raw in tokens)
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            var modifier = t.ToLowerInvariant() switch
            {
                "controlorcommand" or "control" or "ctrl" => "Ctrl",
                "shift" => "Shift",
                "alt" or "option" => "Alt",
                "windows" or "win" or "meta" or "command" or "super" => "Win",
                _ => null,
            };
            if (modifier is not null)
            {
                modifiers.Add(modifier);
                continue;
            }
            if (key is not null || KeyToken(t) is not { } k) return false;
            key = k;
        }

        var ordered = s_modifierOrder.Where(modifiers.Contains).ToList();
        if (key is null)
        {
            if (ordered.Count != 1) return false; // nur eine einzelne Modifier-Taste ist sinnvoll (z.B. "Win")
            keys = ordered[0];
            return true;
        }
        keys = string.Join("+", ordered.Append(key));
        return KeyChordParser.TryParse(keys, out _, out _);
    }

    /// <summary>Tastenname im Web-/Loupedeck-Stil ("KeyQ", "Digit5", "ArrowLeft", "Period") → RingMouse-Name.</summary>
    private static string? KeyToken(string t)
    {
        if (t.Length == 1) return t;
        if (t.Length == 4 && t.StartsWith("Key", StringComparison.OrdinalIgnoreCase) && char.IsLetterOrDigit(t[3])) return char.ToUpperInvariant(t[3]).ToString();
        if (t.Length == 6 && t.StartsWith("Digit", StringComparison.OrdinalIgnoreCase) && char.IsDigit(t[5])) return t[5].ToString();
        if (t.StartsWith("Arrow", StringComparison.OrdinalIgnoreCase)) return t[5..];
        if (t.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase))
        {
            var rest = t[6..];
            return rest.Length == 1 && char.IsDigit(rest[0]) ? "Num" + rest
                : rest.Equals("Enter", StringComparison.OrdinalIgnoreCase) ? "Enter" : "Num" + rest;
        }

        var mapped = t.ToLowerInvariant() switch
        {
            "escape" => "Esc",
            "return" => "Enter",
            "period" => ".",
            "comma" => ",",
            "minus" => "-",
            "equal" => "=",
            "slash" => "/",
            "backslash" => "\\",
            "semicolon" => ";",
            "quote" => "'",
            "bracketleft" => "[",
            "bracketright" => "]",
            "backquote" => "`",
            "contextmenu" => "Apps",
            "audiovolumeup" => "VolumeUp",
            "audiovolumedown" => "VolumeDown",
            "audiovolumemute" => "VolumeMute",
            "mediatracknext" => "MediaNext",
            "mediatrackprevious" => "MediaPrevious",
            _ => null,
        };
        if (mapped is not null) return mapped;
        return KeyNames.TryKey(t, out _) ? t : null;
    }

    /// <summary>HID-Usage (Tastatur-Seite) → RingMouse-Name; Satzzeichen nach US-Position.</summary>
    private static string? HidKey(int code) => code switch
    {
        >= 0x04 and <= 0x1D => ((char)('A' + code - 0x04)).ToString(),
        >= 0x1E and <= 0x26 => ((char)('1' + code - 0x1E)).ToString(),
        0x27 => "0",
        0x28 => "Enter",
        0x29 => "Esc",
        0x2A => "Backspace",
        0x2B => "Tab",
        0x2C => "Space",
        0x2D => "-",
        0x2E => "=",
        0x2F => "[",
        0x30 => "]",
        0x31 => "\\",
        0x33 => ";",
        0x34 => "'",
        0x35 => "`",
        0x36 => ",",
        0x37 => ".",
        0x38 => "/",
        0x39 => "CapsLock",
        >= 0x3A and <= 0x45 => $"F{code - 0x3A + 1}",
        0x46 => "PrintScreen",
        0x47 => "ScrollLock",
        0x48 => "Pause",
        0x49 => "Insert",
        0x4A => "Home",
        0x4B => "PageUp",
        0x4C => "Delete",
        0x4D => "End",
        0x4E => "PageDown",
        0x4F => "Right",
        0x50 => "Left",
        0x51 => "Down",
        0x52 => "Up",
        0x53 => "NumLock",
        0x54 => "NumDivide",
        0x55 => "NumMultiply",
        0x56 => "NumSubtract",
        0x57 => "NumAdd",
        0x58 => "Enter",
        >= 0x59 and <= 0x61 => $"Num{code - 0x59 + 1}",
        0x62 => "Num0",
        0x63 => "NumDecimal",
        0x65 => "Apps",
        >= 0x68 and <= 0x73 => $"F{code - 0x68 + 13}",
        0x7F => "VolumeMute",
        0x80 => "VolumeUp",
        0x81 => "VolumeDown",
        _ => null,
    };
}

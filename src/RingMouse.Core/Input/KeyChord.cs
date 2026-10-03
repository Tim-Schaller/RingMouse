using System.Globalization;
using System.Text;
using static RingMouse.Core.Localization.Lang;

namespace RingMouse.Core.Input;

public enum ModifierKey
{
    Ctrl,
    Shift,
    Alt,
    Win,
}

/// <summary>Eine Taste: entweder virtueller Tastencode oder ein Zeichen, das erst beim Senden über das aktive Layout aufgelöst wird.</summary>
public readonly record struct KeySpec(ushort VirtualKey, char Character, bool Extended)
{
    public bool IsCharacter => VirtualKey == 0;

    public static KeySpec Vk(ushort vk, bool extended = false) => new(vk, '\0', extended);

    public static KeySpec Char(char c) => new(0, c, false);

    public override string ToString() => IsCharacter ? Character.ToString() : KeyNames.NameOf(VirtualKey);
}

/// <summary>Eine Kombination wie Ctrl+Shift+S.</summary>
public sealed record KeyStroke(IReadOnlyList<ModifierKey> Modifiers, KeySpec Key)
{
    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var m in Modifiers) sb.Append(m).Append('+');
        sb.Append(Key);
        return sb.ToString();
    }
}

/// <summary>
/// Parst Tastenkürzel wie "Ctrl+Shift+S", "Win+.", "Alt+F4" oder Folgen "Ctrl+K, Ctrl+C".
/// Deutsche Namen (Strg, Entf, Pos1, Bild auf …) sind erlaubt; "+" und "," als Taste: "Ctrl++", "Ctrl+,".
/// </summary>
public static class KeyChordParser
{
    public static IReadOnlyList<KeyStroke> Parse(string text) =>
        TryParse(text, out var strokes, out var error) ? strokes : throw new FormatException(error);

    public static bool TryParse(string? text, out IReadOnlyList<KeyStroke> strokes, out string? error)
    {
        strokes = [];
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = L("Empty keyboard shortcut.", "Leere Tastenkombination.");
            return false;
        }

        var result = new List<KeyStroke>();
        foreach (var chord in Tokenize(text))
        {
            if (chord.Count == 0) continue;
            var modifiers = new List<ModifierKey>();
            for (var i = 0; i < chord.Count - 1; i++)
            {
                if (!KeyNames.TryModifier(chord[i], out var mods))
                {
                    error = L($"\"{chord[i]}\" is not a modifier key (Ctrl/Shift/Alt/Win) – in \"{text}\".",
                        $"\"{chord[i]}\" ist keine Zusatztaste (Ctrl/Shift/Alt/Win) – in \"{text}\".");
                    return false;
                }
                foreach (var m in mods) if (!modifiers.Contains(m)) modifiers.Add(m);
            }

            var last = chord[^1];
            if (KeyNames.TryKey(last, out var key))
            {
                result.Add(new KeyStroke(modifiers, key));
            }
            else if (KeyNames.TryModifier(last, out var lastMods) && lastMods.Count == 1)
            {
                // Nur eine Zusatztaste allein, z.B. "Win" (öffnet Start)
                result.Add(new KeyStroke(modifiers, KeySpec.Vk(KeyNames.ModifierVirtualKey(lastMods[0]), lastMods[0] == ModifierKey.Win)));
            }
            else
            {
                error = L($"Unknown key \"{last}\" in \"{text}\".", $"Unbekannte Taste \"{last}\" in \"{text}\".");
                return false;
            }
        }

        if (result.Count == 0)
        {
            error = L("Empty keyboard shortcut.", "Leere Tastenkombination.");
            return false;
        }
        strokes = result;
        return true;
    }

    /// <summary>Zerlegt in Akkorde (getrennt durch ',') und Tasten (getrennt durch '+').</summary>
    private static List<List<string>> Tokenize(string text)
    {
        var chords = new List<List<string>> { new() };
        var token = new StringBuilder();
        var expectToken = true;

        void Flush()
        {
            var t = token.ToString().Trim();
            if (t.Length > 0) chords[^1].Add(t);
            token.Clear();
        }

        foreach (var c in text)
        {
            if ((c == '+' || c == ',') && token.ToString().Trim().Length == 0 && expectToken && chords[^1].Count > 0)
            {
                // Separator an Stelle einer Taste → wörtliche Taste ("Ctrl++", "Ctrl+,")
                token.Append(c);
                expectToken = false;
                continue;
            }
            if (c == '+')
            {
                Flush();
                expectToken = true;
                continue;
            }
            if (c == ',')
            {
                Flush();
                chords.Add([]);
                expectToken = true;
                continue;
            }
            if (!char.IsWhiteSpace(c)) expectToken = false;
            token.Append(c);
        }
        Flush();
        return chords.Where(c => c.Count > 0).ToList();
    }
}

/// <summary>Namens-Tabelle für virtuelle Tastencodes (VK_*).</summary>
public static class KeyNames
{
    private static readonly Dictionary<string, KeySpec> s_named = BuildNamed();
    private static readonly Dictionary<ushort, string> s_display = new()
    {
        [0x0D] = "Enter", [0x1B] = "Esc", [0x09] = "Tab", [0x20] = "Space", [0x08] = "Backspace",
        [0x2E] = "Delete", [0x2D] = "Insert", [0x24] = "Home", [0x23] = "End", [0x21] = "PageUp",
        [0x22] = "PageDown", [0x26] = "Up", [0x28] = "Down", [0x25] = "Left", [0x27] = "Right",
        [0x2C] = "PrintScreen", [0x13] = "Pause", [0x14] = "CapsLock", [0x90] = "NumLock", [0x91] = "ScrollLock",
        [0x5D] = "Apps", [0x5B] = "Win", [0xA2] = "Ctrl", [0xA0] = "Shift", [0xA4] = "Alt",
        [0xAD] = "VolumeMute", [0xAE] = "VolumeDown", [0xAF] = "VolumeUp", [0xB0] = "MediaNext",
        [0xB1] = "MediaPrevious", [0xB2] = "MediaStop", [0xB3] = "MediaPlayPause",
    };

    public static bool TryModifier(string token, out IReadOnlyList<ModifierKey> modifiers)
    {
        modifiers = token.Trim().ToLowerInvariant() switch
        {
            "ctrl" or "control" or "strg" or "ctl" => [ModifierKey.Ctrl],
            "shift" or "umschalt" => [ModifierKey.Shift],
            "alt" => [ModifierKey.Alt],
            "altgr" => [ModifierKey.Ctrl, ModifierKey.Alt],
            "win" or "windows" or "meta" or "super" or "cmd" => [ModifierKey.Win],
            _ => [],
        };
        return modifiers.Count > 0;
    }

    public static ushort ModifierVirtualKey(ModifierKey m) => m switch
    {
        ModifierKey.Ctrl => 0xA2,  // VK_LCONTROL
        ModifierKey.Shift => 0xA0, // VK_LSHIFT
        ModifierKey.Alt => 0xA4,   // VK_LMENU
        ModifierKey.Win => 0x5B,   // VK_LWIN
        _ => 0,
    };

    public static bool TryKey(string token, out KeySpec key)
    {
        var t = token.Trim();
        key = default;
        if (t.Length == 0) return false;

        if (t.Length == 1)
        {
            var c = t[0];
            if (c is >= 'a' and <= 'z') c = char.ToUpperInvariant(c);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                key = KeySpec.Vk(c);
                return true;
            }
            key = KeySpec.Char(t[0]);
            return true;
        }

        var norm = t.ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
        if (s_named.TryGetValue(norm, out key)) return true;

        if (norm.Length is >= 2 and <= 3 && norm[0] == 'f' &&
            int.TryParse(norm[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 24)
        {
            key = KeySpec.Vk((ushort)(0x70 + f - 1));
            return true;
        }

        foreach (var prefix in new[] { "numpad", "num" })
        {
            if (norm.StartsWith(prefix, StringComparison.Ordinal) && norm.Length == prefix.Length + 1 && char.IsDigit(norm[^1]))
            {
                key = KeySpec.Vk((ushort)(0x60 + (norm[^1] - '0')));
                return true;
            }
        }
        return false;
    }

    public static string NameOf(ushort vk)
    {
        if (s_display.TryGetValue(vk, out var n)) return n;
        if (vk is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return $"F{vk - 0x70 + 1}";
        if (vk is >= 0x60 and <= 0x69) return $"Num{vk - 0x60}";
        return $"VK{vk:X2}";
    }

    private static Dictionary<string, KeySpec> BuildNamed()
    {
        var d = new Dictionary<string, KeySpec>(StringComparer.Ordinal);

        void Add(ushort vk, bool ext, params string[] names)
        {
            foreach (var n in names) d[n] = KeySpec.Vk(vk, ext);
        }

        Add(0x0D, false, "enter", "return", "eingabe");
        Add(0x1B, false, "esc", "escape");
        Add(0x09, false, "tab", "tabulator");
        Add(0x20, false, "space", "leertaste", "leer", "spacebar");
        Add(0x08, false, "backspace", "back", "rücktaste", "ruecktaste");
        Add(0x2E, true, "delete", "del", "entf", "entfernen");
        Add(0x2D, true, "insert", "ins", "einfg", "einfügen");
        Add(0x24, true, "home", "pos1");
        Add(0x23, true, "end", "ende");
        Add(0x21, true, "pageup", "pgup", "bildauf", "prior");
        Add(0x22, true, "pagedown", "pgdn", "bildab", "next");
        Add(0x26, true, "up", "oben", "pfeiloben", "uparrow");
        Add(0x28, true, "down", "unten", "pfeilunten", "downarrow");
        Add(0x25, true, "left", "links", "pfeillinks", "leftarrow");
        Add(0x27, true, "right", "rechts", "pfeilrechts", "rightarrow");
        Add(0x2C, true, "printscreen", "prtsc", "print", "druck", "prtscr");
        Add(0x13, false, "pause", "break");
        Add(0x14, false, "capslock", "feststell");
        Add(0x90, true, "numlock");
        Add(0x91, false, "scrolllock", "rollen");
        Add(0x5D, true, "apps", "menu", "kontext", "contextmenu", "kontextmenü");
        Add(0x6A, false, "multiply", "nummultiply", "numstar");
        Add(0x6B, false, "add", "numadd", "numplus");
        Add(0x6D, false, "subtract", "numsubtract", "numminus");
        Add(0x6E, false, "decimal", "numdecimal");
        Add(0x6F, true, "divide", "numdivide", "numslash");
        Add(0xAD, true, "volumemute", "mute", "stumm");
        Add(0xAE, true, "volumedown", "leiser");
        Add(0xAF, true, "volumeup", "lauter");
        Add(0xB0, true, "medianext", "nexttrack", "medianexttrack");
        Add(0xB1, true, "mediaprevious", "mediaprev", "prevtrack", "mediaprevtrack");
        Add(0xB2, true, "mediastop", "stop");
        Add(0xB3, true, "mediaplaypause", "playpause", "play");
        Add(0xA6, true, "browserback");
        Add(0xA7, true, "browserforward");
        Add(0xA8, true, "browserrefresh");
        Add(0xAC, true, "browserhome");

        d["plus"] = KeySpec.Char('+');
        d["minus"] = KeySpec.Char('-');
        d["comma"] = KeySpec.Char(',');
        d["komma"] = KeySpec.Char(',');
        d["period"] = KeySpec.Char('.');
        d["dot"] = KeySpec.Char('.');
        d["punkt"] = KeySpec.Char('.');
        return d;
    }
}

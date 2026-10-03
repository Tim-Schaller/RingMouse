namespace RingMouse.HidPlusPlus.Features;

/// <summary>Standard-Maustasten, auf die sich eine CID nativ abbilden lässt (für Durchreichen/Hook-Fallback).</summary>
public enum StandardMouseButton
{
    None,
    Left,
    Right,
    Middle,
    Back,     // XButton1
    Forward,  // XButton2
}

/// <summary>
/// Anzeigenamen bekannter Control-IDs. Die Software ermittelt die vorhandenen CIDs immer per 0x1B04;
/// diese Tabelle dient nur der Beschriftung und der Zuordnung zu Standard-Maustasten.
/// </summary>
public static class ControlIds
{
    private static readonly Dictionary<ushort, string> s_names = new()
    {
        [0x0001] = "Volume Up",
        [0x0002] = "Volume Down",
        [0x0003] = "Mute",
        [0x0004] = "Play/Pause",
        [0x0005] = "Next Track",
        [0x0006] = "Previous Track",
        [0x0007] = "Stop",
        [0x0050] = "Left Button",
        [0x0051] = "Right Button",
        [0x0052] = "Middle Button",
        [0x0053] = "Back",
        [0x0056] = "Forward",
        [0x005B] = "Tilt Left",
        [0x005D] = "Tilt Right",
        [0x00C3] = "Gesture Button",
        [0x00C4] = "Smart Shift",
        [0x00D7] = "Virtual Gesture Button",
        [0x00FD] = "DPI Switch",
    };

    public static string GetName(ushort controlId) =>
        s_names.TryGetValue(controlId, out var n) ? n : $"CID 0x{controlId:X4}";

    public static StandardMouseButton GetStandardMouseButton(ushort controlId) => controlId switch
    {
        0x0050 => StandardMouseButton.Left,
        0x0051 => StandardMouseButton.Right,
        0x0052 => StandardMouseButton.Middle,
        0x0053 => StandardMouseButton.Back,
        0x0056 => StandardMouseButton.Forward,
        _ => StandardMouseButton.None,
    };

    /// <summary>Formatiert eine CID wie in der Config ("0x00FD").</summary>
    public static string Format(ushort controlId) => $"0x{controlId:X4}";

    /// <summary>Akzeptiert "0x00FD", "00FD", "fd" oder dezimal "253".</summary>
    public static bool TryParse(string? text, out ushort controlId)
    {
        controlId = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ushort.TryParse(t[2..], System.Globalization.NumberStyles.HexNumber, null, out controlId);
        if (t.StartsWith('c') && ushort.TryParse(t[1..], out controlId)) return true; // Options+-Schreibweise "c253"
        if (t.Any(char.IsLetter))
            return ushort.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out controlId);
        return ushort.TryParse(t, out controlId);
    }
}

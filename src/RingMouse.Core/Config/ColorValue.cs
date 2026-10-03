using System.Globalization;

namespace RingMouse.Core.Config;

/// <summary>Farbangabe aus der Config: "#RRGGBB" bzw. "#AARRGGBB" (das # ist optional).</summary>
public readonly record struct ColorValue(byte A, byte R, byte G, byte B)
{
    public static bool TryParse(string? text, out ColorValue color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('#');
        if (s.Length is not (6 or 8) || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        color = s.Length == 6
            ? new ColorValue(0xFF, (byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : new ColorValue((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static ColorValue FromRgb(byte r, byte g, byte b) => new(0xFF, r, g, b);

    public override string ToString() => A == 0xFF ? $"#{R:X2}{G:X2}{B:X2}" : $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}

using System.Windows.Media;
using RingMouse.Core.Config;
using RingMouse.Platform.Display;

namespace RingMouse.App.Ring;

/// <summary>
/// Farben des Rings: Hell/Dunkel (nach Windows-App-Modus) oder aus einer eigenen Grundfarbe abgeleitet,
/// Markierung in der Windows-Akzentfarbe oder einer eigenen Farbe.
/// </summary>
internal sealed record RingTheme(
    bool IsDark,
    Brush Background,
    Brush Border,
    Brush Segment,
    Brush SegmentEmpty,
    Brush Highlight,
    Brush Icon,
    Brush IconHighlight,
    Brush Label,
    Brush LabelHighlight,
    Brush Center,
    Brush CenterText)
{
    public static RingTheme Create(ThemePreference preference) => Create(new RingSettings { Theme = preference });

    public static RingTheme Create(RingSettings settings)
    {
        var dark = settings.Theme switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => !SystemTheme.AppsUseLightTheme,
        };
        Color accent;
        if (ColorValue.TryParse(settings.AccentColor, out var custom))
        {
            accent = Color.FromRgb(custom.R, custom.G, custom.B);
        }
        else
        {
            var (r, g, b) = SystemTheme.AccentColor;
            accent = Color.FromRgb(r, g, b);
            // Akzent für dunklen Modus etwas aufhellen, damit er auf Grau leuchtet
            if (dark) accent = Blend(accent, Colors.White, 0.12);
        }
        var onAccent = Luminance(accent) > 0.55 ? Color.FromRgb(0x10, 0x10, 0x10) : Colors.White;

        if (ColorValue.TryParse(settings.RingColor, out var ring))
            return FromBase(Color.FromRgb(ring.R, ring.G, ring.B), accent, onAccent);

        return dark
            ? new RingTheme(true,
                Brush(Color.FromArgb(0xEE, 0x20, 0x20, 0x20)),
                Brush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                Brush(Color.FromArgb(0xFF, 0x2F, 0x2F, 0x2F)),
                Brush(Color.FromArgb(0x55, 0x2F, 0x2F, 0x2F)),
                Brush(accent),
                Brush(Color.FromRgb(0xF2, 0xF2, 0xF2)),
                Brush(onAccent),
                Brush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
                Brush(onAccent),
                Brush(Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A)),
                Brush(Color.FromRgb(0xE6, 0xE6, 0xE6)))
            : new RingTheme(false,
                Brush(Color.FromArgb(0xF2, 0xF0, 0xF0, 0xF0)),
                Brush(Color.FromArgb(0x30, 0x00, 0x00, 0x00)),
                Brush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                Brush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)),
                Brush(accent),
                Brush(Color.FromRgb(0x1B, 0x1B, 0x1B)),
                Brush(onAccent),
                Brush(Color.FromRgb(0x44, 0x44, 0x44)),
                Brush(onAccent),
                Brush(Color.FromArgb(0xFF, 0xE8, 0xE8, 0xE8)),
                Brush(Color.FromRgb(0x2A, 0x2A, 0x2A)));
    }

    /// <summary>Eigene Grundfarbe = Segmentfarbe; Scheibe und Mitte etwas dunkler, Schrift hell oder dunkel je nach Helligkeit.</summary>
    private static RingTheme FromBase(Color segment, Color accent, Color onAccent)
    {
        var dark = Luminance(segment) < 0.5;
        var text = dark ? Color.FromRgb(0xF2, 0xF2, 0xF2) : Color.FromRgb(0x1B, 0x1B, 0x1B);
        var label = dark ? Color.FromRgb(0xC8, 0xC8, 0xC8) : Color.FromRgb(0x44, 0x44, 0x44);
        var background = Blend(segment, Colors.Black, dark ? 0.32 : 0.06);
        var center = Blend(segment, Colors.Black, dark ? 0.45 : 0.09);
        return new RingTheme(dark,
            Brush(Color.FromArgb(dark ? (byte)0xEE : (byte)0xF2, background.R, background.G, background.B)),
            Brush(dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0x00, 0x00, 0x00)),
            Brush(segment),
            Brush(Color.FromArgb(dark ? (byte)0x55 : (byte)0x70, segment.R, segment.G, segment.B)),
            Brush(accent),
            Brush(text),
            Brush(onAccent),
            Brush(label),
            Brush(onAccent),
            Brush(center),
            Brush(text));
    }

    private static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}

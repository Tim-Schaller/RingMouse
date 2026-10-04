using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RingMouse.App.Tray;

/// <summary>
/// Zeichnet das Tray-Icon: Akkustand in Prozent in der Mitte, darum ein Ring (Erkennungszeichen von RingMouse, damit es
/// sich z.B. von der Prozentanzeige von MagicPods unterscheidet), der zugleich den Füllstand als Bogen zeigt.
/// Farben wie Windows: einfarbig passend zur Taskleiste, orange ab 20 %, rot ab 10 %, grün beim Laden/am Netz,
/// grau wenn das Gerät nicht erreichbar ist (dann letzter bekannter Stand), "?" wenn kein Wert vorliegt.
/// </summary>
internal static class BatteryIcon
{
    private static readonly Color Orange = Color.FromRgb(0xF7, 0x63, 0x0C);
    private static readonly Color Red = Color.FromRgb(0xFF, 0x43, 0x43);
    private static readonly Color RedOnLight = Color.FromRgb(0xC4, 0x2B, 0x1C);
    private static readonly Color GreenOnDark = Color.FromRgb(0x6C, 0xCB, 0x5F);
    private static readonly Color GreenOnLight = Color.FromRgb(0x0F, 0x7B, 0x0F);
    private static readonly Color Grey = Color.FromRgb(0x8A, 0x8A, 0x8A);
    private static readonly Typeface DigitFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public static BitmapSource Render(int? percent, bool charging, bool connected, bool lightTaskbar, int size)
    {
        var s = (double)size;
        var foreground = lightTaskbar ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Colors.White;
        var level = percent is { } known ? Math.Clamp(known, 0, 100) : 0;
        var accent = !connected ? Grey
            : charging ? (lightTaskbar ? GreenOnLight : GreenOnDark)
            : percent is null ? foreground
            : level <= 10 ? (lightTaskbar ? RedOnLight : Red)
            : level <= 20 ? Orange
            : foreground;
        var textColor = !connected ? Grey : charging || level <= 10 && percent is not null ? accent : foreground;

        // 16 px → 2 px Ring, 24 px → 3 px, 32 px → 4 px
        var thickness = Math.Max(1.5, Math.Round(s / 8.0));
        var radius = s / 2 - thickness / 2;
        var center = new Point(s / 2, s / 2);

        var visual = new DrawingVisual();
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
        using (var dc = visual.RenderOpen())
        {
            // Spur des Rings dezent, der Füllstand als Bogen ab 12 Uhr im Uhrzeigersinn
            var trackColor = Color.FromArgb((byte)(lightTaskbar ? 0x40 : 0x50), foreground.R, foreground.G, foreground.B);
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(trackColor), thickness), center, radius, radius);
            if (percent is not null && level > 0)
            {
                var pen = new Pen(new SolidColorBrush(accent), thickness);
                if (level >= 100) dc.DrawEllipse(null, pen, center, radius, radius);
                else dc.DrawGeometry(null, pen, Arc(center, radius, Math.Max(level, 4) / 100.0 * 360));
            }

            var innerRadius = Math.Max(radius - thickness / 2, 1); // nie ≤ 0 (sonst negative Schriftgröße bei winziger Icongröße)
            if (percent is null && charging)
                DrawBolt(dc, textColor, center, innerRadius); // MX Vertical meldet beim Laden keinen Prozentwert
            else if (level >= 100)
                DrawGlyph(dc, Ring.IconCatalog.Glyphs["CheckMark"], textColor, center, innerRadius); // voll – "100" wäre bei 16 px unleserlich
            else
                DrawCentered(dc, percent is { } p ? p.ToString(CultureInfo.InvariantCulture) : "?", textColor, center, innerRadius);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private const double DigitHeightEm = 0.70; // Segoe UI: Ziffernhöhe ≈ 0,7 em

    /// <summary>Zahl so groß wie möglich, aber vollständig im Innenkreis (die Ecken des Ziffernblocks berühren ihn höchstens).</summary>
    private static void DrawCentered(DrawingContext dc, string text, Color color, Point center, double innerRadius)
    {
        var brush = new SolidColorBrush(color);
        var unit = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, DigitFace, 100, brush, 1.0);
        var halfWidthEm = unit.WidthIncludingTrailingWhitespace / 100 / 2;
        var fontSize = innerRadius / Math.Sqrt(halfWidthEm * halfWidthEm + DigitHeightEm * DigitHeightEm / 4);
        fontSize = Math.Floor(Math.Min(fontSize, innerRadius * 2 * 0.95) * 2) / 2;

        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, DigitFace, fontSize, brush, 1.0);
        // Ziffernhöhe statt Zeilenhöhe zentrieren
        var y = center.Y + fontSize * DigitHeightEm / 2 - formatted.Baseline;
        dc.DrawText(formatted, new Point(Math.Round(center.X - formatted.WidthIncludingTrailingWhitespace / 2), Math.Round(y)));
    }

    private static void DrawBolt(DrawingContext dc, Color color, Point center, double innerRadius)
    {
        var h = innerRadius * 1.55;
        var w = h * 0.62;
        var (cx, cy) = (center.X, center.Y);
        var bolt = new StreamGeometry();
        using (var g = bolt.Open())
        {
            g.BeginFigure(new Point(cx + w * 0.12, cy - h / 2), true, true);
            g.LineTo(new Point(cx - w * 0.50, cy + h * 0.08), true, false);
            g.LineTo(new Point(cx - w * 0.02, cy + h * 0.08), true, false);
            g.LineTo(new Point(cx - w * 0.14, cy + h / 2), true, false);
            g.LineTo(new Point(cx + w * 0.50, cy - h * 0.10), true, false);
            g.LineTo(new Point(cx + w * 0.02, cy - h * 0.10), true, false);
        }
        bolt.Freeze();
        dc.DrawGeometry(new SolidColorBrush(color), null, bolt);
    }

    private static void DrawGlyph(DrawingContext dc, char glyph, Color color, Point center, double innerRadius)
    {
        var face = new Typeface(Ring.IconCatalog.IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var formatted = new FormattedText(glyph.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face,
            Math.Round(innerRadius * 1.3), new SolidColorBrush(color), 1.0);
        dc.DrawText(formatted, new Point(Math.Round(center.X - formatted.Width / 2), Math.Round(center.Y - formatted.Height / 2)));
    }

    private static Geometry Arc(Point center, double radius, double sweepDegrees)
    {
        var end = (sweepDegrees - 90) * Math.PI / 180;
        var figure = new PathFigure { StartPoint = new Point(center.X, center.Y - radius), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(new Point(center.X + radius * Math.Cos(end), center.Y + radius * Math.Sin(end)),
            new Size(radius, radius), 0, sweepDegrees > 180, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }
}

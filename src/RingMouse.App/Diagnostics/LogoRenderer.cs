using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RingMouse.App.Diagnostics;

/// <summary>
/// Das RingMouse-Logo als Vektorzeichnung: acht Ringsegmente wie im Actions Ring, eines im Akzentverlauf hervorgehoben
/// und leicht herausgezogen (wie die Markierung beim Zeigen), in der Mitte eine Maus mit leuchtendem Mausrad.
/// Daraus entstehen App-Icon (.ico) und die Logo-Grafiken fürs README ("--render-ui").
/// </summary>
internal static class LogoRenderer
{
    private static readonly Color SegmentTop = Color.FromRgb(0x5A, 0x67, 0x7D);
    private static readonly Color SegmentBottom = Color.FromRgb(0x46, 0x51, 0x63);
    private static readonly Color AccentFrom = Color.FromRgb(0x38, 0xBD, 0xF8); // himmelblau
    private static readonly Color AccentTo = Color.FromRgb(0x81, 0x4C, 0xF6);   // violett
    private static readonly Color CenterTop = Color.FromRgb(0x27, 0x30, 0x41);
    private static readonly Color CenterBottom = Color.FromRgb(0x14, 0x19, 0x23);

    /// <summary>Größen im App-Icon (Taskleiste, Explorer, Alt+Tab, Fenstertitel).</summary>
    public static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    public static Drawing Draw(double size)
    {
        var small = size <= 24;
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            var c = new Point(size / 2, size / 2);
            var outer = size * 0.47;
            var inner = size * 0.285;
            var gap = Math.Max(size * 0.022, 1.1);
            var lift = size * 0.022;
            var accent = new LinearGradientBrush(AccentFrom, AccentTo, new Point(0, 0), new Point(1, 1));
            var segment = new LinearGradientBrush(SegmentTop, SegmentBottom, 90);

            for (var i = 0; i < 8; i++)
            {
                var mid = -90 + i * 45; // 0 = oben, im Uhrzeigersinn
                var highlighted = i == 1;
                var offset = highlighted ? lift : 0;
                dc.DrawGeometry(highlighted ? accent : segment, null, Sector(c, inner, outer, mid - 22.5, mid + 22.5, gap, offset));
            }

            // Mitte: dunkle Scheibe mit Maus
            var disc = size * 0.25;
            dc.DrawEllipse(new LinearGradientBrush(CenterTop, CenterBottom, 90), null, c, disc, disc);

            var mouseW = size * (small ? 0.20 : 0.17);
            var mouseH = size * (small ? 0.30 : 0.275);
            var body = new Rect(c.X - mouseW / 2, c.Y - mouseH / 2, mouseW, mouseH);
            dc.DrawRoundedRectangle(Brushes.White, null, body, mouseW / 2, mouseW / 2);
            if (!small)
            {
                // Tastentrennung und Mausrad
                var split = new Pen(new SolidColorBrush(CenterBottom), Math.Max(size * 0.011, 1)) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
                dc.DrawLine(split, new Point(c.X, body.Top), new Point(c.X, body.Top + mouseH * 0.40));
                var wheelW = Math.Max(size * 0.030, 2);
                var wheel = new Rect(c.X - wheelW / 2, body.Top + mouseH * 0.13, wheelW, mouseH * 0.17);
                dc.DrawRoundedRectangle(accent, null, wheel, wheelW / 2, wheelW / 2);
            }
        }
        return group;
    }

    /// <summary>Ringsegment mit gleichmäßig breiten Fugen (statt keilförmiger) und optionalem Versatz nach außen.</summary>
    private static Geometry Sector(Point c, double inner, double outer, double fromDeg, double toDeg, double gap, double offset)
    {
        var midRad = (fromDeg + toDeg) / 2 * Math.PI / 180;
        var center = new Point(c.X + Math.Cos(midRad) * offset, c.Y + Math.Sin(midRad) * offset);

        Point At(double r, double deg, double side)
        {
            // Punkt auf Radius r, um die halbe Fugenbreite seitlich (side = ±1) vom Strahl abgerückt
            var a = (deg * Math.PI / 180) + side * Math.Asin(Math.Min(gap / 2 / r, 0.5));
            return new Point(center.X + Math.Cos(a) * r, center.Y + Math.Sin(a) * r);
        }

        var figure = new PathFigure { StartPoint = At(outer, fromDeg, +1), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new ArcSegment(At(outer, toDeg, -1), new Size(outer, outer), 0, false, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(At(inner, toDeg, -1), true));
        figure.Segments.Add(new ArcSegment(At(inner, fromDeg, +1), new Size(inner, inner), 0, false, SweepDirection.Counterclockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    public static BitmapSource Render(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawDrawing(Draw(size));
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static byte[] Png(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static void WritePng(string path, int size) => File.WriteAllBytes(path, Png(Render(size)));

    /// <summary>ICO mit PNG-komprimierten Einträgen (von Windows ab Vista in jeder Größe unterstützt).</summary>
    public static void WriteIco(string path)
    {
        var images = IconSizes.Select(s => (Size: s, Data: Png(Render(s)))).ToList();
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0);            // reserviert
        writer.Write((ushort)1);            // Typ: Icon
        writer.Write((ushort)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, data) in images)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);          // keine Palette
            writer.Write((byte)0);
            writer.Write((ushort)1);        // Farbebenen
            writer.Write((ushort)32);       // Bit pro Pixel
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in images) writer.Write(data);
    }
}

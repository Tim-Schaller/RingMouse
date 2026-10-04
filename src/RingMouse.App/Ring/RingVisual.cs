using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using RingMouse.Core.Config;
using RingMouse.Core.Ring;
using static RingMouse.Core.Localization.Lang;
using WpfPath = System.Windows.Shapes.Path;

namespace RingMouse.App.Ring;

/// <summary>
/// Gezeichneter Ring (WPF-Elemente). Das markierte Segment blendet weich in die Akzentfarbe und gleitet leicht nach
/// außen; beim Durchschieben zu einem Untermenü wandert es weiter hinaus. Ein Zeigerpunkt folgt der Raw-XY-Bewegung,
/// wenn der echte Mauszeiger beim Halten stillsteht. Ohne <see cref="RingSettings.Animation"/> alles sofort.
/// </summary>
internal sealed class RingVisual
{
    private const int HighlightMs = 110;
    private const int PulseMs = 70;
    private static readonly IEasingFunction Ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    private readonly RingTheme _theme;
    private readonly Palette _colors;
    private readonly bool _animate;
    private readonly List<SegmentParts?> _segments;
    private readonly string _defaultCenterText;
    private readonly double _centerFontSize;
    private readonly Dictionary<string, double> _centerFits = new(StringComparer.Ordinal);
    private readonly double _hoverLift;
    private readonly double _pushLift;
    private readonly double _deadzone;
    private readonly double _pointerLimit;
    private readonly Ellipse _pointer;
    private readonly TranslateTransform _pointerOffset = new();
    private double _pointerX;
    private double _pointerY;
    private bool _pointerShown;
    private int _highlighted = -1;

    private readonly record struct Palette(Color Segment, Color Highlight, Color Icon, Color IconHighlight, Color Label, Color LabelHighlight);

    private sealed class SegmentParts
    {
        public required Canvas Group { get; init; }
        public required TranslateTransform Offset { get; init; }
        public required SolidColorBrush Fill { get; init; }
        public SolidColorBrush? GlyphBrush { get; init; }
        public SolidColorBrush? LabelBrush { get; init; }
        public required double Angle { get; init; }
        public required bool IsSubmenu { get; init; }
        public required string Text { get; init; }
        public double Lift { get; set; }
    }

    private RingVisual(Canvas root, double size, RingTheme theme, bool animate, List<SegmentParts?> segments, TextBlock centerText,
        string defaultCenterText, Ellipse pointer, double k, double deadzone, double pointerLimit)
    {
        Root = root;
        Size = size;
        _theme = theme;
        _animate = animate;
        _segments = segments;
        _colors = new Palette(ColorOf(theme.Segment), ColorOf(theme.Highlight), ColorOf(theme.Icon), ColorOf(theme.IconHighlight),
            ColorOf(theme.Label), ColorOf(theme.LabelHighlight));
        CenterText = centerText;
        _defaultCenterText = defaultCenterText;
        _centerFontSize = centerText.FontSize;
        _pointer = pointer;
        _pointer.RenderTransform = _pointerOffset;
        _hoverLift = 4 * k;
        _pushLift = 8 * k;
        _deadzone = deadzone;
        _pointerLimit = pointerLimit;
        FitCenterText(defaultCenterText);
    }

    public Canvas Root { get; }
    public double Size { get; }
    public TextBlock CenterText { get; }

    public void SetHighlight(int index)
    {
        if (index == _highlighted) return;
        Style(_highlighted, on: false);
        _highlighted = index;
        Style(index, on: true);
        SetCenterText(index >= 0 && index < _segments.Count && _segments[index] is { } seg ? seg.Text : _defaultCenterText);
    }

    /// <summary>Fortschritt 0–1 zum Durchschieben: das markierte Untermenü-Segment wandert entsprechend weiter nach außen.</summary>
    public void SetPushProgress(double progress)
    {
        if (_highlighted < 0 || _highlighted >= _segments.Count || _segments[_highlighted] is not { IsSubmenu: true } seg) return;
        var lift = _hoverLift + Math.Clamp(progress, 0, 1) * _pushLift;
        if (progress <= 0 && Math.Abs(seg.Lift - _hoverLift) < 0.05) return; // Hover-Animation nicht stören
        SetLift(seg, lift, animate: false);
    }

    /// <summary>Zeigerpunkt an den Versatz (DIP ab Mitte) – nur sichtbar, wenn der echte Zeiger beim Halten stillsteht.</summary>
    public void UpdatePointer(double dx, double dy, bool visible)
    {
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length > _pointerLimit)
        {
            dx *= _pointerLimit / length;
            dy *= _pointerLimit / length;
        }
        // Weich folgen (bei 60 Hz ≈ 40 ms Nachlauf), ohne Ruckeln bei groben Raw-XY-Schritten
        var follow = _animate ? 0.5 : 1.0;
        _pointerX += (dx - _pointerX) * follow;
        _pointerY += (dy - _pointerY) * follow;
        _pointerOffset.X = _pointerX;
        _pointerOffset.Y = _pointerY;

        var show = visible && length >= Math.Min(8, _deadzone * 0.4);
        if (show == _pointerShown) return;
        _pointerShown = show;
        AnimateDouble(_pointer, UIElement.OpacityProperty, show ? 0.9 : 0, 90);
    }

    /// <summary>Kurzes Aufleuchten des gewählten Segments beim Ausführen.</summary>
    public void Pulse(int index)
    {
        if (!_animate || index < 0 || index >= _segments.Count || _segments[index] is not { } seg) return;
        var bright = Blend(_colors.Highlight, Colors.White, _theme.IsDark ? 0.35 : 0.25);
        seg.Fill.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(bright, TimeSpan.FromMilliseconds(PulseMs)) { AutoReverse = true, EasingFunction = Ease });
        SetLift(seg, _hoverLift * 1.8, animate: true);
    }

    private void Style(int index, bool on)
    {
        if (index < 0 || index >= _segments.Count || _segments[index] is not { } seg) return;
        AnimateColor(seg.Fill, on ? _colors.Highlight : _colors.Segment);
        if (seg.GlyphBrush is { } glyph) AnimateColor(glyph, on ? _colors.IconHighlight : _colors.Icon);
        if (seg.LabelBrush is { } label) AnimateColor(label, on ? _colors.LabelHighlight : _colors.Label);
        Panel.SetZIndex(seg.Group, on ? 1 : 0);
        SetLift(seg, on ? _hoverLift : 0, animate: true);
    }

    private void SetLift(SegmentParts seg, double lift, bool animate)
    {
        if (Math.Abs(seg.Lift - lift) < 0.05) return;
        seg.Lift = lift;
        var (x, y) = RingGeometry.PointAt(seg.Angle, lift);
        if (animate && _animate)
        {
            AnimateDouble(seg.Offset, TranslateTransform.XProperty, x, HighlightMs);
            AnimateDouble(seg.Offset, TranslateTransform.YProperty, y, HighlightMs);
            return;
        }
        seg.Offset.BeginAnimation(TranslateTransform.XProperty, null);
        seg.Offset.BeginAnimation(TranslateTransform.YProperty, null);
        seg.Offset.X = x;
        seg.Offset.Y = y;
    }

    private void AnimateColor(SolidColorBrush brush, Color to)
    {
        if (_animate)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(HighlightMs)) { EasingFunction = Ease });
            return;
        }
        brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        brush.Color = to;
    }

    private void AnimateDouble(IAnimatable target, DependencyProperty property, double to, int ms)
    {
        if (_animate)
        {
            target.BeginAnimation(property, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease });
            return;
        }
        target.BeginAnimation(property, null);
        ((DependencyObject)target).SetValue(property, to);
    }

    /// <summary>Text der Mitte setzen (sanft eingeblendet).</summary>
    private void SetCenterText(string text)
    {
        if (CenterText.Text == text) return;
        FitCenterText(text);
        if (_animate)
            CenterText.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = Ease });
    }

    /// <summary>Schrift so weit verkleinern, dass das längste Wort nicht mitten im Wort umbricht.</summary>
    private void FitCenterText(string text)
    {
        CenterText.Text = text;
        if (!_centerFits.TryGetValue(text, out var fontSize))
        {
            var typeface = new Typeface(CenterText.FontFamily, CenterText.FontStyle, CenterText.FontWeight, CenterText.FontStretch);
            var longest = 0.0;
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                longest = Math.Max(longest, MeasureDisplay(word, typeface, _centerFontSize));
            }
            var available = CenterText.Width - 2;
            fontSize = longest <= available ? _centerFontSize : Math.Max(_centerFontSize * 0.6, _centerFontSize * available / longest);
            _centerFits[text] = fontSize;
        }
        CenterText.FontSize = fontSize;
    }

    /// <summary>Platz um den Ring im Fenster – reicht für herausgleitende Segmente und die Zoom-Übergänge.</summary>
    public const double Margin = 14;

    public static RingVisual Build(RingDefinition ring, RingTheme theme, RingSettings settings, bool isSubmenu)
    {
        // Untergrenze wie im ConfigValidator: schützt die Geometrie (u.a. Math.Clamp(chord, 64, 132*k), outer = radius-3)
        // vor degenerierten Werten, falls Build je mit einer unvalidierten Config aufgerufen wird.
        var radius = Math.Max(settings.Radius, 80);
        var size = 2 * (radius + Margin);
        var c = size / 2;
        var k = radius / 150.0;
        var inner = Math.Max(settings.Deadzone + 6, radius * 0.36);
        var outer = radius - 3;
        var mid = (inner + outer) / 2;
        var count = Math.Max(ring.Segments.Count, 1);
        var span = 360.0 / count;
        var gapDeg = 3.0 / mid * 180.0 / Math.PI;

        var root = new Canvas { Width = size, Height = size, SnapsToDevicePixels = true };
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);
        // Eigene Ebene für die Deckkraft – Root.Opacity gehört den Öffnen-/Übergangsanimationen
        var layer = new Canvas { Width = size, Height = size, Opacity = Math.Clamp(settings.Opacity, 30, 100) / 100.0 };
        root.Children.Add(layer);
        var t = Math.Clamp(settings.TextScale, 50, 200) / 100.0; // Symbole und Schrift

        var disk = new Ellipse
        {
            Width = 2 * radius,
            Height = 2 * radius,
            Fill = theme.Background,
            Stroke = theme.Border,
            StrokeThickness = 1,
        };
        Canvas.SetLeft(disk, c - radius);
        Canvas.SetTop(disk, c - radius);
        layer.Children.Add(disk);

        var parts = new List<SegmentParts?>(count);
        var labelRadius = mid + 6 * k;
        var chord = 2 * labelRadius * Math.Sin(Math.Min(span, 170) / 2 * Math.PI / 180);
        var boxWidth = Math.Clamp(chord, 64, 132 * k);
        var boxHeight = (settings.ShowLabels ? 58 * k : 34 * k) * t;

        for (var i = 0; i < count; i++)
        {
            var segment = i < ring.Segments.Count ? ring.Segments[i] : null;
            var start = RingGeometry.SegmentStartAngle(i, count) + gapDeg / 2;
            var sweep = span - gapDeg;
            var angle = RingGeometry.SegmentCenterAngle(i, count);

            // Alles eines Segments in einer Gruppe, damit es gemeinsam nach außen gleiten kann
            var offset = new TranslateTransform();
            var group = new Canvas { Width = size, Height = size, IsHitTestVisible = false, RenderTransform = offset };
            layer.Children.Add(group);

            var fill = new SolidColorBrush(ColorOf(segment?.Action is null ? theme.SegmentEmpty : theme.Segment));
            group.Children.Add(new WpfPath { Data = Sector(c, inner, outer, start, sweep), Fill = fill });

            if (segment?.Action is null)
            {
                parts.Add(null);
                continue;
            }

            var (px, py) = RingGeometry.PointAt(angle, labelRadius);
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

            SolidColorBrush? glyphBrush = null;
            var icon = IconCatalog.Resolve(segment.Icon, segment.Action);
            if (icon.Glyph is { } g)
            {
                glyphBrush = new SolidColorBrush(ColorOf(theme.Icon));
                stack.Children.Add(new TextBlock
                {
                    Text = g.ToString(),
                    FontFamily = IconCatalog.IconFont,
                    FontSize = 22 * k * t,
                    Foreground = glyphBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
            }
            else if (icon.Image is { } img)
            {
                stack.Children.Add(new Image { Source = img, Width = 24 * k * t, Height = 24 * k * t, HorizontalAlignment = HorizontalAlignment.Center });
            }
            else if (icon.Text is { } txt)
            {
                glyphBrush = new SolidColorBrush(ColorOf(theme.Icon));
                stack.Children.Add(new TextBlock
                {
                    Text = txt,
                    FontSize = 16 * k * t,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = glyphBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
            }

            SolidColorBrush? labelBrush = null;
            if (settings.ShowLabels && !string.IsNullOrWhiteSpace(segment.Label))
            {
                labelBrush = new SolidColorBrush(ColorOf(theme.Label));
                var labelSize = FitWords(segment.Label, Math.Max(8, Math.Max(10, 11 * k) * t), boxWidth - 2); // das Feld begrenzt die Zeile
                stack.Children.Add(new TextBlock
                {
                    Text = segment.Label,
                    FontSize = labelSize,
                    Foreground = labelBrush,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.WrapWithOverflow,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = boxWidth + 8 * k,
                    MaxHeight = 2.7 * Math.Max(8, Math.Max(10, 11 * k) * t),
                    Margin = new Thickness(0, 3 * k, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
            }

            var box = new Grid { Width = boxWidth, Height = boxHeight, IsHitTestVisible = false };
            box.Children.Add(stack);
            Canvas.SetLeft(box, c + px - boxWidth / 2);
            Canvas.SetTop(box, c + py - boxHeight / 2);
            group.Children.Add(box);

            var isSubmenuSegment = segment.Action is SubmenuAction;
            if (isSubmenuSegment)
            {
                var (cx, cy) = RingGeometry.PointAt(angle, outer - 9 * k);
                var chevron = new TextBlock
                {
                    Text = IconCatalog.Glyphs["ChevronRight"].ToString(),
                    FontFamily = IconCatalog.IconFont,
                    FontSize = 9 * k,
                    Foreground = labelBrush ?? new SolidColorBrush(ColorOf(theme.Label)),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new RotateTransform(angle - 90),
                    Width = 12 * k,
                    Height = 12 * k,
                    TextAlignment = TextAlignment.Center,
                };
                Canvas.SetLeft(chevron, c + cx - 6 * k);
                Canvas.SetTop(chevron, c + cy - 6 * k);
                group.Children.Add(chevron);
            }

            parts.Add(new SegmentParts
            {
                Group = group,
                Offset = offset,
                Fill = fill,
                GlyphBrush = glyphBrush,
                LabelBrush = labelBrush,
                Angle = angle,
                IsSubmenu = isSubmenuSegment,
                Text = segment.Label,
            });
        }

        // Zeigerpunkt (Raw-XY beim Halten): zeigt die Bewegung, obwohl der echte Mauszeiger stillsteht
        // Standard hell mit dunklem Rand: sichtbar auf dem markierten (Akzent-)Segment wie auf grauen Flächen
        var pointerRadius = Math.Clamp(settings.PointerSize, 2, 40) / 2;
        var pointerColor = ColorValue.TryParse(settings.PointerColor, out var pc)
            ? Color.FromArgb(pc.A, pc.R, pc.G, pc.B)
            : ColorOf(theme.IconHighlight);
        var pointer = new Ellipse
        {
            Width = 2 * pointerRadius,
            Height = 2 * pointerRadius,
            Fill = new SolidColorBrush(pointerColor),
            Stroke = new SolidColorBrush(Luminance(pointerColor) > 0.5 ? Color.FromArgb(0x70, 0x00, 0x00, 0x00) : Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1.5,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(pointer, c - pointerRadius);
        Canvas.SetTop(pointer, c - pointerRadius);
        Panel.SetZIndex(pointer, 5);
        layer.Children.Add(pointer);

        // Mitte: Abbrechen bzw. Zurück im Untermenü
        var center = new Ellipse { Width = 2 * (inner - 4), Height = 2 * (inner - 4), Fill = theme.Center };
        Canvas.SetLeft(center, c - (inner - 4));
        Canvas.SetTop(center, c - (inner - 4));
        Panel.SetZIndex(center, 3);
        layer.Children.Add(center);

        var centerIcon = new TextBlock
        {
            Text = (isSubmenu ? IconCatalog.Glyphs["Back"] : IconCatalog.Glyphs["Cancel"]).ToString(),
            FontFamily = IconCatalog.IconFont,
            FontSize = 12 * k,
            Foreground = theme.CenterText,
            Opacity = 0.55,
            Width = 2 * inner,
            TextAlignment = TextAlignment.Center,
        };
        Canvas.SetLeft(centerIcon, c - inner);
        Canvas.SetTop(centerIcon, c - (inner - 4) + 9 * k);
        Panel.SetZIndex(centerIcon, 4);
        layer.Children.Add(centerIcon);

        var defaultText = ring.Title ?? (isSubmenu ? L("Back", "Zurück") : "");
        var centerText = new TextBlock
        {
            Text = defaultText,
            FontSize = Math.Max(9, Math.Max(10.5, 12 * k) * t),
            FontWeight = FontWeights.SemiBold,
            Foreground = theme.CenterText,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.WrapWithOverflow,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Width = 1.55 * (inner - 4),
            MaxHeight = 3.2 * 12 * k,
        };
        Canvas.SetLeft(centerText, c - centerText.Width / 2);
        Canvas.SetTop(centerText, c - 8 * k);
        Panel.SetZIndex(centerText, 4);
        layer.Children.Add(centerText);

        return new RingVisual(root, size, theme, settings.Animation, parts, centerText, defaultText, pointer, k,
            settings.Deadzone, outer - pointerRadius - 2);
    }

    /// <summary>Kreisringsektor um (c, c) von <paramref name="startDeg"/> über <paramref name="sweepDeg"/> (0° = oben, im Uhrzeigersinn).</summary>
    private static Geometry Sector(double c, double inner, double outer, double startDeg, double sweepDeg)
    {
        var endDeg = startDeg + sweepDeg;
        var (ox1, oy1) = RingGeometry.PointAt(startDeg, outer);
        var (ox2, oy2) = RingGeometry.PointAt(endDeg, outer);
        var (ix2, iy2) = RingGeometry.PointAt(endDeg, inner);
        var (ix1, iy1) = RingGeometry.PointAt(startDeg, inner);
        var large = sweepDeg > 180;

        var figure = new PathFigure { StartPoint = new Point(c + ox1, c + oy1), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new ArcSegment(new Point(c + ox2, c + oy2), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(new Point(c + ix2, c + iy2), true));
        figure.Segments.Add(new ArcSegment(new Point(c + ix1, c + iy1), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    private static Color ColorOf(Brush brush) => brush is SolidColorBrush solid ? solid.Color : Colors.Gray;

    private static readonly Typeface LabelFace = new(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>Schriftgröße, bei der das längste Wort (Umbruch an Leerzeichen und nach "/") in die Breite passt – nie größer als gewünscht.</summary>
    private static double FitWords(string text, double fontSize, double maxWidth)
    {
        var longest = 0.0;
        foreach (var word in text.Replace("/", "/ ").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            longest = Math.Max(longest, MeasureDisplay(word, LabelFace, fontSize));
        }
        return longest <= maxWidth ? fontSize : Math.Max(fontSize * 0.6, Math.Floor(fontSize * maxWidth / longest * 2) / 2);
    }

    /// <summary>Breite wie gezeichnet: der Ring rendert Text im Display-Modus (ganze Pixel, etwas breiter als "Ideal"), plus 4 % Reserve.</summary>
    private static double MeasureDisplay(string text, Typeface face, double fontSize)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, fontSize, Brushes.Black,
            null, TextFormattingMode.Display, 1.0);
        return formatted.WidthIncludingTrailingWhitespace * 1.04;
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

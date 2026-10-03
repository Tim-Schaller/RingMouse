using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using RingMouse.Core.Config;
using static RingMouse.Core.Localization.Lang;

namespace RingMouse.App.Settings;

/// <summary>
/// Farbwahl fürs Formular: "Automatisch", Farbfelder und "Eigene…" (Windows-Farbdialog).
/// Liefert "#RRGGBB" bzw. null für automatisch.
/// </summary>
internal sealed unsafe partial class ColorPicker : WrapPanel
{
    public static readonly string[] AccentPresets =
        ["#0078D4", "#0099BC", "#00B294", "#10893E", "#7FBA00", "#FFB900", "#F7630C", "#E81123", "#E3008C", "#8764B8", "#69797E"];

    public static readonly string[] RingPresets =
        ["#1B1B1B", "#2B2B2B", "#3C3C3C", "#1E2A3A", "#1F3329", "#3A2130", "#E6E6E6", "#FFFFFF", "#F2E8D5"];

    public static readonly string[] PointerPresets =
        ["#FFFFFF", "#1B1B1B", "#FFB900", "#E81123", "#10893E", "#0078D4", "#E3008C"];

    private static readonly int[] s_customColors = new int[16]; // eigene Farben des Windows-Dialogs, für diese Sitzung

    private readonly Action<string?> _onChange;
    private readonly List<(string? Value, Border Frame)> _choices = [];
    private readonly Border _customFrame;
    private readonly Border _customSwatch;
    private string? _value;

    public ColorPicker(string? value, IEnumerable<string> presets, string automaticText, Action<string?> onChange)
    {
        _onChange = onChange;
        _value = Normalize(value);
        Margin = new Thickness(0, 2, 0, 2);

        Children.Add(Choice(null, new TextBlock { Text = automaticText, Margin = new Thickness(6, 1, 6, 1), VerticalAlignment = VerticalAlignment.Center },
            automaticText));
        foreach (var preset in presets) Children.Add(Choice(Normalize(preset), Swatch(preset), preset));

        // Feld für eine eigene Farbe (sichtbar, sobald eine gewählt ist)
        _customSwatch = Swatch(_value ?? "#000000");
        _customFrame = Frame(new Button { Content = _customSwatch, Padding = new Thickness(3), ToolTip = L("Custom color", "Eigene Farbe") });
        ((Button)_customFrame.Child).Click += (_, _) => Select(CustomValue);
        Children.Add(_customFrame);

        var pick = new Button { Content = L("Custom…", "Eigene…"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(4, 2, 2, 2), VerticalAlignment = VerticalAlignment.Center };
        pick.Click += (_, _) => PickCustom();
        Children.Add(pick);
        UpdateSelection();
    }

    private string? CustomValue { get; set; }

    private Border Choice(string? value, object content, string tooltip)
    {
        var button = new Button { Content = content, Padding = new Thickness(3), ToolTip = tooltip, VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => Select(value);
        var frame = Frame(button);
        _choices.Add((value, frame));
        return frame;
    }

    private static Border Frame(Button button) => new()
    {
        Child = button,
        BorderThickness = new Thickness(2),
        CornerRadius = new CornerRadius(6),
        Margin = new Thickness(1),
        Padding = new Thickness(1),
    };

    private static Border Swatch(string color)
    {
        var c = ColorValue.TryParse(color, out var v) ? Color.FromArgb(v.A, v.R, v.G, v.B) : Colors.Transparent;
        return new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(c),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
        };
    }

    private void Select(string? value)
    {
        if (string.Equals(value, _value, StringComparison.OrdinalIgnoreCase)) return;
        _value = value;
        UpdateSelection();
        _onChange(value);
    }

    private void UpdateSelection()
    {
        var accent = SystemColors.HighlightBrush;
        var matched = false;
        foreach (var (value, frame) in _choices)
        {
            var selected = string.Equals(value, _value, StringComparison.OrdinalIgnoreCase);
            matched |= selected;
            frame.BorderBrush = selected ? accent : Brushes.Transparent;
        }

        // Eigene Farbe (nicht unter den Feldern): eigenes Feld zeigen und markieren
        if (!matched && _value is not null) CustomValue = _value;
        _customFrame.Visibility = CustomValue is null ? Visibility.Collapsed : Visibility.Visible;
        if (CustomValue is not null && ColorValue.TryParse(CustomValue, out var c))
        {
            _customSwatch.Background = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
            ((Button)_customFrame.Child).ToolTip = L($"Custom color {CustomValue}", $"Eigene Farbe {CustomValue}");
        }
        _customFrame.BorderBrush = !matched && _value is not null ? accent : Brushes.Transparent;
    }

    /// <summary>Windows-Farbdialog (comdlg32) – ohne WinForms-Abhängigkeit.</summary>
    private void PickCustom()
    {
        var start = ColorValue.TryParse(_value, out var v) ? v : ColorValue.FromRgb(0x00, 0x78, 0xD4);
        var owner = Window.GetWindow(this) is { } w ? new WindowInteropHelper(w).Handle : IntPtr.Zero;
        fixed (int* custom = s_customColors)
        {
            var cc = new CHOOSECOLOR
            {
                lStructSize = sizeof(CHOOSECOLOR),
                hwndOwner = owner,
                rgbResult = start.R | (start.G << 8) | (start.B << 16),
                lpCustColors = (IntPtr)custom,
                Flags = CC_RGBINIT | CC_FULLOPEN,
            };
            if (!ChooseColor(ref cc)) return;
            var rgb = cc.rgbResult;
            Select($"#{rgb & 0xFF:X2}{(rgb >> 8) & 0xFF:X2}{(rgb >> 16) & 0xFF:X2}");
        }
    }

    private static string? Normalize(string? value) => ColorValue.TryParse(value, out var c) ? c.ToString() : null;

    private const int CC_RGBINIT = 0x1;
    private const int CC_FULLOPEN = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct CHOOSECOLOR
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public int rgbResult;
        public IntPtr lpCustColors;
        public int Flags;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
    }

    [LibraryImport("comdlg32.dll", EntryPoint = "ChooseColorW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChooseColor(ref CHOOSECOLOR cc);
}

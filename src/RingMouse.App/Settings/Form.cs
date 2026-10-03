using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RingMouse.App.Settings;

/// <summary>Kleine Helfer für code-gebaute Formulare (einheitliche Abstände/Beschriftungen).</summary>
internal static class Form
{
    public const double LabelWidth = 170;

    public static FrameworkElement Row(string label, UIElement control, string? hint = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 0) };
        grid.Children.Add(text);
        FrameworkElement content = (FrameworkElement)control;
        if (hint is not null)
        {
            var stack = new StackPanel();
            stack.Children.Add(control);
            stack.Children.Add(Hint(hint));
            content = stack;
        }
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    public static TextBlock Hint(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.7,
        FontSize = 12,
        Margin = new Thickness(0, 3, 0, 0),
    };

    public static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 10, 0, 6),
    };

    public static GroupBox Group(string header, params UIElement[] children)
    {
        var stack = new StackPanel { Margin = new Thickness(8) };
        foreach (var c in children) stack.Children.Add(c);
        return new GroupBox { Header = header, Content = stack, Margin = new Thickness(0, 6, 0, 6) };
    }

    public static TextBox Text(string? value, Action<string> onChange, bool multiline = false, double? height = null)
    {
        var box = new TextBox
        {
            Text = value ?? "",
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        };
        if (height is { } h) box.Height = h;
        var last = box.Text;
        box.TextChanged += (_, _) =>
        {
            if (box.Text == last) return;
            last = box.Text;
            onChange(box.Text);
        };
        return box;
    }

    public static CheckBox Check(string text, bool value, Action<bool> onChange)
    {
        var box = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 4, 0, 4) };
        box.Checked += (_, _) => onChange(true);
        box.Unchecked += (_, _) => onChange(false);
        return box;
    }

    public static ComboBox Choice<T>(IEnumerable<(T Value, string Text)> items, T selected, Action<T> onChange)
    {
        var box = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
        var list = items.ToList();
        foreach (var (_, text) in list) box.Items.Add(text);
        box.SelectedIndex = Math.Max(0, list.FindIndex(i => EqualityComparer<T>.Default.Equals(i.Value, selected)));
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0) onChange(list[box.SelectedIndex].Value);
        };
        return box;
    }

    public static TextBox Number(double value, Action<double> onChange, double min, double max, string format = "0")
    {
        var box = new TextBox { Text = value.ToString(format, CultureInfo.CurrentCulture), Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
        box.TextChanged += (_, _) =>
        {
            if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) && v >= min && v <= max)
            {
                box.ClearValue(Control.BorderBrushProperty);
                onChange(v);
            }
            else
            {
                box.BorderBrush = Brushes.IndianRed;
            }
        };
        return box;
    }

    public static Slider Slider(double value, double min, double max, Action<double> onChange, double tick = 1)
    {
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            TickFrequency = tick,
            IsSnapToTickEnabled = true,
            Width = 260,
            HorizontalAlignment = HorizontalAlignment.Left,
            AutoToolTipPlacement = System.Windows.Controls.Primitives.AutoToolTipPlacement.TopLeft,
        };
        slider.ValueChanged += (_, e) =>
        {
            if (Math.Abs(e.NewValue - e.OldValue) > 1e-9) onChange(e.NewValue);
        };
        return slider;
    }

    public static Button Button(string text, Action onClick, bool accent = false)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(12, 4, 12, 4), MinWidth = 32 };
        if (accent) b.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
        b.Click += (_, _) => onClick();
        return b;
    }

    public static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        foreach (var b in buttons) panel.Children.Add(b);
        return panel;
    }

    public static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(0, 0, 8, 0),
    };
}

using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using RingMouse.Core.Input;

namespace RingMouse.App.Settings;

/// <summary>Textfeld für Tastenkürzel: frei tippbar oder per "Aufnehmen" die nächste Kombination übernehmen.</summary>
internal sealed partial class KeyCaptureBox : UserControl
{
    private readonly TextBox _box;
    private readonly ToggleButton _record;
    private readonly TextBlock _status;

    public KeyCaptureBox(string? value, Action<string> onChange)
    {
        _box = new TextBox { Text = value ?? "", MinWidth = 220 };
        _record = new ToggleButton { Content = "⌨ Aufnehmen", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
        _status = new TextBlock { Opacity = 0.75, FontSize = 12, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap };

        var line = new DockPanel();
        DockPanel.SetDock(_record, Dock.Right);
        line.Children.Add(_record);
        line.Children.Add(_box);
        var stack = new StackPanel();
        stack.Children.Add(line);
        stack.Children.Add(_status);
        Content = stack;

        _box.TextChanged += (_, _) =>
        {
            onChange(_box.Text);
            Validate();
        };
        _record.Checked += (_, _) =>
        {
            _box.Focus();
            _status.Text = "Jetzt die Tastenkombination drücken …";
        };
        _record.Unchecked += (_, _) => Validate();
        _box.PreviewKeyDown += OnPreviewKeyDown;
        Validate();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_record.IsChecked != true) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.None)
        {
            e.Handled = true;
            return;
        }

        var sb = new StringBuilder();
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl+");
        if (mods.HasFlag(ModifierKeys.Shift)) sb.Append("Shift+");
        if (mods.HasFlag(ModifierKeys.Alt)) sb.Append("Alt+");
        if (mods.HasFlag(ModifierKeys.Windows) || Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) sb.Append("Win+");
        sb.Append(KeyName(key));
        _box.Text = sb.ToString();
        _record.IsChecked = false;
        e.Handled = true;
    }

    private void Validate()
    {
        if (_record.IsChecked == true) return;
        _status.Text = KeyChordParser.TryParse(_box.Text, out _, out var error)
            ? "Beispiele: Ctrl+Shift+S · Win+. · Alt+F4 · Folge: Ctrl+K, Ctrl+C"
            : $"⚠ {error}";
    }

    private static string KeyName(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return key.ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((int)(key - Key.D0)).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return $"Num{(int)(key - Key.NumPad0)}";
        if (key is >= Key.F1 and <= Key.F24) return $"F{(int)(key - Key.F1) + 1}";
        switch (key)
        {
            case Key.Return: return "Enter";
            case Key.Escape: return "Esc";
            case Key.Space: return "Space";
            case Key.Tab: return "Tab";
            case Key.Back: return "Backspace";
            case Key.Delete: return "Delete";
            case Key.Insert: return "Insert";
            case Key.Home: return "Home";
            case Key.End: return "End";
            case Key.PageUp: return "PageUp";
            case Key.PageDown: return "PageDown";
            case Key.Left: return "Left";
            case Key.Right: return "Right";
            case Key.Up: return "Up";
            case Key.Down: return "Down";
            case Key.Snapshot: return "PrintScreen";
            case Key.Pause: return "Pause";
            case Key.Apps: return "Apps";
            case Key.Multiply: return "Multiply";
            case Key.Add: return "Add";
            case Key.Subtract: return "Subtract";
            case Key.Divide: return "Divide";
            case Key.Decimal: return "Decimal";
            case Key.VolumeUp: return "VolumeUp";
            case Key.VolumeDown: return "VolumeDown";
            case Key.VolumeMute: return "VolumeMute";
            case Key.MediaPlayPause: return "MediaPlayPause";
            case Key.MediaNextTrack: return "MediaNext";
            case Key.MediaPreviousTrack: return "MediaPrevious";
        }

        // OEM-Tasten: Zeichen laut aktuellem Layout (".", "#", "ü" …)
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        var ch = (char)(MapVirtualKey(vk, 2 /* MAPVK_VK_TO_CHAR */) & 0xFFFF);
        return ch switch
        {
            '\0' => key.ToString(),
            '+' => "Plus",
            ',' => "Comma",
            _ => char.ToLowerInvariant(ch).ToString(),
        };
    }

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static partial uint MapVirtualKey(uint uCode, uint uMapType);
}

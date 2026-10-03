using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RingMouse.App.Ring;
using RingMouse.App.Settings;
using RingMouse.App.Tray;
using RingMouse.Core.Config;
using RingMouse.Core.Localization;
using RingMouse.Device;
using RingMouse.HidPlusPlus;
using RingMouse.HidPlusPlus.Features;
using RingMouse.Platform.Autostart;

namespace RingMouse.App.Diagnostics;

/// <summary>
/// "RingMouse.exe --render-ui &lt;ordner&gt;": rendert Ring (hell/dunkel), Tray-Icons und alle Einstellungsseiten
/// als PNG – zur visuellen Kontrolle ohne echte Maus, je Sprache in einen Unterordner (en, de).
/// Startet keine Geräte und ändert nichts.
/// </summary>
internal static class UiSnapshots
{
    public static void RenderAll(string directory)
    {
        foreach (var (language, folder) in new[] { (UiLanguage.English, "en"), (UiLanguage.German, "de") })
        {
            Lang.Apply(language);
            RenderLanguage(Path.Combine(directory, folder));
        }
    }

    private static void RenderLanguage(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = DefaultConfig.Create();
        config.Ring.Animation = false; // Momentaufnahmen: Endzustand statt Animationsbeginn

        foreach (var dark in new[] { true, false })
        {
            var theme = RingTheme.Create(dark ? ThemePreference.Dark : ThemePreference.Light);

            // Halten + Durchschieben: Untermenü unten (Platz 4) markiert und halb herausgezogen, Zeigerpunkt sichtbar
            var push = RingVisual.Build(config.Rings["main"], theme, config.Ring, false);
            push.SetHighlight(4);
            push.UpdatePointer(0, 108, visible: true);
            push.SetPushProgress(0.6);
            SaveElement(push.Root, push.Size, push.Size, 1.5, Path.Combine(directory, $"ring-main-push-{(dark ? "dark" : "light")}.png"),
                dark ? Color.FromRgb(0x45, 0x55, 0x6B) : Color.FromRgb(0xC9, 0xD6, 0xE3));

            // Eigene Farben/Größen aus dem Bereich "Aussehen"
            var look = ConfigSerializer.Clone(config.Ring);
            (look.RingColor, look.AccentColor, look.Opacity, look.TextScale, look.PointerColor, look.PointerSize) =
                (dark ? "#1E2A3A" : "#F2E8D5", "#FFB900", 90, 125, "#E81123", 14);
            var custom = RingVisual.Build(config.Rings["main"], RingTheme.Create(look), look, false);
            custom.SetHighlight(1);
            var (cx, cy) = RingMouse.Core.Ring.RingGeometry.PointAt(45, 95);
            custom.UpdatePointer(cx, cy, visible: true);
            SaveElement(custom.Root, custom.Size, custom.Size, 1.5, Path.Combine(directory, $"ring-main-custom-{(dark ? "dark" : "light")}.png"),
                dark ? Color.FromRgb(0x45, 0x55, 0x6B) : Color.FromRgb(0xC9, 0xD6, 0xE3));

            foreach (var (name, ring) in config.Rings)
            {
                var visual = RingVisual.Build(ring, theme, config.Ring, !name.Equals("main", StringComparison.OrdinalIgnoreCase));
                visual.SetHighlight(name.Equals("main", StringComparison.OrdinalIgnoreCase) ? 2 : 0);
                SaveElement(visual.Root, visual.Size, visual.Size, 1.5, Path.Combine(directory, $"ring-{name}-{(dark ? "dark" : "light")}.png"),
                    dark ? Color.FromRgb(0x45, 0x55, 0x6B) : Color.FromRgb(0xC9, 0xD6, 0xE3));
            }
        }

        // Tray-Icons in verschiedenen Zuständen und Größen nebeneinander
        // Zeilen: Originalgröße 16/20/24 px, darunter 16 und 24 px pixelgenau vergrößert (×4 bzw. ×3)
        var states = new (int? Percent, bool Charging, bool Connected)[]
            { (85, false, true), (20, false, true), (6, false, true), (55, true, true), (null, true, true), (100, true, true), (40, false, false), (null, false, true) };
        foreach (var light in new[] { false, true })
        {
            var rows = new StackPanel { Background = new SolidColorBrush(light ? Color.FromRgb(0xEE, 0xEE, 0xEE) : Color.FromRgb(0x20, 0x20, 0x20)) };
            foreach (var (size, zoom) in new[] { (16, 1), (20, 1), (24, 1), (16, 4), (24, 3) })
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 4) };
                foreach (var (p, c, conn) in states)
                {
                    var image = new Image { Source = BatteryIcon.Render(p, c, conn, light, size), Width = size * zoom, Height = size * zoom, Margin = new Thickness(0, 0, 10, 0) };
                    RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
                    row.Children.Add(image);
                }
                rows.Children.Add(row);
            }
            rows.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            SaveElement(rows, rows.DesiredSize.Width, rows.DesiredSize.Height, 1, Path.Combine(directory, $"tray-{(light ? "light" : "dark")}.png"), null);
        }

        // Einstellungsfenster, jede Seite
        var window = new SettingsWindow(new SnapshotHost(config)) { Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false, SuppressClosePrompt = true };
        window.Show();
        var background = RingMouse.Platform.Display.SystemTheme.AppsUseLightTheme ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x20, 0x20, 0x20);
        var tabs = FindChild<TabControl>(window);
        string? dirtyAfter = null;
        for (var i = 0; tabs is not null && i < tabs.Items.Count; i++)
        {
            tabs.SelectedIndex = i;
            window.UpdateLayout();
            DoEvents();
            Wait(600); // Aufklapp-Animationen (Fluent-Expander) auslaufen lassen
            var header = ((TabItem)tabs.Items[i]).Header?.ToString() ?? i.ToString();
            if (window.IsDirty && dirtyAfter is null) dirtyAfter = header;
            SaveVisual(window, (int)window.ActualWidth, (int)window.ActualHeight, Path.Combine(directory, $"settings-{i + 1}-{Sanitize(header)}.png"), background);

            // lange Seiten zusätzlich nach unten gescrollt
            if (FindChild<ScrollViewer>((DependencyObject)((TabItem)tabs.Items[i]).Content) is { ScrollableHeight: > 20 } scroll)
            {
                scroll.ScrollToVerticalOffset(scroll.ScrollableHeight);
                window.UpdateLayout();
                Wait(200);
                SaveVisual(window, (int)window.ActualWidth, (int)window.ActualHeight, Path.Combine(directory, $"settings-{i + 1}-{Sanitize(header)}-unten.png"), background);
                scroll.ScrollToVerticalOffset(0);
            }
        }
        File.WriteAllText(Path.Combine(directory, "settings-dirty.txt"), window.IsDirty ? $"DIRTY after page {dirtyAfter}" : "clean");
        window.Close();

        // Ersteinrichtung: erkannte Taste ("Fertig!") und ohne Tastendruck ("Nochmal")
        foreach (var (name, result) in new[] { ("fertig", (ButtonEvent?)new ButtonEvent("demo", 0x00FD, true, 0)), ("ohne-taste", null) })
        {
            var setup = new RingSetupWindow(new SnapshotHost(config) { CaptureResult = result }, (_, _) => { })
            {
                Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false,
            };
            setup.Show();
            DoEvents();
            Wait(300);
            setup.UpdateLayout();
            SaveVisual(setup, (int)setup.ActualWidth, (int)setup.ActualHeight, Path.Combine(directory, $"ring-setup-{name}.png"), background);
            setup.Close();
        }
    }

    private static void SaveElement(FrameworkElement element, double width, double height, double scale, string path, Color? background)
    {
        var host = new Border { Child = element, Width = width, Height = height, Background = background is { } b ? new SolidColorBrush(b) : Brushes.Transparent };
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(host);
        Write(bmp, path);
        host.Child = null;
    }

    private static void SaveVisual(Visual visual, int width, int height, string path, Color background)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var rect = new Rect(0, 0, width, height);
            dc.DrawRectangle(new SolidColorBrush(background), null, rect);
            dc.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, rect);
        }
        var bmp = new RenderTargetBitmap(Math.Max(1, width), Math.Max(1, height), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        Write(bmp, path);
    }

    private static void Write(BitmapSource bmp, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static void Wait(int ms)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(ms), System.Windows.Threading.DispatcherPriority.Background,
            (s, _) =>
            {
                ((System.Windows.Threading.DispatcherTimer)s!).Stop();
                frame.Continue = false;
            }, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void DoEvents()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }

    private static string Sanitize(string s) => new(s.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray());

    /// <summary>Beispiel-Host mit einer simulierten MX Vertical (Werte aus der echten Probe-Aufzeichnung).</summary>
    private sealed class SnapshotHost(RingMouseConfig config) : ISettingsHost
    {
        public RingMouseConfig CurrentConfig { get; } = config;

        public IReadOnlyList<DeviceSnapshot> Devices { get; } =
        [
            new DeviceSnapshot
            {
                Key = "B020-demo-FF",
                Name = "MX Vertical Advanced Ergonomic Mouse",
                ProductId = 0xB020,
                UnitId = "1A2B3C4D",
                Connection = "Bluetooth LE",
                State = DeviceState.Ready,
                Kind = DeviceKind.Mouse,
                Protocol = new ProtocolVersion(4, 5),
                Battery = BatteryDecoder.DecodeStatus([20, 10, 0]),
                BatteryTimestamp = DateTimeOffset.Now,
                Dpi = 1000,
                SupportedDpi = Enumerable.Range(4, 37).Select(i => i * 100).ToList(),
                Controls =
                [
                    ControlInfo.Parse(0, [0x00, 0x50, 0x00, 0x38, 0x11, 0, 1, 1, 0x04]),
                    ControlInfo.Parse(1, [0x00, 0x51, 0x00, 0x39, 0x11, 0, 1, 1, 0x04]),
                    ControlInfo.Parse(2, [0x00, 0x52, 0x00, 0x3A, 0x71, 0, 2, 3, 0x05]),
                    ControlInfo.Parse(3, [0x00, 0x53, 0x00, 0x3C, 0x71, 0, 2, 3, 0x05]),
                    ControlInfo.Parse(4, [0x00, 0x56, 0x00, 0x3E, 0x71, 0, 2, 3, 0x05]),
                    ControlInfo.Parse(5, [0x00, 0xFD, 0x00, 0xD2, 0x71, 0, 2, 3, 0x05]),
                    ControlInfo.Parse(6, [0x00, 0xD7, 0x00, 0xB4, 0xA0, 0, 3, 0, 0x03]),
                ],
                DivertedControls = new HashSet<ushort> { 0x00FD },
            },
        ];

        public AutostartService Autostart { get; } = new();
        public void SaveConfig(RingMouseConfig config) { }
        public string? BackupConfig() => null;
        public void OpenConfigFile() { }
        public void OpenLogs() { }
        public void OpenConfigFolder() { }
        public void ReconnectAll() { }
        public Task<int?> SetDpiNowAsync(string deviceKey, int dpi) => Task.FromResult<int?>(dpi);
        public ButtonEvent? CaptureResult { get; init; }
        public Task<ButtonEvent?> CaptureButtonAsync(Action? armed, CancellationToken ct) => Task.FromResult(CaptureResult);
        public void PreviewRing(RingDefinition ring, RingSettings settings, bool isSubmenu) { }
        public void Restart() { }
    }
}

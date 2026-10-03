using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RingMouse.Core.Config;

namespace RingMouse.App.Settings;

/// <summary>Einstellungsfenster: bearbeitet eine Arbeitskopie der Config und speichert sie validiert zurück.</summary>
internal sealed class SettingsWindow : Window
{
    private readonly ISettingsHost _host;
    private readonly TabControl _tabs = new() { Margin = new Thickness(8, 8, 8, 0) };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 0) };
    private SettingsContext _ctx = null!;
    private ButtonsPage? _buttonsPage;
    private DevicePage? _devicePage;
    private AutostartMode _savedAutostart;
    private bool _dirty;

    /// <summary>Beim Schließen nicht nach ungespeicherten Änderungen fragen (Diagnose-Snapshots).</summary>
    internal bool SuppressClosePrompt { get; set; }

    internal bool IsDirty => _dirty;

    public SettingsWindow(ISettingsHost host)
    {
        _host = host;
        Title = "RingMouse – Einstellungen";
        Width = 1080;
        Height = 760;
        MinWidth = 860;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try
        {
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/ringmouse.ico"));
        }
        catch
        {
            // Icon optional
        }

        var save = new Button { Content = "Speichern", MinWidth = 110, Padding = new Thickness(12, 5, 12, 5), IsDefault = false };
        save.SetResourceReference(StyleProperty, "AccentButtonStyle");
        save.Click += (_, _) => Save();
        var close = new Button { Content = "Schließen", MinWidth = 110, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(save);
        buttons.Children.Add(close);
        var bottom = new DockPanel { Margin = new Thickness(8, 10, 16, 14), LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(_tabs);
        Content = root;

        // Kompaktere Listeneinträge als der Fluent-Standard
        try
        {
            var baseStyle = TryFindResource(typeof(ListBoxItem)) as Style;
            var itemStyle = new Style(typeof(ListBoxItem), baseStyle);
            itemStyle.Setters.Add(new Setter(MinHeightProperty, 26.0));
            itemStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(10, 3, 10, 3)));
            Resources[typeof(ListBoxItem)] = itemStyle;
        }
        catch
        {
            // Standardstil behalten
        }

        Load(ConfigSerializer.Clone(host.CurrentConfig));
    }

    private void Load(RingMouseConfig working)
    {
        var selected = _tabs.SelectedIndex;
        _ctx = new SettingsContext(_host, working);
        _ctx.Dirty += () =>
        {
            _dirty = true;
            _status.Text = "Ungespeicherte Änderungen";
            _status.Foreground = (Brush)FindResource(SystemColors.ControlTextBrushKey);
        };
        _savedAutostart = working.General.Autostart;
        _buttonsPage = new ButtonsPage(_ctx);
        _devicePage = new DevicePage(_ctx);
        _tabs.Items.Clear();
        _tabs.Items.Add(new TabItem { Header = "Ringe", Content = new RingsPage(_ctx) });
        _tabs.Items.Add(new TabItem { Header = "Tasten", Content = _buttonsPage });
        _tabs.Items.Add(new TabItem { Header = "Profile", Content = new ProfilesPage(_ctx) });
        _tabs.Items.Add(new TabItem { Header = "Gerät & Akku", Content = _devicePage });
        _tabs.Items.Add(new TabItem { Header = "Allgemein", Content = new GeneralPage(_ctx, ResetToDefaults) });
        _tabs.SelectedIndex = selected < 0 ? 0 : selected;
        _dirty = false;
        _status.Text = "";
    }

    /// <summary>Config wurde (z.B. im Editor) geändert: ohne ungespeicherte Änderungen neu laden.</summary>
    public void OnExternalConfigChanged(RingMouseConfig config)
    {
        if (_dirty)
        {
            _status.Text = "Hinweis: config.json wurde außerhalb geändert – Speichern überschreibt diese Änderungen.";
            return;
        }
        if (ConfigSerializer.Serialize(config) == ConfigSerializer.Serialize(_ctx.Config)) return; // eigenes Speichern
        Load(ConfigSerializer.Clone(config));
        _status.Text = $"Neu geladen ({DateTime.Now:HH:mm:ss}) – config.json wurde geändert.";
    }

    /// <summary>Aufruf vom Host, wenn sich Geräte geändert haben.</summary>
    public void OnDevicesChanged()
    {
        _devicePage?.Refresh();
        _buttonsPage?.Refresh();
    }

    public bool Save()
    {
        var issues = ConfigValidator.Validate(_ctx.Config);
        var errors = issues.Where(i => i.Severity == IssueSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            _status.Text = "Nicht gespeichert – bitte korrigieren:\n" + string.Join("\n", errors.Take(4).Select(e => $"• {e.Path}: {e.Message}"));
            _status.Foreground = Brushes.IndianRed;
            return false;
        }

        _host.SaveConfig(ConfigSerializer.Clone(_ctx.Config));
        var message = $"Gespeichert {DateTime.Now:HH:mm:ss}" + (issues.Count > 0 ? $" ({issues.Count} Hinweis(e): {issues[0].Message})" : "");

        if (_ctx.Config.General.Autostart != _savedAutostart && Environment.ProcessPath is { } exe)
        {
            var result = _host.Autostart.Apply(_ctx.Config.General.Autostart, exe);
            message += result.Success ? $" · {result.Message}" : $" · Autostart: {result.Message}";
            if (result.Success) _savedAutostart = _ctx.Config.General.Autostart;
        }

        _dirty = false;
        _status.Text = message;
        _status.Foreground = (Brush)FindResource(SystemColors.ControlTextBrushKey);
        return true;
    }

    private void ResetToDefaults()
    {
        if (MessageBox.Show(this, "Alle Einstellungen auf den Standard zurücksetzen?\n\nDie bisherige config.json wird vorher als Sicherung abgelegt.",
                "Standard wiederherstellen", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        string? backup;
        try
        {
            backup = _host.BackupConfig();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Sicherung fehlgeschlagen, es wurde nichts zurückgesetzt:\n{ex.Message}", "Standard wiederherstellen",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Load(DefaultConfig.Create());
        _dirty = true;
        _status.Text = "Standard geladen – zum Übernehmen speichern" + (backup is null ? "" : $" · Sicherung: {Path.GetFileName(backup)}");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_dirty && !SuppressClosePrompt)
        {
            var answer = MessageBox.Show(this, "Änderungen speichern?", "RingMouse", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !Save()))
            {
                e.Cancel = true;
                return;
            }
        }
        base.OnClosing(e);
    }
}

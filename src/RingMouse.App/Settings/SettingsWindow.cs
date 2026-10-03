using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using RingMouse.Core.Config;
using RingMouse.Core.Import;
using RingMouse.Platform.Import;
using static RingMouse.Core.Localization.Lang;

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
        Title = L("RingMouse – Settings", "RingMouse – Einstellungen");
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

        var save = new Button { Content = L("Save", "Speichern"), MinWidth = 110, Padding = new Thickness(12, 5, 12, 5), IsDefault = false };
        save.SetResourceReference(StyleProperty, "AccentButtonStyle");
        save.Click += (_, _) => Save();
        var close = new Button { Content = L("Close", "Schließen"), MinWidth = 110, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0) };
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
            _status.Text = L("Unsaved changes", "Ungespeicherte Änderungen");
            _status.Foreground = (Brush)FindResource(SystemColors.ControlTextBrushKey);
        };
        _savedAutostart = working.General.Autostart;
        _buttonsPage = new ButtonsPage(_ctx);
        _devicePage = new DevicePage(_ctx);
        _tabs.Items.Clear();
        _tabs.Items.Add(new TabItem { Header = L("Rings", "Ringe"), Content = new RingsPage(_ctx) });
        _tabs.Items.Add(new TabItem { Header = L("Buttons", "Tasten"), Content = _buttonsPage });
        _tabs.Items.Add(new TabItem { Header = L("Profiles", "Profile"), Content = new ProfilesPage(_ctx) });
        _tabs.Items.Add(new TabItem { Header = L("Device & battery", "Gerät & Akku"), Content = _devicePage });
        _tabs.Items.Add(new TabItem
        {
            Header = L("General", "Allgemein"),
            Content = new GeneralPage(_ctx, new GeneralPageCommands(ResetToDefaults, SaveAndRestart, Export, ImportFromFile, ImportFromOptionsPlus)),
        });
        _tabs.SelectedIndex = selected < 0 ? 0 : selected;
        _dirty = false;
        _status.Text = "";
    }

    /// <summary>Config wurde (z.B. im Editor) geändert: ohne ungespeicherte Änderungen neu laden.</summary>
    public void OnExternalConfigChanged(RingMouseConfig config)
    {
        if (_dirty)
        {
            _status.Text = L("Note: config.json was changed outside the app – saving overwrites those changes.",
                "Hinweis: config.json wurde außerhalb geändert – Speichern überschreibt diese Änderungen.");
            return;
        }
        if (ConfigSerializer.Serialize(config) == ConfigSerializer.Serialize(_ctx.Config)) return; // eigenes Speichern
        Load(ConfigSerializer.Clone(config));
        _status.Text = L($"Reloaded ({DateTime.Now:HH:mm:ss}) – config.json was changed.", $"Neu geladen ({DateTime.Now:HH:mm:ss}) – config.json wurde geändert.");
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
            _status.Text = L("Not saved – please correct:\n", "Nicht gespeichert – bitte korrigieren:\n") +
                           string.Join("\n", errors.Take(4).Select(e => $"• {e.Path}: {e.Message}"));
            _status.Foreground = Brushes.IndianRed;
            return false;
        }

        _host.SaveConfig(ConfigSerializer.Clone(_ctx.Config));
        var message = L($"Saved {DateTime.Now:HH:mm:ss}", $"Gespeichert {DateTime.Now:HH:mm:ss}") +
                      (issues.Count > 0 ? L($" ({issues.Count} note(s): {issues[0].Message})", $" ({issues.Count} Hinweis(e): {issues[0].Message})") : "");

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

    // ------------------------------------------------------------------ Export / Import

    /// <summary>Die Konfiguration, wie sie im Fenster steht, als JSON-Datei speichern.</summary>
    private void Export()
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"RingMouse-{DateTime.Now:yyyy-MM-dd}.json",
            Filter = L("RingMouse configuration (*.json)|*.json", "RingMouse-Konfiguration (*.json)|*.json"),
            DefaultExt = ".json",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, ConfigSerializer.Serialize(_ctx.Config), new UTF8Encoding(false));
            _status.Text = L($"Exported: {Path.GetFileName(dialog.FileName)}", $"Exportiert: {Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, L("Export", "Exportieren"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>RingMouse-Config (.json) oder in Options+ exportiertes Actions-Ring-Preset (.lp5) importieren.</summary>
    private void ImportFromFile()
    {
        if (!EnsureSaved()) return;
        var dialog = new OpenFileDialog
        {
            Filter = L("RingMouse configuration or Options+ ring preset (*.json;*.lp5)|*.json;*.lp5|All files (*.*)|*.*",
                "RingMouse-Konfiguration oder Options+-Ring-Preset (*.json;*.lp5)|*.json;*.lp5|Alle Dateien (*.*)|*.*"),
        };
        if (dialog.ShowDialog(this) != true) return;

        ImportResult result;
        try
        {
            var name = Path.GetFileName(dialog.FileName);
            if (Path.GetExtension(dialog.FileName).Equals(".lp5", StringComparison.OrdinalIgnoreCase))
            {
                result = new ImportResult(L($"Options+ ring preset {name}", $"Options+-Ring-Preset {name}"));
                ActionsRingImporter.ImportRing(OptionsPlusFiles.ReadPreset(dialog.FileName), DefaultConfig.MainRing, result, L("Actions Ring", "Actions Ring"));
            }
            else
            {
                result = RingMouseFileImporter.Load(File.ReadAllText(dialog.FileName), name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            MessageBox.Show(this, L($"The file could not be read:\n{ex.Message}", $"Die Datei konnte nicht gelesen werden:\n{ex.Message}"),
                L("Import", "Importieren"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        RunImport(new ImportWindow(result));
    }

    /// <summary>Ring, Tastenbelegungen und App-Profile aus einer installierten Logi Options+ übernehmen.</summary>
    private void ImportFromOptionsPlus()
    {
        if (!EnsureSaved()) return;
        using var importer = OptionsPlusFiles.Load();
        if (!importer.HasAnything)
        {
            MessageBox.Show(this, L("No Logi Options+ settings were found on this computer.", "Auf diesem Rechner wurden keine Einstellungen von Logi Options+ gefunden."),
                L("Import", "Importieren"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        RunImport(ImportWindow.ForOptionsPlus(importer, _host.Devices));
    }

    private void RunImport(ImportWindow window)
    {
        window.Owner = this;
        if (window.ShowDialog() != true) return;
        try
        {
            var backup = _host.ApplyImport(window.Result.Config, window.SelectedParts);
            Load(ConfigSerializer.Clone(_host.CurrentConfig));
            _status.Text = L("Imported", "Importiert") + (backup is null ? "" : L($" · backup: {Path.GetFileName(backup)}", $" · Sicherung: {Path.GetFileName(backup)}"));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, L($"Nothing was imported:\n{ex.Message}", $"Es wurde nichts importiert:\n{ex.Message}"),
                L("Import", "Importieren"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Vor einem Import: ungespeicherte Änderungen speichern oder verwerfen (false = abbrechen).</summary>
    private bool EnsureSaved()
    {
        if (!_dirty) return true;
        var answer = MessageBox.Show(this, L("Save the current changes before importing?", "Aktuelle Änderungen vor dem Import speichern?"),
            L("Import", "Importieren"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) return Save();
        Load(ConfigSerializer.Clone(_host.CurrentConfig)); // verwerfen
        return true;
    }

    /// <summary>Speichern und RingMouse neu starten, z.B. damit eine neue Sprache überall gilt.</summary>
    private void SaveAndRestart()
    {
        if (_dirty && !Save()) return;
        SuppressClosePrompt = true;
        _host.Restart();
    }

    private void ResetToDefaults()
    {
        if (MessageBox.Show(this,
                L("Reset all settings to the defaults?\n\nThe current config.json is saved as a backup first.",
                    "Alle Einstellungen auf den Standard zurücksetzen?\n\nDie bisherige config.json wird vorher als Sicherung abgelegt."),
                L("Restore defaults", "Standard wiederherstellen"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        string? backup;
        try
        {
            backup = _host.BackupConfig();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this,
                L($"Backup failed, nothing was reset:\n{ex.Message}", $"Sicherung fehlgeschlagen, es wurde nichts zurückgesetzt:\n{ex.Message}"),
                L("Restore defaults", "Standard wiederherstellen"), MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Load(DefaultConfig.Create());
        _dirty = true;
        _status.Text = L("Defaults loaded – save to apply", "Standard geladen – zum Übernehmen speichern") +
                       (backup is null ? "" : L($" · Backup: {Path.GetFileName(backup)}", $" · Sicherung: {Path.GetFileName(backup)}"));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_dirty && !SuppressClosePrompt)
        {
            var answer = MessageBox.Show(this, L("Save changes?", "Änderungen speichern?"), "RingMouse", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !Save()))
            {
                e.Cancel = true;
                return;
            }
        }
        base.OnClosing(e);
    }
}

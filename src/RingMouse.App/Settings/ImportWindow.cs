using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RingMouse.Core.Config;
using RingMouse.Core.Import;
using RingMouse.Device;
using static RingMouse.Core.Localization.Lang;

namespace RingMouse.App.Settings;

/// <summary>
/// Import-Vorschau: zeigt Quelle, wählbare Teile (Ringe, Tasten, Profile, Geräte, Einstellungen) und den Bericht,
/// was übernommen wird und was nicht. Übernehmen schließt mit DialogResult = true; angewendet wird vom Aufrufer.
/// </summary>
internal sealed class ImportWindow : Window
{
    private readonly Func<string?, ImportResult>? _rebuild;
    private readonly StackPanel _partsPanel = new();
    private readonly ListBox _notes = new() { MinHeight = 160, MaxHeight = 300 };
    private readonly Dictionary<ImportParts, CheckBox> _checks = [];
    private readonly Button _apply;
    private readonly TextBlock _execWarningText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border _execWarning;

    public ImportWindow(ImportResult result, IReadOnlyList<OptionsPlusDevice>? mice = null, string? selectedMouse = null,
        Func<string?, ImportResult>? rebuild = null)
    {
        Result = result;
        _rebuild = rebuild;
        Title = L("Import", "Importieren");
        Width = 720;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        try
        {
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/ringmouse.ico"));
        }
        catch
        {
            // Icon optional
        }

        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        root.Children.Add(Form.Heading(L("Import", "Importieren")));
        root.Children.Add(Form.Row(L("Source", "Quelle"), new TextBlock { Text = result.Source, TextWrapping = TextWrapping.Wrap }));

        if (mice is { Count: > 0 } && rebuild is not null)
        {
            var current = selectedMouse ?? mice[0].SlotPrefix;
            root.Children.Add(Form.Row(L("Buttons of", "Tasten von"), Form.Choice(mice.Select(m => (m.SlotPrefix, m.DisplayName)), current, prefix =>
            {
                Result = rebuild(prefix);
                Refresh(keepSelection: true);
            }), L("Logi Options+ stores the button assignments per mouse.", "Logi Options+ speichert die Tastenbelegung je Maus.")));
        }

        root.Children.Add(Form.Group(L("Import these parts", "Diese Teile übernehmen"), _partsPanel,
            Form.Hint(L("Selected parts replace rings, profiles and device settings with the same name and the same buttons; everything else stays. " +
                        "A backup of the current configuration is created first.",
                "Gewählte Teile ersetzen gleichnamige Ringe, Profile und Geräte-Einstellungen sowie dieselben Tasten; alles andere bleibt. " +
                "Vorher wird eine Sicherung der aktuellen Konfiguration angelegt."))));

        _execWarning = new Border
        {
            Child = _execWarningText,
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0xE0, 0x6C, 0x00)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x00)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 4, 0, 8),
            Visibility = Visibility.Collapsed,
        };
        root.Children.Add(_execWarning);
        root.Children.Add(Form.Group(L("Report", "Bericht"), _notes));

        _apply = Form.Button(L("Import", "Übernehmen"), () => DialogResult = true, accent: true);
        var cancel = Form.Button(L("Cancel", "Abbrechen"), () => DialogResult = false);
        var buttons = Form.Buttons(_apply, cancel);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        root.Children.Add(buttons);
        Content = root;
        Refresh(keepSelection: false);
    }

    /// <summary>Aktuelles Import-Ergebnis (bei Options+ abhängig von der gewählten Maus).</summary>
    public ImportResult Result { get; private set; }

    public ImportParts SelectedParts =>
        _checks.Where(kv => kv.Value.IsEnabled && kv.Value.IsChecked == true).Aggregate(ImportParts.None, (all, kv) => all | kv.Key);

    private void Refresh(bool keepSelection)
    {
        var previous = keepSelection ? _checks.ToDictionary(kv => kv.Key, kv => kv.Value.IsChecked == true) : null;
        _checks.Clear();
        _partsPanel.Children.Clear();
        var c = Result.Config;
        AddPart(ImportParts.Rings, L($"Rings ({c.Rings.Count})", $"Ringe ({c.Rings.Count})"), c.Rings.Count > 0, previous);
        AddPart(ImportParts.Buttons, L($"Button assignments ({c.Buttons.Count})", $"Tastenbelegungen ({c.Buttons.Count})"), c.Buttons.Count > 0, previous);
        AddPart(ImportParts.Profiles, L($"App profiles ({c.Profiles.Count})", $"App-Profile ({c.Profiles.Count})"), c.Profiles.Count > 0, previous);
        AddPart(ImportParts.Devices, L($"Device settings ({c.Devices.Count})", $"Geräte-Einstellungen ({c.Devices.Count})"), c.Devices.Count > 0, previous);
        if (Result.HasSettings)
            AddPart(ImportParts.Settings, L("General settings and ring appearance", "Allgemeine Einstellungen und Aussehen des Rings"), true, previous,
                defaultChecked: false);

        _notes.Items.Clear();
        foreach (var note in Result.Notes)
        {
            var imported = note.Kind == ImportNoteKind.Imported;
            var item = new TextBlock { Text = (imported ? "✔ " : "✘ ") + note.Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 620 };
            if (!imported) item.Opacity = 0.7;
            _notes.Items.Add(item);
        }
        if (Result.Notes.Count == 0)
            _notes.Items.Add(new TextBlock { Text = L("Nothing found to import.", "Nichts zum Importieren gefunden."), Foreground = Brushes.Gray });
        UpdateExecWarning(c);
        UpdateApply();
    }

    /// <summary>Warnt sichtbar, wenn die zu importierende Config beim Auslösen Programme/Befehle ausführt.</summary>
    private void UpdateExecWarning(RingMouseConfig config)
    {
        var exec = ConfigInspection.CountExecutable(config);
        if (exec.Total == 0)
        {
            _execWarning.Visibility = Visibility.Collapsed;
            return;
        }

        var parts = new List<string>();
        if (exec.Launch > 0) parts.Add(L($"{exec.Launch} program start(s)", $"{exec.Launch} Programmstart(s)"));
        if (exec.PowerShell > 0) parts.Add(L($"{exec.PowerShell} PowerShell action(s)", $"{exec.PowerShell} PowerShell-Aktion(en)"));
        var what = string.Join(L(" and ", " und "), parts);
        var admin = exec.AnyElevated ? L(", some requesting admin rights (UAC)", ", teils mit Adminrechten (UAC)") : "";
        _execWarningText.Text = "⚠ " + L(
            $"This configuration contains {what} that run programs or commands when the button/segment is triggered{admin}. " +
            "Only import configurations from a source you trust.",
            $"Diese Konfiguration enthält {what}, die beim Auslösen der Taste/des Segments Programme oder Befehle ausführen{admin}. " +
            "Importiere nur Konfigurationen aus einer Quelle, der du vertraust.");
        _execWarning.Visibility = Visibility.Visible;
    }

    private void AddPart(ImportParts part, string text, bool available, Dictionary<ImportParts, bool>? previous, bool defaultChecked = true)
    {
        var check = new CheckBox
        {
            Content = text,
            IsEnabled = available,
            IsChecked = available && (previous?.GetValueOrDefault(part, defaultChecked) ?? defaultChecked),
            Margin = new Thickness(0, 2, 0, 2),
        };
        check.Checked += (_, _) => UpdateApply();
        check.Unchecked += (_, _) => UpdateApply();
        _checks[part] = check;
        _partsPanel.Children.Add(check);
    }

    private void UpdateApply()
    {
        if (_apply is not null) _apply.IsEnabled = SelectedParts != ImportParts.None;
    }

    /// <summary>Import aus Logi Options+ mit Mausauswahl; vorgewählt ist eine verbundene Maus, sonst die erste.</summary>
    public static ImportWindow ForOptionsPlus(OptionsPlusImporter importer, IReadOnlyList<DeviceSnapshot> connected)
    {
        var mouse = importer.Mice.FirstOrDefault(m => connected.Any(d => d.Name.Contains(m.DisplayName, StringComparison.OrdinalIgnoreCase)))
                    ?? importer.Mice.FirstOrDefault();
        return new ImportWindow(importer.Build(mouse?.SlotPrefix), importer.Mice, mouse?.SlotPrefix, importer.Build);
    }
}

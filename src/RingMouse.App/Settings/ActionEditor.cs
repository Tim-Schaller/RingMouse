using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RingMouse.Core.Config;

namespace RingMouse.App.Settings;

public enum ActionContext
{
    /// <summary>Segment eines Rings (Untermenü erlaubt, "Ring öffnen"/"Originalfunktion" nicht).</summary>
    Segment,
    /// <summary>Tastenbelegung (Ring öffnen, Originalfunktion erlaubt, Untermenü nicht).</summary>
    Button,
}

/// <summary>Editor für eine Aktion: Typauswahl + passende Felder. Bearbeitet das Objekt direkt.</summary>
internal sealed class ActionEditor : UserControl
{
    private static readonly (string Type, string Text, ActionContext? Only)[] s_types =
    [
        ("ring", "Ring öffnen", ActionContext.Button),
        ("native", "Originalfunktion (nicht umleiten)", ActionContext.Button),
        ("keys", "Tastenkombination", null),
        ("media", "Medientaste", null),
        ("launch", "Programm / Datei / URL starten", null),
        ("snippet", "Text einfügen (Textbaustein)", null),
        ("powershell", "PowerShell ausführen", null),
        ("screenshot", "Bildschirmfoto (Ausschnitt)", null),
        ("submenu", "Untermenü (weiterer Ring)", ActionContext.Segment),
        ("system", "Systemfunktion", null),
        ("dpi", "DPI setzen / umschalten", null),
        ("appKeys", "Hotkey an bestimmte App (z.B. Spotify)", null),
        ("mouse", "Maustaste", null),
        ("sequence", "Sequenz (mehrere Aktionen, JSON)", null),
        ("none", "Keine Funktion", null),
    ];

    private readonly ActionContext _context;
    private readonly Func<IEnumerable<string>> _ringNames;
    private readonly ComboBox _type = new() { MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _fields = new();
    private readonly List<(string Type, string Text)> _available;
    private ActionDefinition? _action;
    private bool _loading;

    public ActionEditor(ActionContext context, Func<IEnumerable<string>> ringNames)
    {
        _context = context;
        _ringNames = ringNames;
        _available = s_types.Where(t => t.Only is null || t.Only == context).Select(t => (t.Type, t.Text)).ToList();
        foreach (var (_, text) in _available) _type.Items.Add(text);
        _type.SelectionChanged += (_, _) => OnTypeChanged();

        var stack = new StackPanel();
        stack.Children.Add(Form.Row("Aktion", _type));
        stack.Children.Add(_fields);
        Content = stack;
    }

    /// <summary>Wird bei jeder Änderung ausgelöst (Typ oder Feld).</summary>
    public event Action? Changed;

    /// <summary>Aktuelle Aktion (null bei "Originalfunktion" im Tastenkontext).</summary>
    public ActionDefinition? Action
    {
        get => _action is NativeAction && _context == ActionContext.Button ? null : _action;
        set
        {
            _loading = true;
            _action = value ?? (_context == ActionContext.Button ? new NativeAction() : new KeysAction());
            var index = _available.FindIndex(t => t.Type == _action.TypeName);
            _type.SelectedIndex = index < 0 ? 0 : index;
            BuildFields();
            _loading = false;
        }
    }

    private void OnTypeChanged()
    {
        if (_loading || _type.SelectedIndex < 0) return;
        var type = _available[_type.SelectedIndex].Type;
        if (_action?.TypeName == type) return;
        _action = Create(type);
        BuildFields();
        Changed?.Invoke();
    }

    private ActionDefinition Create(string type) => type switch
    {
        "ring" => new OpenRingAction { Ring = _ringNames().FirstOrDefault() ?? "main" },
        "native" => new NativeAction(),
        "keys" => new KeysAction { Keys = "Ctrl+C" },
        "media" => new MediaAction(),
        "launch" => new LaunchAction(),
        "snippet" => new SnippetAction(),
        "powershell" => new PowerShellAction(),
        "screenshot" => new ScreenshotAction(),
        "submenu" => new SubmenuAction { Ring = _ringNames().FirstOrDefault(n => !n.Equals("main", StringComparison.OrdinalIgnoreCase)) ?? "" },
        "system" => new SystemAction(),
        "dpi" => new DpiAction(),
        "appKeys" => new AppKeysAction { Process = "Spotify.exe", Keys = "Ctrl+S" },
        "mouse" => new MouseAction(),
        "sequence" => new SequenceAction(),
        _ => new NoneAction(),
    };

    private void Touch()
    {
        if (!_loading) Changed?.Invoke();
    }

    private void BuildFields()
    {
        _fields.Children.Clear();
        switch (_action)
        {
            case OpenRingAction r:
                _fields.Children.Add(Form.Row("Ring", RingChooser(r.Ring, v => { r.Ring = v; Touch(); })));
                break;

            case SubmenuAction s:
                _fields.Children.Add(Form.Row("Ring", RingChooser(s.Ring, v => { s.Ring = v; Touch(); }),
                    "Öffnet diesen Ring an derselben Stelle; die Mitte führt zurück."));
                break;

            case NativeAction:
                _fields.Children.Add(Form.Hint("Die Taste wird nicht umgeleitet und behält ihre normale Funktion."));
                break;

            case KeysAction k:
                _fields.Children.Add(Form.Row("Tasten", new KeyCaptureBox(k.Keys, v => { k.Keys = v; Touch(); })));
                break;

            case MediaAction m:
                _fields.Children.Add(Form.Row("Taste", Form.Choice(Enum.GetValues<MediaKey>().Select(v => (v, new MediaAction { Key = v }.Describe())),
                    m.Key, v => { m.Key = v; Touch(); })));
                break;

            case LaunchAction l:
                _fields.Children.Add(Form.Row("Ziel", PathBox(l.Target, v => { l.Target = v; Touch(); }, "Programme|*.exe;*.lnk;*.bat;*.cmd|Alle Dateien|*.*"),
                    "Pfad, Datei, Ordner, URL (https://…), URI (ms-settings:, spotify:…) oder shell:AppsFolder\\<AUMID>. Umgebungsvariablen erlaubt."));
                _fields.Children.Add(Form.Row("Argumente", Form.Text(l.Arguments, v => { l.Arguments = Null(v); Touch(); })));
                _fields.Children.Add(Form.Row("Arbeitsordner", Form.Text(l.WorkingDirectory, v => { l.WorkingDirectory = Null(v); Touch(); })));
                _fields.Children.Add(Form.Row("", Form.Check("Mit Adminrechten starten (UAC-Abfrage)", l.Elevated, v => { l.Elevated = v; Touch(); })));
                break;

            case SnippetAction sn:
                _fields.Children.Add(Form.Row("Text", Form.Text(sn.Text, v => { sn.Text = v; Touch(); }, multiline: true, height: 90),
                    "Platzhalter: {now:dd.MM.yyyy HH:mm} · {date} · {time} · {clipboard} · {user} · {computer} · {newline}"));
                _fields.Children.Add(Form.Row("Eingabe", Form.Choice(
                    [(SnippetMode.Type, "Tippen (Unicode, layoutunabhängig)"), (SnippetMode.Paste, "Über Zwischenablage einfügen (Strg+V, danach wiederherstellen)")],
                    sn.Mode, v => { sn.Mode = v; Touch(); }), "\"Einfügen\" ist bei langen Texten und Remote-Sitzungen oft zuverlässiger."));
                break;

            case PowerShellAction ps:
                _fields.Children.Add(Form.Row("Skript (.ps1)", PathBox(ps.Script, v => { ps.Script = Null(v); Touch(); }, "PowerShell-Skripte|*.ps1|Alle Dateien|*.*"),
                    "Entweder ein Skript oder unten einen Befehl angeben."));
                _fields.Children.Add(Form.Row("oder Befehl", Form.Text(ps.Command, v => { ps.Command = Null(v); Touch(); }, multiline: true, height: 70)));
                _fields.Children.Add(Form.Row("Argumente", Form.Text(ps.Arguments, v => { ps.Arguments = Null(v); Touch(); })));
                _fields.Children.Add(Form.Row("", Form.Check("Versteckt ausführen (kein Fenster)", ps.Hidden, v => { ps.Hidden = v; Touch(); })));
                _fields.Children.Add(Form.Row("", Form.Check("PowerShell 7 (pwsh.exe) statt Windows PowerShell", ps.UsePwsh, v => { ps.UsePwsh = v; Touch(); })));
                _fields.Children.Add(Form.Row("", Form.Check("Mit Adminrechten (UAC-Abfrage)", ps.Elevated, v => { ps.Elevated = v; Touch(); })));
                break;

            case ScreenshotAction:
                _fields.Children.Add(Form.Hint("Öffnet die Bildschirmausschnitt-Auswahl des Snipping Tools (wie Win+Shift+S)."));
                break;

            case SystemAction sys:
                _fields.Children.Add(Form.Row("Funktion", Form.Choice(Enum.GetValues<SystemCommand>().Select(v => (v, new SystemAction { Command = v }.Describe())),
                    sys.Command, v => { sys.Command = v; Touch(); })));
                break;

            case DpiAction d:
                _fields.Children.Add(Form.Row("DPI-Werte", Form.Text(string.Join(", ", d.Values), v =>
                {
                    d.Values = v.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n > 0).ToList();
                    Touch();
                }), "Ein Wert = setzen, mehrere = bei jedem Auslösen zum nächsten Wert wechseln (z.B. 1000, 2000)."));
                break;

            case AppKeysAction a:
                _fields.Children.Add(Form.Row("Prozess", ProcessBox(a.Process, v => { a.Process = v; Touch(); }), "z.B. Spotify.exe – das Fenster wird kurz aktiviert."));
                _fields.Children.Add(Form.Row("Tasten", new KeyCaptureBox(a.Keys, v => { a.Keys = v; Touch(); }),
                    "Spotify: Strg+S Zufall · Strg+R Wiederholen · Alt+Shift+B Gefällt mir"));
                _fields.Children.Add(Form.Row("", Form.Check("Danach vorheriges Fenster wieder aktivieren", a.RestoreFocus, v => { a.RestoreFocus = v; Touch(); })));
                _fields.Children.Add(Form.Row("Falls nicht aktiv", Form.Text(a.LaunchIfNotRunning, v => { a.LaunchIfNotRunning = Null(v); Touch(); }),
                    "Optional: Programm/URI, das gestartet wird, wenn der Prozess nicht läuft."));
                break;

            case MouseAction mouse:
                _fields.Children.Add(Form.Row("Taste", Form.Choice(
                    [(MouseButtonKind.Left, "Links"), (MouseButtonKind.Right, "Rechts"), (MouseButtonKind.Middle, "Mitte"),
                     (MouseButtonKind.Back, "Zurück (X1)"), (MouseButtonKind.Forward, "Vor (X2)")],
                    mouse.Button, v => { mouse.Button = v; Touch(); })));
                break;

            case SequenceAction seq:
                _fields.Children.Add(SequenceEditor(seq));
                break;

            case NoneAction:
                _fields.Children.Add(Form.Hint("Die Taste wird umgeleitet, löst aber nichts aus."));
                break;
        }
    }

    private FrameworkElement SequenceEditor(SequenceAction seq)
    {
        var status = Form.Hint("");
        var json = JsonSerializer.Serialize(seq.Steps, ConfigSerializer.Options);
        var box = Form.Text(json, text =>
        {
            try
            {
                var steps = JsonSerializer.Deserialize<List<ActionDefinition>>(text, ConfigSerializer.Options) ?? [];
                seq.Steps = steps;
                status.Text = $"✔ {steps.Count} Schritt(e)";
                Touch();
            }
            catch (JsonException ex)
            {
                status.Text = $"⚠ {ex.Message.Split('\n')[0]}";
            }
        }, multiline: true, height: 150);
        box.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas");
        var stack = new StackPanel();
        stack.Children.Add(Form.Row("Schritte (JSON)", box,
            "Beispiel: [ {\"type\":\"keys\",\"keys\":\"Ctrl+C\"}, {\"type\":\"delay\",\"ms\":300}, {\"type\":\"launch\",\"target\":\"https://…\"} ]"));
        stack.Children.Add(Form.Row("", status));
        return stack;
    }

    private ComboBox RingChooser(string value, Action<string> onChange)
    {
        var box = new ComboBox { IsEditable = true, MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var name in _ringNames()) box.Items.Add(name);
        box.Text = value;
        var last = value;
        void Report(string text)
        {
            if (text == last) return; // Template-Aufbau setzt den Text erneut – keine echte Änderung
            last = text;
            onChange(text);
        }
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is string s) Report(s);
        };
        box.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Report(box.Text)));
        return box;
    }

    private static FrameworkElement PathBox(string? value, Action<string> onChange, string filter)
    {
        var box = Form.Text(value, onChange);
        var browse = new Button { Content = "…", Width = 32, Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
            if (dialog.ShowDialog() == true) box.Text = dialog.FileName;
        };
        var dock = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        dock.Children.Add(browse);
        dock.Children.Add(box);
        return dock;
    }

    private static FrameworkElement ProcessBox(string value, Action<string> onChange)
    {
        var box = new ComboBox { IsEditable = true, MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left, Text = value };
        var last = value;
        void Report(string text)
        {
            if (text == last) return;
            last = text;
            onChange(text);
        }
        box.DropDownOpened += (_, _) =>
        {
            var current = box.Text;
            box.Items.Clear();
            foreach (var name in RunningWindowProcesses()) box.Items.Add(name);
            box.Text = current;
        };
        box.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Report(box.Text)));
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is string s) Report(s);
        };
        return box;
    }

    /// <summary>Prozesse mit Fenster (für Profil- und App-Hotkey-Auswahl).</summary>
    public static IReadOnlyList<string> RunningWindowProcesses()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(p.MainWindowTitle)) names.Add(p.ProcessName + ".exe");
            }
            catch
            {
                // Zugriff verweigert
            }
            finally
            {
                p.Dispose();
            }
        }
        return names.ToList();
    }

    private static string? Null(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

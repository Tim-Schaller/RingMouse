using System.Windows;
using System.Windows.Controls;
using RingMouse.Core.Config;
using RingMouse.HidPlusPlus.Features;
using static RingMouse.Core.Localization.Lang;

namespace RingMouse.App.Settings;

/// <summary>App-Profile: Prozesse, Tasten-Überschreibungen, Ring-Ersetzungen. Das erste passende Profil gewinnt.</summary>
internal sealed class ProfilesPage : UserControl
{
    private readonly SettingsContext _ctx;
    private readonly ListBox _list = new() { MinHeight = 200 };
    private readonly StackPanel _editor = new();
    private int _index = -1;

    public ProfilesPage(SettingsContext ctx)
    {
        _ctx = ctx;
        var grid = new Grid { Margin = new Thickness(12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        left.Children.Add(Form.Heading(L("Profiles", "Profile")));
        left.Children.Add(_list);
        left.Children.Add(Form.Buttons(Form.Button(L("New", "Neu"), Add), Form.Button(L("Delete", "Löschen"), Remove), Form.Button("↑", () => Move(-1)),
            Form.Button("↓", () => Move(1))));
        left.Children.Add(Form.Hint(L("Profiles apply when the process of the foreground window matches (the first profile in the list wins). " +
                                      "Without a matching profile, the default applies. Profiles inherit everything they don't override.",
                                      "Profile gelten, wenn der Prozess des Vordergrundfensters passt (erstes Profil in der Liste gewinnt). " +
                                      "Ohne passendes Profil gilt der Standard. Profile erben alles, was sie nicht überschreiben.")));
        grid.Children.Add(left);

        var right = new StackPanel();
        right.Children.Add(_editor);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        Content = Form.Scroll(grid);

        _list.SelectionChanged += (_, _) =>
        {
            _index = _list.SelectedIndex;
            BuildEditor();
        };
        Refresh(0);
    }

    private void Refresh(int select)
    {
        _list.Items.Clear();
        foreach (var p in _ctx.Config.Profiles)
            _list.Items.Add($"{(p.Enabled ? "" : L("(off) ", "(aus) "))}{p.Name} – {string.Join(", ", p.Processes)}");
        _list.SelectedIndex = Math.Min(select, _ctx.Config.Profiles.Count - 1);
        if (_ctx.Config.Profiles.Count == 0) BuildEditor();
    }

    private void BuildEditor()
    {
        _editor.Children.Clear();
        if (_index < 0 || _index >= _ctx.Config.Profiles.Count)
        {
            _editor.Children.Add(Form.Heading(L("No profile", "Kein Profil")));
            _editor.Children.Add(Form.Hint(L("Use \"New\" to create an app profile, e.g. for excel.exe with its own ring or a different assignment for Back/Forward.",
                "Mit \"Neu\" ein App-Profil anlegen, z.B. für excel.exe mit eigenem Ring oder anderer Belegung von Zurück/Vor.")));
            return;
        }

        var p = _ctx.Config.Profiles[_index];
        var index = _index;
        void Dirty()
        {
            _ctx.MarkDirty();
            _list.Items[index] = $"{(p.Enabled ? "" : L("(off) ", "(aus) "))}{p.Name} – {string.Join(", ", p.Processes)}";
            _list.SelectedIndex = index;
        }

        _editor.Children.Add(Form.Heading(L("Edit profile", "Profil bearbeiten")));
        _editor.Children.Add(Form.Row("Name", Form.Text(p.Name, v => { p.Name = v; Dirty(); })));
        _editor.Children.Add(Form.Row("", Form.Check(L("Active", "Aktiv"), p.Enabled, v => { p.Enabled = v; Dirty(); })));

        var processes = Form.Text(string.Join(", ", p.Processes), v =>
        {
            p.Processes = v.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            Dirty();
        });
        var pick = new ComboBox { MinWidth = 200, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        pick.Items.Add(L("＋ add running program …", "＋ laufendes Programm übernehmen …"));
        pick.SelectedIndex = 0;
        pick.DropDownOpened += (_, _) =>
        {
            while (pick.Items.Count > 1) pick.Items.RemoveAt(1);
            foreach (var name in ActionEditor.RunningWindowProcesses()) pick.Items.Add(name);
        };
        pick.SelectionChanged += (_, _) =>
        {
            if (pick.SelectedIndex <= 0 || pick.SelectedItem is not string name) return;
            if (!p.Processes.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                p.Processes.Add(name);
                processes.Text = string.Join(", ", p.Processes);
            }
            pick.SelectedIndex = 0;
        };
        var procStack = new StackPanel();
        procStack.Children.Add(processes);
        procStack.Children.Add(pick);
        _editor.Children.Add(Form.Row(L("Processes", "Prozesse"), procStack,
            L("Comma-separated, e.g. excel.exe, winword.exe; wildcards * and ? allowed (\"*teams*\").",
                "Kommagetrennt, z.B. excel.exe, winword.exe; Platzhalter * und ? erlaubt (\"*teams*\").")));

        _editor.Children.Add(BuildButtonOverrides(p, Dirty));
        _editor.Children.Add(BuildRingOverrides(p, Dirty));
    }

    private FrameworkElement BuildButtonOverrides(ProfileDefinition p, Action dirty)
    {
        var list = new ListBox { MinHeight = 90 };
        var editorHost = new StackPanel();
        void RefreshList()
        {
            list.Items.Clear();
            foreach (var (key, action) in p.Buttons) list.Items.Add($"{key} {(ControlIds.TryParse(key, out var c) ? ControlIds.GetName(c) : "")} → {action.Describe()}");
        }

        var controls = _ctx.KnownControls();
        var choose = new ComboBox { MinWidth = 220 };
        foreach (var c in controls) choose.Items.Add($"{ControlIds.Format(c.Cid)} {c.Name}");
        var add = Form.Button(L("Override", "Überschreiben"), () =>
        {
            if (choose.SelectedIndex < 0) return;
            var key = ControlIds.Format(controls[choose.SelectedIndex].Cid);
            if (!p.Buttons.ContainsKey(key)) p.Buttons[key] = new KeysAction { Keys = "Ctrl+Z" };
            RefreshList();
            dirty();
        });
        var remove = Form.Button(L("Remove", "Entfernen"), () =>
        {
            if (list.SelectedIndex < 0) return;
            p.Buttons.Remove(p.Buttons.Keys.ElementAt(list.SelectedIndex));
            editorHost.Children.Clear();
            RefreshList();
            dirty();
        });
        list.SelectionChanged += (_, _) =>
        {
            editorHost.Children.Clear();
            if (list.SelectedIndex < 0 || list.SelectedIndex >= p.Buttons.Count) return;
            var key = p.Buttons.Keys.ElementAt(list.SelectedIndex);
            var editor = new ActionEditor(ActionContext.Button, _ctx.RingNames) { Action = p.Buttons[key] };
            editor.Changed += () =>
            {
                p.Buttons[key] = editor.Action ?? new NativeAction();
                dirty();
            };
            editorHost.Children.Add(editor);
        };
        RefreshList();

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        addRow.Children.Add(choose);
        addRow.Children.Add(new Border { Width = 6 });
        addRow.Children.Add(add);
        addRow.Children.Add(remove);
        return Form.Group(L("Buttons in this profile", "Tasten in diesem Profil"), list, addRow, editorHost);
    }

    private FrameworkElement BuildRingOverrides(ProfileDefinition p, Action dirty)
    {
        var list = new ListBox { MinHeight = 60 };
        void RefreshList()
        {
            list.Items.Clear();
            foreach (var (from, to) in p.Rings) list.Items.Add($"{from} → {to}");
        }
        var from = new ComboBox { MinWidth = 140 };
        var to = new ComboBox { MinWidth = 140 };
        foreach (var name in _ctx.RingNames())
        {
            from.Items.Add(name);
            to.Items.Add(name);
        }
        from.SelectedIndex = 0;
        var add = Form.Button(L("Replace", "Ersetzen"), () =>
        {
            if (from.SelectedItem is not string f || to.SelectedItem is not string t || f.Equals(t, StringComparison.OrdinalIgnoreCase)) return;
            p.Rings[f] = t;
            RefreshList();
            dirty();
        });
        var remove = Form.Button(L("Remove", "Entfernen"), () =>
        {
            if (list.SelectedIndex < 0) return;
            p.Rings.Remove(p.Rings.Keys.ElementAt(list.SelectedIndex));
            RefreshList();
            dirty();
        });
        RefreshList();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(from);
        row.Children.Add(new TextBlock { Text = " → ", VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(to);
        row.Children.Add(new Border { Width = 6 });
        row.Children.Add(add);
        row.Children.Add(remove);
        return Form.Group(L("Replace rings in this profile", "Ringe in diesem Profil ersetzen"), list, row,
            Form.Hint(L("Example: \"main → main-excel\" shows the ring \"main-excel\" in Excel when the ring button is pressed.",
                "Beispiel: \"main → main-excel\" zeigt in Excel beim Druck auf die Ring-Taste den Ring \"main-excel\".")));
    }

    private void Add()
    {
        _ctx.Config.Profiles.Add(new ProfileDefinition { Name = L("New profile", "Neues Profil"), Processes = ["notepad.exe"] });
        _ctx.MarkDirty();
        Refresh(_ctx.Config.Profiles.Count - 1);
    }

    private void Remove()
    {
        if (_index < 0 || _index >= _ctx.Config.Profiles.Count) return;
        _ctx.Config.Profiles.RemoveAt(_index);
        _ctx.MarkDirty();
        Refresh(Math.Max(0, _index - 1));
    }

    private void Move(int delta)
    {
        var target = _index + delta;
        if (_index < 0 || target < 0 || target >= _ctx.Config.Profiles.Count) return;
        (_ctx.Config.Profiles[_index], _ctx.Config.Profiles[target]) = (_ctx.Config.Profiles[target], _ctx.Config.Profiles[_index]);
        _ctx.MarkDirty();
        Refresh(target);
    }
}

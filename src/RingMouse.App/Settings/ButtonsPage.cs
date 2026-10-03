using System.Windows;
using System.Windows.Controls;
using RingMouse.Core.Config;
using RingMouse.HidPlusPlus.Features;

namespace RingMouse.App.Settings;

/// <summary>Standard-Tastenbelegung (CID → Aktion). Die Tastenliste kommt per 0x1B04 von den verbundenen Geräten.</summary>
internal sealed class ButtonsPage : UserControl
{
    private readonly SettingsContext _ctx;
    private readonly ListBox _list = new() { MinHeight = 260 };
    private readonly StackPanel _editor = new();
    private IReadOnlyList<SettingsContext.KnownControl> _controls = [];
    private ushort? _selected;

    public ButtonsPage(SettingsContext ctx)
    {
        _ctx = ctx;
        var grid = new Grid { Margin = new Thickness(12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        left.Children.Add(Form.Heading("Tasten"));
        left.Children.Add(_list);
        var manual = new TextBox { Width = 110, Margin = new Thickness(0, 0, 6, 0) };
        var add = Form.Button("CID hinzufügen", () =>
        {
            if (!ControlIds.TryParse(manual.Text, out var cid)) return;
            _ctx.Config.Buttons[ControlIds.Format(cid)] = new KeysAction { Keys = "Ctrl+C" };
            _ctx.MarkDirty();
            Refresh(cid);
        });
        var manualRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        manualRow.Children.Add(manual);
        manualRow.Children.Add(add);
        left.Children.Add(manualRow);
        left.Children.Add(Form.Hint("Die Tasten werden von der Maus selbst gemeldet (ringmouse-probe controls). " +
                                    "Für nicht verbundene Geräte die CID manuell eintragen, z.B. 0x00FD."));
        grid.Children.Add(left);

        var right = new StackPanel();
        right.Children.Add(Form.Heading("Belegung (Standard-Profil)"));
        right.Children.Add(_editor);
        right.Children.Add(Form.Hint("Hinweis: Wird die DPI-Taste umgeleitet, verliert sie ihre eingebaute DPI-Umschaltung. " +
                                     "Dafür gibt es die Aktion \"DPI setzen / umschalten\" (z.B. als Ring-Segment). " +
                                     "App-spezifische Abweichungen legst du unter \"Profile\" an – alles andere erben Profile von hier."));
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        Content = Form.Scroll(grid);

        _list.SelectionChanged += (_, _) =>
        {
            _selected = _list.SelectedIndex >= 0 && _list.SelectedIndex < _controls.Count ? _controls[_list.SelectedIndex].Cid : null;
            BuildEditor();
        };
        Refresh(0x00FD);
    }

    public void Refresh(ushort? select = null)
    {
        var keep = select ?? _selected;
        _controls = _ctx.KnownControls();
        _list.Items.Clear();
        foreach (var c in _controls)
        {
            var binding = _ctx.Config.Buttons.FirstOrDefault(kv => ControlIds.TryParse(kv.Key, out var id) && id == c.Cid).Value;
            var flags = c.Divertable ? "" : " (nicht umleitbar)";
            _list.Items.Add($"{ControlIds.Format(c.Cid)}  {c.Name}{flags} → {binding?.Describe() ?? "Originalfunktion"}");
        }
        var index = keep is { } k ? _controls.ToList().FindIndex(c => c.Cid == k) : -1;
        _list.SelectedIndex = index >= 0 ? index : _controls.Count > 0 ? 0 : -1;
    }

    private void BuildEditor()
    {
        _editor.Children.Clear();
        if (_selected is not { } cid)
        {
            _editor.Children.Add(Form.Hint("Keine Taste ausgewählt."));
            return;
        }
        var control = _controls.First(c => c.Cid == cid);
        _editor.Children.Add(Form.Row("Taste", new TextBlock { Text = $"{ControlIds.Format(cid)} – {control.Name}", FontWeight = FontWeights.SemiBold }));
        _editor.Children.Add(Form.Row("Gerät(e)", new TextBlock { Text = control.Devices }));
        _editor.Children.Add(Form.Row("Umleitbar", new TextBlock
        {
            Text = control.Divertable ? (control.RawXY ? "ja (mit Raw-XY)" : "ja") : "nein – bei Standardtasten greift der Maus-Hook-Fallback",
        }));

        var key = _ctx.Config.Buttons.Keys.FirstOrDefault(k => ControlIds.TryParse(k, out var id) && id == cid) ?? ControlIds.Format(cid);
        var editor = new ActionEditor(ActionContext.Button, _ctx.RingNames) { Action = _ctx.Config.Buttons.GetValueOrDefault(key) };
        editor.Changed += () =>
        {
            if (editor.Action is { } action) _ctx.Config.Buttons[key] = action;
            else _ctx.Config.Buttons.Remove(key);
            _ctx.MarkDirty();
            var index = _list.SelectedIndex;
            var c = _controls[index];
            _list.Items[index] = $"{ControlIds.Format(c.Cid)}  {c.Name}{(c.Divertable ? "" : " (nicht umleitbar)")} → {editor.Action?.Describe() ?? "Originalfunktion"}";
            _list.SelectedIndex = index;
        };
        _editor.Children.Add(editor);
    }
}

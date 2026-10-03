using System.Windows;
using System.Windows.Controls;
using RingMouse.Core.Config;
using RingMouse.Device;
using RingMouse.HidPlusPlus.Features;

namespace RingMouse.App.Settings;

/// <summary>Standard-Tastenbelegung (CID → Aktion). Die Tastenliste kommt per 0x1B04 von den verbundenen Geräten.</summary>
internal sealed class ButtonsPage : UserControl
{
    private readonly SettingsContext _ctx;
    private readonly ListBox _list = new() { MinHeight = 260 };
    private readonly StackPanel _editor = new();
    private readonly Button _captureButton;
    private readonly TextBlock _captureStatus = Form.Hint("");
    private IReadOnlyList<SettingsContext.KnownControl> _controls = [];
    private ushort? _selected;
    private CancellationTokenSource? _capture;

    public ButtonsPage(SettingsContext ctx)
    {
        _ctx = ctx;
        var grid = new Grid { Margin = new Thickness(12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        left.Children.Add(Form.Heading("Tasten"));
        _captureButton = Form.Button("Taste drücken …", () => _ = CaptureAsync(), accent: true);
        left.Children.Add(Form.Buttons(_captureButton));
        left.Children.Add(_captureStatus);
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
        left.Children.Add(Form.Hint("„Taste drücken …“ wählt die Taste aus, die du an der Maus drückst. Die Liste meldet die Maus " +
                                    "selbst (ringmouse-probe controls); für nicht verbundene Geräte die CID manuell eintragen."));
        grid.Children.Add(left);

        var right = new StackPanel();
        right.Children.Add(Form.Heading("Belegung (Standard-Profil)"));
        right.Children.Add(_editor);
        right.Children.Add(Form.Hint("Hinweis: Eine umgeleitete Taste verliert ihre eingebaute Funktion (z.B. die DPI-Umschaltung). " +
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
        Unloaded += (_, _) => _capture?.Cancel();
        Refresh();
    }

    public void Refresh(ushort? select = null)
    {
        _controls = _ctx.KnownControls();
        var keep = select ?? _selected ?? DefaultSelection();
        _list.Items.Clear();
        foreach (var c in _controls) _list.Items.Add(ItemText(c, BindingOf(c.Cid)));
        var index = keep is { } k ? _controls.ToList().FindIndex(c => c.Cid == k) : -1;
        _list.SelectedIndex = index >= 0 ? index : _controls.Count > 0 ? 0 : -1;
    }

    /// <summary>Ring-Taste, sonst die erste belegte Taste, sonst die erste umleitbare Sondertaste.</summary>
    private ushort? DefaultSelection()
    {
        var bound = _controls.Where(c => BindingOf(c.Cid) is not null).ToList();
        return bound.FirstOrDefault(c => BindingOf(c.Cid) is OpenRingAction)?.Cid
               ?? bound.FirstOrDefault()?.Cid
               ?? _controls.FirstOrDefault(c => c.Divertable && !c.Virtual && ControlIds.GetStandardMouseButton(c.Cid) == StandardMouseButton.None)?.Cid;
    }

    private ActionDefinition? BindingOf(ushort cid) =>
        _ctx.Config.Buttons.FirstOrDefault(kv => ControlIds.TryParse(kv.Key, out var id) && id == cid).Value;

    private static string ItemText(SettingsContext.KnownControl c, ActionDefinition? binding) =>
        $"{ControlIds.Format(c.Cid)}  {c.Name}{(c.Divertable ? "" : " (nicht umleitbar)")}{(c.Virtual ? " (virtuell)" : "")} → {binding?.Describe() ?? "Originalfunktion"}";

    /// <summary>Nächste gedrückte Maustaste erkennen und auswählen; ein zweiter Klick bricht ab.</summary>
    private async Task CaptureAsync()
    {
        if (_capture is not null)
        {
            _capture.Cancel();
            return;
        }
        if (!_ctx.Host.Devices.Any(d => d.State == DeviceState.Ready))
        {
            _captureStatus.Text = "Keine Maus verbunden – einmal bewegen, dann nochmal.";
            return;
        }

        var cts = _capture = new CancellationTokenSource();
        _captureButton.Content = "Abbrechen";
        _captureStatus.Text = "Einen Moment …";
        try
        {
            var pressed = await _ctx.Host.CaptureButtonAsync(
                () => Dispatcher.BeginInvoke(() =>
                {
                    if (!cts.IsCancellationRequested) _captureStatus.Text = "Jetzt die gewünschte Taste an der Maus drücken …";
                }),
                cts.Token);
            if (pressed is { } e)
            {
                Refresh(e.ControlId);
                _captureStatus.Text = $"Erkannt: {ControlIds.Format(e.ControlId)} – {ControlIds.GetName(e.ControlId)}. Rechts die Aktion wählen.";
            }
            else
            {
                _captureStatus.Text = cts.IsCancellationRequested ? "Abgebrochen." : "Keine Taste erkannt (Zeit abgelaufen).";
            }
        }
        catch (InvalidOperationException ex)
        {
            _captureStatus.Text = ex.Message; // Erkennung läuft schon (z.B. in der Ersteinrichtung)
        }
        finally
        {
            cts.Dispose();
            _capture = null;
            _captureButton.Content = "Taste drücken …";
        }
    }

    private void BuildEditor()
    {
        _editor.Children.Clear();
        if (_selected is not { } cid)
        {
            _editor.Children.Add(Form.Hint("Keine Taste ausgewählt. Mit „Taste drücken …“ eine Taste der Maus wählen."));
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
            _list.Items[index] = ItemText(_controls[index], editor.Action);
            _list.SelectedIndex = index;
        };
        _editor.Children.Add(editor);
    }
}

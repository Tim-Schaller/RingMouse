using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RingMouse.App.Ring;
using RingMouse.Core.Config;
using RingMouse.Core.Ring;
using static RingMouse.Core.Localization.Lang;

namespace RingMouse.App.Settings;

/// <summary>Ringe und Segmente bearbeiten, mit anklickbarer Live-Vorschau und Ring-Verhalten.</summary>
internal sealed class RingsPage : UserControl
{
    private readonly SettingsContext _ctx;
    private readonly ListBox _rings = new() { MinHeight = 160 };
    private readonly ListBox _segments = new() { MinHeight = 200 };
    private readonly Viewbox _preview = new() { Width = 300, Height = 300, Margin = new Thickness(0, 0, 0, 8), Cursor = Cursors.Hand };
    private readonly StackPanel _segmentEditor = new();
    private string? _ringName;
    private int _segmentIndex = -1;
    private bool _refreshing;

    public RingsPage(SettingsContext ctx)
    {
        _ctx = ctx;
        var grid = new Grid { Margin = new Thickness(12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Spalte 1: Ringe
        var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        left.Children.Add(Form.Heading(L("Rings", "Ringe")));
        left.Children.Add(_rings);
        left.Children.Add(Form.Buttons(Form.Button(L("New", "Neu"), NewRing), Form.Button(L("Rename", "Umbenennen"), RenameRing),
            Form.Button(L("Delete", "Löschen"), DeleteRing)));
        left.Children.Add(Form.Hint(L("\"main\" is opened by the ring button (see Buttons). Submenus are separate rings.",
            "\"main\" öffnet die Ring-Taste (siehe Tasten). Untermenüs sind eigene Ringe.")));
        grid.Children.Add(left);

        // Spalte 2: Vorschau + Segmentliste
        var middle = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        middle.Children.Add(Form.Heading(L("Segments", "Segmente")));
        middle.Children.Add(_preview);
        middle.Children.Add(_segments);
        middle.Children.Add(Form.Buttons(
            Form.Button("+", AddSegment), Form.Button(L("Remove", "Entfernen"), RemoveSegment), Form.Button("↑", () => MoveSegment(-1)),
            Form.Button("↓", () => MoveSegment(1)), Form.Button(L("Empty/assigned", "Leer/belegt"), ToggleEmpty)));
        middle.Children.Add(Form.Hint(L("Segment 1 is at the top, then clockwise. Click it in the preview to edit it.",
            "Segment 1 liegt oben, dann im Uhrzeigersinn. In der Vorschau anklicken zum Bearbeiten.")));
        Grid.SetColumn(middle, 1);
        grid.Children.Add(middle);

        // Spalte 3: Segment-Editor + Verhalten
        var right = new StackPanel();
        right.Children.Add(Form.Heading(L("Edit segment", "Segment bearbeiten")));
        right.Children.Add(_segmentEditor);
        right.Children.Add(BuildAppearance());
        right.Children.Add(BuildBehaviour());
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        Content = Form.Scroll(grid);

        _rings.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            _ringName = _rings.SelectedItem as string;
            _segmentIndex = CurrentRing?.Segments.Count > 0 ? 0 : -1;
            RefreshSegments();
        };
        _segments.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            _segmentIndex = _segments.SelectedIndex;
            BuildSegmentEditor();
            RefreshPreview();
        };
        _preview.MouseLeftButtonUp += OnPreviewClick;
        RefreshRings(DefaultConfig.MainRing);
    }

    private RingDefinition? CurrentRing => _ringName is not null && _ctx.Config.Rings.TryGetValue(_ringName, out var r) ? r : null;

    private void RefreshRings(string? select)
    {
        _refreshing = true;
        _rings.Items.Clear();
        foreach (var name in _ctx.RingNames()) _rings.Items.Add(name);
        _refreshing = false;
        _rings.SelectedItem = select is not null && _ctx.Config.Rings.ContainsKey(select) ? _ctx.RingNames().First(n => n.Equals(select, StringComparison.OrdinalIgnoreCase)) : _rings.Items.Count > 0 ? _rings.Items[0] : null;
        _ringName = _rings.SelectedItem as string;
        if (_segmentIndex < 0 && CurrentRing?.Segments.Count > 0) _segmentIndex = 0;
        RefreshSegments();
    }

    private void RefreshSegments()
    {
        _refreshing = true;
        _segments.Items.Clear();
        var ring = CurrentRing;
        if (ring is not null)
        {
            for (var i = 0; i < ring.Segments.Count; i++)
            {
                var s = ring.Segments[i];
                var where = Direction(i, ring.Segments.Count);
                _segments.Items.Add(s?.Action is null
                    ? L($"{i + 1} · {where}: (empty)", $"{i + 1} · {where}: (leer)")
                    : $"{i + 1} · {where}: {s.Label} – {s.Action.Describe()}");
            }
        }
        _segments.SelectedIndex = _segmentIndex;
        _refreshing = false;
        BuildSegmentEditor();
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        var ring = CurrentRing;
        if (ring is null || ring.Segments.Count == 0)
        {
            _preview.Child = null;
            return;
        }
        // Vorschau ohne Animation (Endzustand), mit Zeigerpunkt auf dem gewählten Segment
        var settings = ConfigSerializer.Clone(_ctx.Config.Ring);
        settings.Animation = false;
        var visual = RingVisual.Build(ring, RingTheme.Create(settings), settings,
            !_ringName!.Equals(DefaultConfig.MainRing, StringComparison.OrdinalIgnoreCase));
        if (_segmentIndex >= 0 && _segmentIndex < ring.Segments.Count)
        {
            visual.SetHighlight(_segmentIndex);
            var (px, py) = RingGeometry.PointAt(RingGeometry.SegmentCenterAngle(_segmentIndex, ring.Segments.Count), settings.Radius * 0.62);
            visual.UpdatePointer(px, py, visible: true);
        }
        _preview.Child = visual.Root;
    }

    private void OnPreviewClick(object sender, MouseButtonEventArgs e)
    {
        var ring = CurrentRing;
        if (ring is null || _preview.Child is not FrameworkElement root) return;
        var p = e.GetPosition(root);
        var c = root.Width / 2;
        var index = RingGeometry.SegmentAt(p.X - c, p.Y - c, ring.Segments.Count, _ctx.Config.Ring.Deadzone);
        if (index < 0) return;
        _segmentIndex = index;
        _segments.SelectedIndex = index;
    }

    private void BuildSegmentEditor()
    {
        _segmentEditor.Children.Clear();
        var ring = CurrentRing;
        if (ring is null || _segmentIndex < 0 || _segmentIndex >= ring.Segments.Count)
        {
            _segmentEditor.Children.Add(Form.Hint(L("Select a segment on the left.", "Links ein Segment auswählen.")));
            return;
        }

        var segment = ring.Segments[_segmentIndex];
        if (segment is null)
        {
            _segmentEditor.Children.Add(Form.Hint(L("Empty slot – use \"Empty/assigned\" to add an action.", "Leerer Platz – mit \"Leer/belegt\" eine Aktion anlegen.")));
            return;
        }

        var index = _segmentIndex;
        _segmentEditor.Children.Add(Form.Row(L("Label", "Beschriftung"), Form.Text(segment.Label, v =>
        {
            segment.Label = v;
            Changed(index);
        })));

        var iconPreview = new TextBlock { FontFamily = IconCatalog.IconFont, FontSize = 22, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var icon = new ComboBox { IsEditable = true, MinWidth = 200 };
        foreach (var name in IconCatalog.Glyphs.Keys.OrderBy(n => n)) icon.Items.Add(name);
        icon.Text = segment.Icon ?? "";
        var building = true;
        void UpdateIcon()
        {
            var value = string.IsNullOrWhiteSpace(icon.Text) ? null : icon.Text.Trim();
            var changed = value != segment.Icon;
            segment.Icon = value;
            var spec = IconCatalog.Resolve(segment.Icon, segment.Action);
            iconPreview.Text = spec.Glyph?.ToString() ?? spec.Text ?? "";
            if (changed && !building) Changed(index);
        }
        icon.SelectionChanged += (_, _) =>
        {
            if (icon.SelectedItem is string s) icon.Text = s;
            UpdateIcon();
        };
        icon.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdateIcon()));
        var iconRow = new StackPanel { Orientation = Orientation.Horizontal };
        iconRow.Children.Add(icon);
        iconRow.Children.Add(iconPreview);
        _segmentEditor.Children.Add(Form.Row("Icon", iconRow,
            L("Symbol name, glyph:E72E, file:C:\\path\\image.png, exe:C:\\path\\app.exe or text:AB. Empty = matching the action.",
                "Symbolname, glyph:E72E, file:C:\\pfad\\bild.png, exe:C:\\pfad\\app.exe oder text:AB. Leer = passend zur Aktion.")));
        UpdateIcon();

        var editor = new ActionEditor(ActionContext.Segment, _ctx.RingNames) { Action = segment.Action };
        editor.Changed += () =>
        {
            segment.Action = editor.Action;
            Changed(index);
        };
        _segmentEditor.Children.Add(editor);
    }

    private void Changed(int index)
    {
        _ctx.MarkDirty();
        var ring = CurrentRing;
        if (ring is null || index < 0 || index >= _segments.Items.Count) return;
        var s = ring.Segments[index];
        _refreshing = true;
        _segments.Items[index] = s?.Action is null
            ? L($"{index + 1} · {Direction(index, ring.Segments.Count)}: (empty)", $"{index + 1} · {Direction(index, ring.Segments.Count)}: (leer)")
            : $"{index + 1} · {Direction(index, ring.Segments.Count)}: {s.Label} – {s.Action.Describe()}";
        _segments.SelectedIndex = index;
        _refreshing = false;
        RefreshPreview();
    }

    private FrameworkElement BuildAppearance()
    {
        var r = _ctx.Config.Ring;
        void Changed()
        {
            _ctx.MarkDirty();
            RefreshPreview();
        }

        var size = Form.Slider(r.Radius, 100, 260, v => { r.Radius = v; Changed(); }, 5);
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal };
        sizeRow.Children.Add(size);
        sizeRow.Children.Add(Form.Button(L("Show on screen", "Am Bildschirm ansehen"), () =>
        {
            if (CurrentRing is { } ring)
                _ctx.Host.PreviewRing(ring, ConfigSerializer.Clone(r), !_ringName!.Equals(DefaultConfig.MainRing, StringComparison.OrdinalIgnoreCase));
        }));
        size.Margin = new Thickness(0, 0, 10, 0);

        var pointerRow = new StackPanel();
        pointerRow.Children.Add(Form.Slider(r.PointerSize, 4, 30, v => { r.PointerSize = v; Changed(); }));
        pointerRow.Children.Add(new ColorPicker(r.PointerColor, ColorPicker.PointerPresets, L("Automatic", "Automatisch"), v => { r.PointerColor = v; Changed(); }));

        return new Expander
        {
            Header = L("Ring appearance", "Aussehen des Rings"),
            IsExpanded = true,
            Margin = new Thickness(0, 16, 0, 0),
            Content = new StackPanel
            {
                Margin = new Thickness(4, 8, 0, 0),
                Children =
                {
                    Form.Row(L("Size (radius, DIP)", "Größe (Radius, DIP)"), sizeRow,
                        L("The preview here is always scaled to fit – \"Show on screen\" briefly shows the ring at its real size at the mouse pointer.",
                            "Die Vorschau hier wird immer eingepasst – \"Am Bildschirm ansehen\" zeigt den Ring kurz in echter Größe am Mauszeiger.")),
                    Form.Row(L("Icons & text (%)", "Symbole & Schrift (%)"), Form.Slider(r.TextScale, 50, 200, v => { r.TextScale = (int)v; Changed(); }, 5)),
                    Form.Row(L("Color scheme", "Farbschema"), Form.Choice(
                        [(ThemePreference.System, L("Like Windows", "Wie Windows")), (ThemePreference.Light, L("Light", "Hell")), (ThemePreference.Dark, L("Dark", "Dunkel"))],
                        r.Theme, v => { r.Theme = v; Changed(); })),
                    Form.Row(L("Ring color", "Ringfarbe"),
                        new ColorPicker(r.RingColor, ColorPicker.RingPresets, L("Like color scheme", "Wie Farbschema"), v => { r.RingColor = v; Changed(); }),
                        L("Text and shades are derived from the color.", "Schrift und Abstufungen werden aus der Farbe abgeleitet.")),
                    Form.Row(L("Highlight", "Markierung"),
                        new ColorPicker(r.AccentColor, ColorPicker.AccentPresets, L("Windows accent", "Windows-Akzent"), v => { r.AccentColor = v; Changed(); })),
                    Form.Row(L("Opacity (%)", "Deckkraft (%)"), Form.Slider(r.Opacity, 30, 100, v => { r.Opacity = (int)v; Changed(); }, 5)),
                    Form.Row(L("Pointer dot", "Zeigerpunkt"), pointerRow,
                        L("Size (diameter in DIP) and color of the dot that shows the direction while holding.",
                            "Größe (Durchmesser in DIP) und Farbe des Punkts, der beim Halten die Richtung zeigt.")),
                    Form.Row("", Form.Check(L("Show labels", "Beschriftungen anzeigen"), r.ShowLabels, v => { r.ShowLabels = v; Changed(); })),
                    Form.Row("", Form.Check(L("Animations", "Animationen"), r.Animation, v => { r.Animation = v; _ctx.MarkDirty(); })),
                },
            },
        };
    }

    private FrameworkElement BuildBehaviour()
    {
        var r = _ctx.Config.Ring;
        void Dirty() => _ctx.MarkDirty();
        var expander = new Expander
        {
            Header = L("Ring behavior", "Verhalten des Rings"),
            IsExpanded = false,
            Margin = new Thickness(0, 12, 0, 0),
            Content = new StackPanel
            {
                Margin = new Thickness(4, 8, 0, 0),
                Children =
                {
                    Form.Row(L("Mode", "Bedienung"), Form.Choice(
                        [(RingMode.Hybrid, L("Hybrid – tap briefly = keep open, hold = releasing selects", "Hybrid – kurz tippen = offen lassen, halten = loslassen wählt")),
                         (RingMode.Hold, L("Hold – releasing runs the action", "Halten – loslassen führt aus")),
                         (RingMode.Tap, L("Tap – ring stays open, a click runs the action", "Tippen – Ring bleibt offen, Klick führt aus"))],
                        r.Mode, v => { r.Mode = v; Dirty(); })),
                    Form.Row("Deadzone (DIP)", Form.Slider(r.Deadzone, 10, 60, v => { r.Deadzone = v; Dirty(); RefreshPreview(); }),
                        L("Releasing/clicking in the center cancels (in a submenu: back).", "Loslassen/Klicken in der Mitte bricht ab (im Untermenü: zurück).")),
                    Form.Row(L("Tap threshold (ms)", "Tipp-Schwelle (ms)"), Form.Number(r.TapThresholdMs, v => { r.TapThresholdMs = (int)v; Dirty(); }, 50, 2000)),
                    Form.Row("Raw-XY", Form.Choice(
                        [(RawXYUsage.Auto, L("Automatic (pointer stays still while holding if the button supports it)",
                             "Automatisch (Zeiger bleibt beim Halten stehen, wenn die Taste es kann)")),
                         (RawXYUsage.On, L("Always request", "Immer anfordern")), (RawXYUsage.Off, L("Off (use pointer position)", "Aus (Zeigerposition verwenden)"))],
                        r.UseRawXY, v => { r.UseRawXY = v; Dirty(); })),
                    Form.Row(L("Raw-XY sensitivity", "Raw-XY-Empfindlichkeit"), Form.Number(r.RawXYScale, v => { r.RawXYScale = v; Dirty(); }, 0.05, 10, "0.00")),
                    Form.Row(L("Close automatically (s)", "Automatisch schließen (s)"),
                        Form.Number(r.AutoCloseSeconds, v => { r.AutoCloseSeconds = (int)v; Dirty(); }, 0, 600),
                        L("Tap mode only; 0 = never.", "Nur im Tippen-Modus; 0 = nie.")),
                    Form.Row("", Form.Check(L("Restore the mouse pointer afterwards", "Mauszeiger danach zurücksetzen"), r.RestoreCursor,
                        v => { r.RestoreCursor = v; Dirty(); })),
                    Form.Row("", Form.Check(L("Open submenus while holding by pushing further (push-through)", "Untermenü beim Halten durch Weiterschieben öffnen"),
                            r.SubmenuPush, v => { r.SubmenuPush = v; Dirty(); }),
                        L("Hold the button, push further out towards the submenu, then release over the entry.",
                            "Taste halten, Richtung Untermenü weiter nach außen schieben, dann über dem Eintrag loslassen.")),
                    Form.Row("", Form.Check(L("Hide the mouse pointer while holding (the dot shows the direction)",
                            "Mauszeiger beim Halten ausblenden (Punkt zeigt die Richtung)"), r.HideCursor,
                        v => { r.HideCursor = v; Dirty(); })),
                },
            },
        };
        return expander;
    }

    // ------------------------------------------------------------------ Aktionen

    private void NewRing()
    {
        var name = InputDialog.Ask(Window.GetWindow(this), L("New ring", "Neuer Ring"), L("Ring name (e.g. \"office\"):", "Name des Rings (z.B. \"office\"):"));
        if (string.IsNullOrWhiteSpace(name) || _ctx.Config.Rings.ContainsKey(name)) return;
        _ctx.Config.Rings[name] = new RingDefinition
        {
            Title = name,
            Segments = [new RingSegment { Label = L("Copy", "Kopieren"), Icon = "Copy", Action = new KeysAction { Keys = "Ctrl+C" } },
                        new RingSegment { Label = L("Paste", "Einfügen"), Icon = "Paste", Action = new KeysAction { Keys = "Ctrl+V" } },
                        null, null],
        };
        _ctx.MarkDirty();
        RefreshRings(name);
    }

    private void RenameRing()
    {
        if (_ringName is null) return;
        var name = InputDialog.Ask(Window.GetWindow(this), L("Rename ring", "Ring umbenennen"), L("New name:", "Neuer Name:"), _ringName);
        if (string.IsNullOrWhiteSpace(name) || name == _ringName || _ctx.Config.Rings.ContainsKey(name)) return;
        _ctx.RenameRing(_ringName, name);
        _ctx.MarkDirty();
        RefreshRings(name);
    }

    private void DeleteRing()
    {
        if (_ringName is null) return;
        if (MessageBox.Show(Window.GetWindow(this)!,
                L($"Delete ring \"{_ringName}\"? References to it become invalid.", $"Ring \"{_ringName}\" löschen? Verweise darauf werden ungültig."),
                L("Delete ring", "Ring löschen"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _ctx.Config.Rings.Remove(_ringName);
        _ctx.MarkDirty();
        RefreshRings(DefaultConfig.MainRing);
    }

    private void AddSegment()
    {
        var ring = CurrentRing;
        if (ring is null || ring.Segments.Count >= 8) return;
        ring.Segments.Add(new RingSegment { Label = L("New", "Neu"), Action = new KeysAction { Keys = "Ctrl+C" } });
        _segmentIndex = ring.Segments.Count - 1;
        _ctx.MarkDirty();
        RefreshSegments();
    }

    private void RemoveSegment()
    {
        var ring = CurrentRing;
        if (ring is null || _segmentIndex < 0 || ring.Segments.Count <= 2) return;
        ring.Segments.RemoveAt(_segmentIndex);
        _segmentIndex = Math.Min(_segmentIndex, ring.Segments.Count - 1);
        _ctx.MarkDirty();
        RefreshSegments();
    }

    private void MoveSegment(int delta)
    {
        var ring = CurrentRing;
        if (ring is null || _segmentIndex < 0) return;
        var target = _segmentIndex + delta;
        if (target < 0 || target >= ring.Segments.Count) return;
        (ring.Segments[_segmentIndex], ring.Segments[target]) = (ring.Segments[target], ring.Segments[_segmentIndex]);
        _segmentIndex = target;
        _ctx.MarkDirty();
        RefreshSegments();
    }

    private void ToggleEmpty()
    {
        var ring = CurrentRing;
        if (ring is null || _segmentIndex < 0) return;
        ring.Segments[_segmentIndex] = ring.Segments[_segmentIndex] is null
            ? new RingSegment { Label = L("New", "Neu"), Action = new KeysAction { Keys = "Ctrl+C" } }
            : null;
        _ctx.MarkDirty();
        RefreshSegments();
    }

    private static string Direction(int index, int count)
    {
        string[] names =
        [
            L("top", "oben"), L("top right", "oben rechts"), L("right", "rechts"), L("bottom right", "unten rechts"),
            L("bottom", "unten"), L("bottom left", "unten links"), L("left", "links"), L("top left", "oben links"),
        ];
        var angle = RingGeometry.SegmentCenterAngle(index, count);
        return names[(int)Math.Round(angle / 45.0) % 8];
    }
}

internal static class FrameworkElementExtensions
{
    public static T Also<T>(this T element, Action<T> configure)
    {
        configure(element);
        return element;
    }
}

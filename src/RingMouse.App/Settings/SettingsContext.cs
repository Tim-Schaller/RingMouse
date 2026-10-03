using System.Windows;
using System.Windows.Controls;
using RingMouse.Core.Config;
using RingMouse.HidPlusPlus.Features;

namespace RingMouse.App.Settings;

/// <summary>Gemeinsamer Zustand der Einstellungsseiten: Arbeitskopie der Config + Zugriff auf die App.</summary>
internal sealed class SettingsContext(ISettingsHost host, RingMouseConfig config)
{
    public ISettingsHost Host { get; } = host;
    public RingMouseConfig Config { get; set; } = config;

    public event Action? Dirty;

    public void MarkDirty() => Dirty?.Invoke();

    public IEnumerable<string> RingNames() =>
        Config.Rings.Keys.OrderBy(n => n.Equals(DefaultConfig.MainRing, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase);

    public sealed record KnownControl(ushort Cid, string Name, bool Divertable, bool RawXY, string Devices, bool Virtual = false);

    /// <summary>Alle bekannten Tasten: von verbundenen Geräten (per 0x1B04 ermittelt) plus bereits konfigurierte.</summary>
    public IReadOnlyList<KnownControl> KnownControls()
    {
        var map = new Dictionary<ushort, KnownControl>();
        foreach (var device in Host.Devices)
        {
            foreach (var c in device.Controls)
            {
                var name = Tray.TrayController.ShortName(device.Name);
                map[c.ControlId] = map.TryGetValue(c.ControlId, out var existing)
                    ? existing with { Devices = existing.Devices.Contains(name) ? existing.Devices : $"{existing.Devices}, {name}" }
                    : new KnownControl(c.ControlId, c.Name, c.IsDivertable, c.SupportsRawXY, name, c.IsVirtual);
            }
        }
        foreach (var key in Config.Buttons.Keys.Concat(Config.Profiles.SelectMany(p => p.Buttons.Keys)))
        {
            if (ControlIds.TryParse(key, out var cid) && !map.ContainsKey(cid))
                map[cid] = new KnownControl(cid, ControlIds.GetName(cid), true, false, "nicht verbunden");
        }
        return map.Values.OrderBy(c => c.Cid).ToList();
    }

    /// <summary>Ringnamen in allen Verweisen umbenennen.</summary>
    public void RenameRing(string oldName, string newName)
    {
        if (!Config.Rings.Remove(oldName, out var ring)) return;
        Config.Rings[newName] = ring;
        foreach (var action in AllActions())
        {
            switch (action)
            {
                case OpenRingAction o when o.Ring.Equals(oldName, StringComparison.OrdinalIgnoreCase):
                    o.Ring = newName;
                    break;
                case SubmenuAction s when s.Ring.Equals(oldName, StringComparison.OrdinalIgnoreCase):
                    s.Ring = newName;
                    break;
            }
        }
        foreach (var p in Config.Profiles)
        {
            foreach (var key in p.Rings.Keys.ToList())
            {
                var target = p.Rings[key];
                if (target.Equals(oldName, StringComparison.OrdinalIgnoreCase)) p.Rings[key] = newName;
                if (key.Equals(oldName, StringComparison.OrdinalIgnoreCase))
                {
                    p.Rings.Remove(key);
                    p.Rings[newName] = p.Rings.GetValueOrDefault(newName) ?? target;
                }
            }
        }
    }

    private IEnumerable<ActionDefinition> AllActions()
    {
        foreach (var a in Config.Buttons.Values) yield return a;
        foreach (var p in Config.Profiles)
            foreach (var a in p.Buttons.Values)
                yield return a;
        foreach (var r in Config.Rings.Values)
            foreach (var s in r.Segments)
                if (s?.Action is { } a)
                    yield return a;
    }
}

/// <summary>Einfacher Eingabedialog.</summary>
internal static class InputDialog
{
    public static string? Ask(Window? owner, string title, string prompt, string initial = "")
    {
        var box = new TextBox { Text = initial, MinWidth = 280, Margin = new Thickness(0, 8, 0, 12) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Abbrechen", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var stack = new StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(box);
        stack.Children.Add(buttons);
        var window = new Window
        {
            Title = title,
            Content = stack,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return window.ShowDialog() == true ? box.Text.Trim() : null;
    }
}

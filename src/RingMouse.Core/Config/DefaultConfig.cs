using static RingMouse.Core.Localization.Lang;

namespace RingMouse.Core.Config;

/// <summary>
/// Eingebaute Standard-Config für neue Installationen: ein allgemeiner Ring ohne persönliche Inhalte.
/// Keine Taste ist vorbelegt – welche den Ring öffnet, legt die Ersteinrichtung fest (Taste drücken), denn
/// jede Maus hat andere Tasten. Slot-Reihenfolge: 0 = oben, dann im Uhrzeigersinn.
/// </summary>
public static class DefaultConfig
{
    public const string MainRing = "main";

    public static RingMouseConfig Create()
    {
        var c = new RingMouseConfig();

        c.Rings[MainRing] = new RingDefinition
        {
            Segments =
            [
                Seg(L("Play/Pause", "Wiedergabe/Pause"), "PlayPause", new MediaAction { Key = MediaKey.PlayPause }),
                Seg(L("Emoji", "Emoji"), "Emoji", new SystemAction { Command = SystemCommand.EmojiPanel }),
                Seg(L("Media", "Medien"), "Music", new SubmenuAction { Ring = "media" }),
                Seg(L("Lock", "Sperren"), "Lock", new SystemAction { Command = SystemCommand.Lock }),
                Seg(L("Text", "Text"), "Edit", new SubmenuAction { Ring = "text" }),
                Seg(L("Screenshot", "Bildschirmfoto"), "Screenshot", new ScreenshotAction()),
                Seg(L("Desktop", "Desktop"), "Desktop", new SystemAction { Command = SystemCommand.ShowDesktop }),
                Seg(L("Explorer", "Explorer"), "Folder", new LaunchAction { Target = "explorer.exe" }),
            ],
        };

        // Vier Plätze: oben/rechts/unten/links – Lautstärke vertikal, Titelwechsel horizontal
        c.Rings["media"] = new RingDefinition
        {
            Title = L("Media", "Medien"),
            Segments =
            [
                Seg(L("Volume up", "Lauter"), "VolumeUp", new MediaAction { Key = MediaKey.VolumeUp }),
                Seg(L("Next track", "Nächster Titel"), "Next", new MediaAction { Key = MediaKey.Next }),
                Seg(L("Volume down", "Leiser"), "VolumeDown", new MediaAction { Key = MediaKey.VolumeDown }),
                Seg(L("Previous track", "Vorheriger Titel"), "Previous", new MediaAction { Key = MediaKey.Previous }),
            ],
        };

        c.Rings["text"] = new RingDefinition
        {
            Title = L("Text", "Text"),
            Segments =
            [
                Seg(L("Date", "Datum"), "Calendar", new SnippetAction { Text = "{date}" }),
                Seg(L("Time", "Uhrzeit"), "Clock", new SnippetAction { Text = "{time}" }),
                Seg(L("Clipboard", "Zwischenablage"), "Clipboard", new SystemAction { Command = SystemCommand.ClipboardHistory }),
                Seg(L("Timestamp", "Zeitstempel"), "Recent", new SnippetAction { Text = "{now:yyyy-MM-dd HH:mm}" }),
            ],
        };

        c.Devices["*"] = new DeviceSettings();

        return c;
    }

    private static RingSegment Seg(string label, string icon, ActionDefinition action) =>
        new() { Label = label, Icon = icon, Action = action };
}

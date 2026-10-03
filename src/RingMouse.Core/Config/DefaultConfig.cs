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
                Seg("Wiedergabe/Pause", "PlayPause", new MediaAction { Key = MediaKey.PlayPause }),
                Seg("Emoji", "Emoji", new SystemAction { Command = SystemCommand.EmojiPanel }),
                Seg("Medien", "Music", new SubmenuAction { Ring = "media" }),
                Seg("Sperren", "Lock", new SystemAction { Command = SystemCommand.Lock }),
                Seg("Text", "Edit", new SubmenuAction { Ring = "text" }),
                Seg("Bildschirmfoto", "Screenshot", new ScreenshotAction()),
                Seg("Desktop", "Desktop", new SystemAction { Command = SystemCommand.ShowDesktop }),
                Seg("Explorer", "Folder", new LaunchAction { Target = "explorer.exe" }),
            ],
        };

        // Vier Plätze: oben/rechts/unten/links – Lautstärke vertikal, Titelwechsel horizontal
        c.Rings["media"] = new RingDefinition
        {
            Title = "Medien",
            Segments =
            [
                Seg("Lauter", "VolumeUp", new MediaAction { Key = MediaKey.VolumeUp }),
                Seg("Nächster Titel", "Next", new MediaAction { Key = MediaKey.Next }),
                Seg("Leiser", "VolumeDown", new MediaAction { Key = MediaKey.VolumeDown }),
                Seg("Vorheriger Titel", "Previous", new MediaAction { Key = MediaKey.Previous }),
            ],
        };

        c.Rings["text"] = new RingDefinition
        {
            Title = "Text",
            Segments =
            [
                Seg("Datum", "Calendar", new SnippetAction { Text = "{date}" }),
                Seg("Uhrzeit", "Clock", new SnippetAction { Text = "{time}" }),
                Seg("Zwischenablage", "Clipboard", new SystemAction { Command = SystemCommand.ClipboardHistory }),
                Seg("Zeitstempel", "Recent", new SnippetAction { Text = "{now:yyyy-MM-dd HH:mm}" }),
            ],
        };

        c.Devices["*"] = new DeviceSettings();

        return c;
    }

    private static RingSegment Seg(string label, string icon, ActionDefinition action) =>
        new() { Label = label, Icon = icon, Action = action };
}

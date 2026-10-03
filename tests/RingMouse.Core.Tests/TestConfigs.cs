using RingMouse.Core.Config;

namespace RingMouse.Core.Tests;

/// <summary>
/// Feste Test-Config, unabhängig vom mitgelieferten Standard (der sich ändern und übersetzt werden darf).
/// Hauptring: 0 oben Play/Pause, 1 Emoji, 2 rechts Apps▸, 3 Sperren, 4 unten Text▸, 5 Screenshot, 6 leer, 7 Explorer.
/// </summary>
internal static class TestConfigs
{
    public const ushort RingButton = 0x00FD;

    public static RingMouseConfig Sample()
    {
        var c = new RingMouseConfig();
        c.Buttons["0x00FD"] = new OpenRingAction { Ring = "main" };

        c.Rings["main"] = new RingDefinition
        {
            Segments =
            [
                Seg("Play/Pause", new MediaAction { Key = MediaKey.PlayPause }),
                Seg("Emoji", new SystemAction { Command = SystemCommand.EmojiPanel }),
                Seg("Apps", new SubmenuAction { Ring = "apps" }),
                Seg("Sperren", new SystemAction { Command = SystemCommand.Lock }),
                Seg("Text", new SubmenuAction { Ring = "text" }),
                Seg("Screenshot", new ScreenshotAction()),
                null,
                Seg("Explorer", new LaunchAction { Target = "explorer.exe" }),
            ],
        };

        c.Rings["apps"] = new RingDefinition
        {
            Title = "Apps",
            Segments =
            [
                Seg("Speichern", new AppKeysAction { Process = "notepad.exe", Keys = "Ctrl+S" }),
                Seg("Suchen", new AppKeysAction { Process = "notepad.exe", Keys = "Ctrl+F" }),
                Seg("Rechner", new LaunchAction { Target = "calc.exe" }),
                Seg("Editor", new LaunchAction { Target = "notepad.exe" }),
            ],
        };

        c.Rings["text"] = new RingDefinition
        {
            Title = "Text",
            Segments =
            [
                Seg("Erledigt", new SnippetAction { Text = "Erledigt {now:dd.MM.yyyy HH:mm}" }),
                Seg("Grüße", new SnippetAction { Text = "Viele Grüße" }),
                Seg("Datum", new SnippetAction { Text = "{date}" }),
                Seg("Uhrzeit", new SnippetAction { Text = "{time}" }),
            ],
        };

        c.Devices["*"] = new DeviceSettings();
        c.Devices["Sample Mouse"] = new DeviceSettings { Dpi = 1000 };
        return c;
    }

    private static RingSegment Seg(string label, ActionDefinition action) => new() { Label = label, Action = action };
}

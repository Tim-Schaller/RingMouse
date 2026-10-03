using System.ComponentModel;
using System.Text.Json.Serialization;

namespace RingMouse.Core.Config;

/// <summary>Basis aller Aktionen; das JSON-Feld "type" wählt den Typ.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(OpenRingAction), "ring")]
[JsonDerivedType(typeof(KeysAction), "keys")]
[JsonDerivedType(typeof(MediaAction), "media")]
[JsonDerivedType(typeof(LaunchAction), "launch")]
[JsonDerivedType(typeof(SnippetAction), "snippet")]
[JsonDerivedType(typeof(PowerShellAction), "powershell")]
[JsonDerivedType(typeof(ScreenshotAction), "screenshot")]
[JsonDerivedType(typeof(SubmenuAction), "submenu")]
[JsonDerivedType(typeof(SystemAction), "system")]
[JsonDerivedType(typeof(DpiAction), "dpi")]
[JsonDerivedType(typeof(AppKeysAction), "appKeys")]
[JsonDerivedType(typeof(MouseAction), "mouse")]
[JsonDerivedType(typeof(NativeAction), "native")]
[JsonDerivedType(typeof(NoneAction), "none")]
[JsonDerivedType(typeof(SequenceAction), "sequence")]
[JsonDerivedType(typeof(DelayAction), "delay")]
public abstract class ActionDefinition
{
    private static readonly Dictionary<Type, string> s_typeNames = typeof(ActionDefinition)
        .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
        .Cast<JsonDerivedTypeAttribute>()
        .ToDictionary(a => a.DerivedType, a => (string)a.TypeDiscriminator!);

    /// <summary>JSON-Typname ("keys", "ring" …). Nicht virtuell, damit [JsonIgnore] greift.</summary>
    [JsonIgnore]
    public string TypeName => s_typeNames.TryGetValue(GetType(), out var name) ? name : GetType().Name;

    /// <summary>Kurzbeschreibung für UI und Logs.</summary>
    public abstract string Describe();

    public ActionDefinition Clone() => ConfigSerializer.Clone(this);
}

[Description("Öffnet einen Ring (nur als Tastenbelegung).")]
public sealed class OpenRingAction : ActionDefinition
{
    public string Ring { get; set; } = "main";
    public override string Describe() => $"Ring \"{Ring}\" öffnen";
}

[Description("Tastenkombination senden, z.B. \"Ctrl+Shift+S\" oder Folge \"Ctrl+K, Ctrl+C\".")]
public sealed class KeysAction : ActionDefinition
{
    public string Keys { get; set; } = "";
    public override string Describe() => $"Tasten {Keys}";
}

public enum MediaKey
{
    PlayPause,
    Next,
    Previous,
    Stop,
    VolumeUp,
    VolumeDown,
    Mute,
}

[Description("Medientaste senden.")]
public sealed class MediaAction : ActionDefinition
{
    public MediaKey Key { get; set; } = MediaKey.PlayPause;
    public override string Describe() => Key switch
    {
        MediaKey.PlayPause => "Wiedergabe/Pause",
        MediaKey.Next => "Nächster Titel",
        MediaKey.Previous => "Vorheriger Titel",
        MediaKey.Stop => "Stopp",
        MediaKey.VolumeUp => "Lauter",
        MediaKey.VolumeDown => "Leiser",
        MediaKey.Mute => "Stumm",
        _ => Key.ToString(),
    };
}

[Description("Programm, Datei, Ordner, URL oder URI starten (auch shell:AppsFolder\\...).")]
public sealed class LaunchAction : ActionDefinition
{
    public string Target { get; set; } = "";
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }

    [Description("Mit Adminrechten (UAC-Abfrage). Sonst immer ohne Adminrechte, auch wenn RingMouse elevated läuft.")]
    public bool Elevated { get; set; }

    public override string Describe() => $"Starten: {Target}{(string.IsNullOrWhiteSpace(Arguments) ? "" : " " + Arguments)}";
}

public enum SnippetMode
{
    [Description("Zeichen per Unicode-Eingabe tippen (layoutunabhängig)")] Type,
    [Description("Über die Zwischenablage einfügen (Strg+V) und Zwischenablage wiederherstellen")] Paste,
}

[Description("Text einfügen. Platzhalter: {now:dd.MM.yyyy HH:mm}, {date}, {time}, {clipboard}, {user}, {computer}.")]
public sealed class SnippetAction : ActionDefinition
{
    public string Text { get; set; } = "";
    public SnippetMode Mode { get; set; } = SnippetMode.Type;
    public override string Describe() => $"Text: {(Text.Length > 40 ? Text[..40] + "…" : Text)}";
}

[Description("PowerShell-Skript (script) oder Befehl (command) ausführen.")]
public sealed class PowerShellAction : ActionDefinition
{
    public string? Script { get; set; }
    public string? Command { get; set; }
    public string? Arguments { get; set; }
    public bool Hidden { get; set; } = true;

    [Description("pwsh.exe (PowerShell 7) statt Windows PowerShell 5.1 verwenden.")]
    public bool UsePwsh { get; set; }

    public bool Elevated { get; set; }
    public override string Describe() => Script is { Length: > 0 } ? $"PowerShell: {Script}" : $"PowerShell: {Command}";
}

[Description("Bildschirmausschnitt aufnehmen (Snipping Tool, wie Win+Shift+S).")]
public sealed class ScreenshotAction : ActionDefinition
{
    public override string Describe() => "Bildschirmfoto";
}

[Description("Untermenü: öffnet einen weiteren Ring an derselben Stelle.")]
public sealed class SubmenuAction : ActionDefinition
{
    public string Ring { get; set; } = "";
    public override string Describe() => $"Untermenü \"{Ring}\"";
}

public enum SystemCommand
{
    [Description("Arbeitsstation sperren")] Lock,
    [Description("Emoji-Panel (Win+.)")] EmojiPanel,
    [Description("Desktop anzeigen (Win+D)")] ShowDesktop,
    [Description("Aktive Anwendungen (Win+Tab)")] TaskView,
    [Description("Zwischenablage-Verlauf (Win+V)")] ClipboardHistory,
    [Description("Bildschirm aus")] MonitorOff,
    [Description("RingMouse-Einstellungen öffnen")] OpenSettings,
}

[Description("Systemfunktion.")]
public sealed class SystemAction : ActionDefinition
{
    public SystemCommand Command { get; set; } = SystemCommand.Lock;
    public override string Describe() => Command switch
    {
        SystemCommand.Lock => "Sperren",
        SystemCommand.EmojiPanel => "Emoji-Panel",
        SystemCommand.ShowDesktop => "Desktop anzeigen",
        SystemCommand.TaskView => "Aktive Anwendungen",
        SystemCommand.ClipboardHistory => "Zwischenablage-Verlauf",
        SystemCommand.MonitorOff => "Bildschirm aus",
        SystemCommand.OpenSettings => "RingMouse-Einstellungen",
        _ => Command.ToString(),
    };
}

[Description("Sensor-DPI setzen (ein Wert) bzw. zwischen mehreren Werten umschalten.")]
public sealed class DpiAction : ActionDefinition
{
    public List<int> Values { get; set; } = [1000, 2000];
    public override string Describe() => Values.Count == 1 ? $"DPI {Values[0]}" : $"DPI umschalten ({string.Join("/", Values)})";
}

[Description("Hotkey an eine bestimmte App senden (Fenster kurz aktivieren, danach Fokus zurück), z.B. Spotify.")]
public sealed class AppKeysAction : ActionDefinition
{
    [Description("Prozessname, z.B. \"Spotify.exe\".")]
    public string Process { get; set; } = "";

    public string Keys { get; set; } = "";

    [Description("Danach wieder das vorherige Fenster aktivieren.")]
    public bool RestoreFocus { get; set; } = true;

    [Description("Optional: Programm/URI zum Starten, falls der Prozess nicht läuft.")]
    public string? LaunchIfNotRunning { get; set; }

    public override string Describe() => $"{Keys} an {Process}";
}

public enum MouseButtonKind
{
    Left,
    Right,
    Middle,
    Back,
    Forward,
}

[Description("Maustaste auslösen (als Tastenbelegung: gedrückt halten wie die physische Taste).")]
public sealed class MouseAction : ActionDefinition
{
    public MouseButtonKind Button { get; set; } = MouseButtonKind.Middle;
    public override string Describe() => $"Maustaste {Button}";
}

[Description("Originalfunktion der Taste (Durchreichen).")]
public sealed class NativeAction : ActionDefinition
{
    public override string Describe() => "Originalfunktion";
}

[Description("Taste deaktivieren.")]
public sealed class NoneAction : ActionDefinition
{
    public override string Describe() => "Keine Funktion";
}

[Description("Mehrere Aktionen nacheinander, mit {\"type\":\"delay\",\"ms\":…} dazwischen.")]
public sealed class SequenceAction : ActionDefinition
{
    public List<ActionDefinition> Steps { get; set; } = [];
    public override string Describe() => $"Sequenz ({Steps.Count} Schritte)";
}

[Description("Pause innerhalb einer Sequenz.")]
public sealed class DelayAction : ActionDefinition
{
    public int Ms { get; set; } = 250;
    public override string Describe() => $"Pause {Ms} ms";
}

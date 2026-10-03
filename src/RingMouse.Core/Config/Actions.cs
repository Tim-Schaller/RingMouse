using System.ComponentModel;
using System.Text.Json.Serialization;
using static RingMouse.Core.Localization.Lang;

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

[Description("Opens a ring (button assignment only).")]
public sealed class OpenRingAction : ActionDefinition
{
    public string Ring { get; set; } = "main";
    public override string Describe() => L($"Open ring \"{Ring}\"", $"Ring \"{Ring}\" öffnen");
}

[Description("Send a keyboard shortcut, e.g. \"Ctrl+Shift+S\" or a sequence \"Ctrl+K, Ctrl+C\".")]
public sealed class KeysAction : ActionDefinition
{
    public string Keys { get; set; } = "";
    public override string Describe() => L($"Keys {Keys}", $"Tasten {Keys}");
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

[Description("Send a media key.")]
public sealed class MediaAction : ActionDefinition
{
    public MediaKey Key { get; set; } = MediaKey.PlayPause;
    public override string Describe() => Key switch
    {
        MediaKey.PlayPause => L("Play/Pause", "Wiedergabe/Pause"),
        MediaKey.Next => L("Next track", "Nächster Titel"),
        MediaKey.Previous => L("Previous track", "Vorheriger Titel"),
        MediaKey.Stop => L("Stop", "Stopp"),
        MediaKey.VolumeUp => L("Volume up", "Lauter"),
        MediaKey.VolumeDown => L("Volume down", "Leiser"),
        MediaKey.Mute => L("Mute", "Stumm"),
        _ => Key.ToString(),
    };
}

[Description("Launch a program, file, folder, URL or URI (also shell:AppsFolder\\...).")]
public sealed class LaunchAction : ActionDefinition
{
    public string Target { get; set; } = "";
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }

    [Description("With admin rights (UAC prompt). Otherwise always without admin rights, even if RingMouse runs elevated.")]
    public bool Elevated { get; set; }

    public override string Describe()
    {
        var args = string.IsNullOrWhiteSpace(Arguments) ? "" : " " + Arguments;
        return L($"Launch: {Target}{args}", $"Starten: {Target}{args}");
    }
}

public enum SnippetMode
{
    [Description("Type the characters via Unicode input (layout-independent)")] Type,
    [Description("Paste via the clipboard (Ctrl+V) and restore the clipboard")] Paste,
}

[Description("Insert text. Placeholders: {now:dd.MM.yyyy HH:mm}, {date}, {time}, {clipboard}, {user}, {computer}.")]
public sealed class SnippetAction : ActionDefinition
{
    public string Text { get; set; } = "";
    public SnippetMode Mode { get; set; } = SnippetMode.Type;
    public override string Describe() => $"Text: {(Text.Length > 40 ? Text[..40] + "…" : Text)}";
}

[Description("Run a PowerShell script (script) or command (command).")]
public sealed class PowerShellAction : ActionDefinition
{
    public string? Script { get; set; }
    public string? Command { get; set; }
    public string? Arguments { get; set; }
    public bool Hidden { get; set; } = true;

    [Description("Use pwsh.exe (PowerShell 7) instead of Windows PowerShell 5.1.")]
    public bool UsePwsh { get; set; }

    public bool Elevated { get; set; }
    public override string Describe() => Script is { Length: > 0 } ? $"PowerShell: {Script}" : $"PowerShell: {Command}";
}

[Description("Capture a screen region (Snipping Tool, like Win+Shift+S).")]
public sealed class ScreenshotAction : ActionDefinition
{
    public override string Describe() => L("Screenshot", "Bildschirmfoto");
}

[Description("Submenu: opens another ring at the same position.")]
public sealed class SubmenuAction : ActionDefinition
{
    public string Ring { get; set; } = "";
    public override string Describe() => L($"Submenu \"{Ring}\"", $"Untermenü \"{Ring}\"");
}

public enum SystemCommand
{
    [Description("Lock the workstation")] Lock,
    [Description("Emoji panel (Win+.)")] EmojiPanel,
    [Description("Show desktop (Win+D)")] ShowDesktop,
    [Description("Task view (Win+Tab)")] TaskView,
    [Description("Clipboard history (Win+V)")] ClipboardHistory,
    [Description("Turn off the screen")] MonitorOff,
    [Description("Open RingMouse settings")] OpenSettings,
}

[Description("System function.")]
public sealed class SystemAction : ActionDefinition
{
    public SystemCommand Command { get; set; } = SystemCommand.Lock;
    public override string Describe() => Command switch
    {
        SystemCommand.Lock => L("Lock", "Sperren"),
        SystemCommand.EmojiPanel => L("Emoji panel", "Emoji-Panel"),
        SystemCommand.ShowDesktop => L("Show desktop", "Desktop anzeigen"),
        SystemCommand.TaskView => L("Task view", "Aktive Anwendungen"),
        SystemCommand.ClipboardHistory => L("Clipboard history", "Zwischenablage-Verlauf"),
        SystemCommand.MonitorOff => L("Screen off", "Bildschirm aus"),
        SystemCommand.OpenSettings => L("RingMouse settings", "RingMouse-Einstellungen"),
        _ => Command.ToString(),
    };
}

[Description("Set the sensor DPI (one value) or cycle between several values.")]
public sealed class DpiAction : ActionDefinition
{
    public List<int> Values { get; set; } = [1000, 2000];

    public override string Describe()
    {
        if (Values.Count == 1) return $"DPI {Values[0]}";
        var values = string.Join("/", Values);
        return L($"Cycle DPI ({values})", $"DPI umschalten ({values})");
    }
}

[Description("Send a hotkey to a specific app (briefly activates its window, then returns focus), e.g. Spotify.")]
public sealed class AppKeysAction : ActionDefinition
{
    [Description("Process name, e.g. \"Spotify.exe\".")]
    public string Process { get; set; } = "";

    public string Keys { get; set; } = "";

    [Description("Activate the previous window again afterwards.")]
    public bool RestoreFocus { get; set; } = true;

    [Description("Optional: program/URI to launch if the process isn't running.")]
    public string? LaunchIfNotRunning { get; set; }

    public override string Describe() => L($"{Keys} to {Process}", $"{Keys} an {Process}");
}

public enum MouseButtonKind
{
    Left,
    Right,
    Middle,
    Back,
    Forward,
}

[Description("Trigger a mouse button (as a button assignment: held down like the physical button).")]
public sealed class MouseAction : ActionDefinition
{
    public MouseButtonKind Button { get; set; } = MouseButtonKind.Middle;
    public override string Describe() => L($"Mouse button {Button}", $"Maustaste {Button}");
}

[Description("The button's native function (pass-through).")]
public sealed class NativeAction : ActionDefinition
{
    public override string Describe() => L("Native function", "Originalfunktion");
}

[Description("Disable the button.")]
public sealed class NoneAction : ActionDefinition
{
    public override string Describe() => L("No function", "Keine Funktion");
}

[Description("Several actions in a row, with {\"type\":\"delay\",\"ms\":…} in between.")]
public sealed class SequenceAction : ActionDefinition
{
    public List<ActionDefinition> Steps { get; set; } = [];
    public override string Describe() => L($"Sequence ({Steps.Count} steps)", $"Sequenz ({Steps.Count} Schritte)");
}

[Description("Delay within a sequence.")]
public sealed class DelayAction : ActionDefinition
{
    public int Ms { get; set; } = 250;
    public override string Describe() => L($"Delay {Ms} ms", $"Pause {Ms} ms");
}

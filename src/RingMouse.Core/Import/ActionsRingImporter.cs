using System.Text;
using System.Text.Json;
using RingMouse.Core.Config;
using static RingMouse.Core.Localization.Lang;

namespace RingMouse.Core.Import;

/// <summary>
/// Actions-Ring-Profile von Logi Options+ (Logi Plugin Service, Loupedeck-Format "ProfileInfo.json" – auch in
/// exportierten .lp5-Presets): Hauptseite → Ring (Platz 0 oben, im Uhrzeigersinn), Ordnerseiten → Untermenüs,
/// Aktionen → RingMouse-Aktionen. Nicht Übertragbares landet mit Grund im Bericht.
/// </summary>
public static class ActionsRingImporter
{
    /// <summary>Gerätetyp des Actions Rings im Logi Plugin Service (andere Typen sind Tastenfelder/Drehregler).</summary>
    public const string RingDeviceType = "Loupedeck72";

    private const int MaxSegments = 8;
    private const int MaxDepth = 4;

    /// <summary>
    /// Liest ein ProfileInfo.json und legt den Ring als <paramref name="ringName"/> (plus Untermenüs) in
    /// <paramref name="result"/> an. Liefert die Zahl der übernommenen Aktionen auf der Hauptseite; unter
    /// <paramref name="minActions"/> wird nichts angelegt (der Bericht nennt dann nur, was nicht übertragbar war).
    /// </summary>
    public static int ImportRing(string profileInfoJson, string ringName, ImportResult result, string? context = null, int minActions = 1)
    {
        var ringsBefore = result.Config.Rings.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notesBefore = result.Notes.Count;
        var imported = ImportRingCore(profileInfoJson, ringName, result, context);
        if (imported >= minActions) return imported;

        foreach (var added in result.Config.Rings.Keys.Where(k => !ringsBefore.Contains(k)).ToList()) result.Config.Rings.Remove(added);
        for (var i = result.Notes.Count - 1; i >= notesBefore; i--)
            if (result.Notes[i].Kind == ImportNoteKind.Imported) result.Notes.RemoveAt(i);
        return imported;
    }

    private static int ImportRingCore(string profileInfoJson, string ringName, ImportResult result, string? context)
    {
        using var doc = JsonDocument.Parse(profileInfoJson, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        var root = doc.RootElement;
        context ??= Str(root, "displayName") ?? ringName;

        var deviceType = Str(root, "deviceType");
        if (deviceType is not null && !deviceType.Equals(RingDeviceType, StringComparison.OrdinalIgnoreCase))
        {
            result.Skipped(L($"{context}: not an Actions Ring profile ({deviceType})", $"{context}: kein Actions-Ring-Profil ({deviceType})"));
            return 0;
        }

        var reader = new Reader(root, result, context);
        if (reader.MainPage() is not { } page)
        {
            result.Skipped(L($"{context}: no ring page found", $"{context}: keine Ring-Seite gefunden"));
            return 0;
        }

        return reader.ImportPage(page, ringName, title: null, depth: 0, isMain: true);
    }

    /// <summary>Ein Ergebnis für einen Platz: Aktion (oder Untermenü-Ordner) mit Beschriftung, sonst Grund.</summary>
    private readonly record struct Converted(ActionDefinition? Action, string? Label, string? Icon, string? FolderName, string? Problem);

    private sealed class Reader(JsonElement root, ImportResult result, string context)
    {
        private readonly Dictionary<string, JsonElement> _profileActions = Index(root, "profileActions");
        private readonly Dictionary<string, JsonElement> _macros = Index(root, "macroCommands");
        private readonly Dictionary<string, JsonElement> _folders = IndexFolders(root);

        public JsonElement? MainPage()
        {
            if (!root.TryGetProperty("layout", out var layout) || !layout.TryGetProperty("layoutModes", out var modes)) return null;
            foreach (var mode in Items(modes))
                foreach (var ws in Items(Prop(mode, "workspaces")))
                    foreach (var page in Items(Prop(ws, "pressPages")))
                        if (Items(Prop(page, "controls")).Any()) return page;
            return null;
        }

        /// <summary>Legt eine Ring-Seite als Ring an; liefert die Zahl übernommener Aktionen.</summary>
        public int ImportPage(JsonElement page, string ringName, string? title, int depth, bool isMain)
        {
            var noteIndex = result.Notes.Count; // Ring-Zeile vor die Zeilen seiner Untermenüs stellen
            var controls = Items(Prop(page, "controls"))
                .Select(c => (Id: Int(c, "controlId"), Action: Str(c, "pressAction")))
                .Where(c => c.Id >= 0)
                .ToList();
            var count = isMain ? MaxSegments : Math.Clamp(controls.Count == 0 ? 0 : controls.Max(c => c.Id) + 1, 2, MaxSegments);
            foreach (var c in controls.Where(c => c.Id >= MaxSegments))
                result.Skipped(L($"{context}: slot {c.Id + 1} – a ring has at most 8 slots", $"{context}: Platz {c.Id + 1} – ein Ring hat höchstens 8 Plätze"));

            var segments = new RingSegment?[count];
            var imported = 0;
            foreach (var (id, actionId) in controls.Where(c => c.Id < count))
            {
                if (string.IsNullOrEmpty(actionId)) continue;
                var converted = Convert(actionId, depth);
                if (converted.FolderName is { } folder)
                {
                    if (depth >= MaxDepth || !_folders.TryGetValue(folder, out var folderPage))
                    {
                        Skip(id, converted.Label, L("folder not found", "Ordner nicht gefunden"));
                        continue;
                    }
                    var label = converted.Label ?? L("Folder", "Ordner");
                    var subName = UniqueRingName(ringName, label);
                    var subCount = ImportPage(folderPage, subName, label, depth + 1, isMain: false);
                    if (subCount == 0)
                    {
                        result.Config.Rings.Remove(subName);
                        Skip(id, label, L("folder without transferable actions", "Ordner ohne übertragbare Aktionen"));
                        continue;
                    }
                    segments[id] = new RingSegment { Label = label, Icon = converted.Icon, Action = new SubmenuAction { Ring = subName } };
                    imported++;
                }
                else if (converted.Action is { } action)
                {
                    segments[id] = new RingSegment { Label = converted.Label ?? action.Describe(), Icon = converted.Icon, Action = action };
                    imported++;
                }
                else
                {
                    Skip(id, converted.Label, converted.Problem ?? L("not supported", "nicht unterstützt"));
                }
            }

            if (imported == 0) return 0;
            result.Config.Rings[ringName] = new RingDefinition { Title = title, Segments = [.. segments] };
            result.Notes.Insert(noteIndex, new ImportNote(ImportNoteKind.Imported,
                L($"{context}: ring \"{ringName}\" with {imported} action(s)", $"{context}: Ring \"{ringName}\" mit {imported} Aktion(en)")));
            return imported;
        }

        private void Skip(int slot, string? label, string reason) =>
            result.Skipped(L($"{context}: slot {slot + 1}{(label is null ? "" : $" \"{label}\"")} – {reason}",
                $"{context}: Platz {slot + 1}{(label is null ? "" : $" „{label}“")} – {reason}"));

        private string UniqueRingName(string parent, string label)
        {
            var prefix = parent.Equals(DefaultConfig.MainRing, StringComparison.OrdinalIgnoreCase)
                ? ""
                : (parent.StartsWith("main-", StringComparison.OrdinalIgnoreCase) ? parent[5..] : parent) + "-";
            var baseName = prefix + Slug(label);
            var name = baseName;
            for (var i = 2; result.Config.Rings.ContainsKey(name) || name.Equals(DefaultConfig.MainRing, StringComparison.OrdinalIgnoreCase); i++)
                name = $"{baseName}-{i}";
            return name;
        }

        // ------------------------------------------------------------------ Aktionen

        private Converted Convert(string actionId, int depth)
        {
            var (group, name, parameter) = SplitId(actionId);
            switch (group)
            {
                case "DefaultWin":
                    return BuiltIn(name);
                case "Spotify":
                    return Spotify(name);
                case "@Generic" when name == "@ProfileAction":
                    return parameter is not null && _profileActions.TryGetValue(Guid(parameter), out var pa)
                        ? FromTemplate(Str(pa, "templateActionName"), Parameters(pa), Str(pa, "displayName"))
                        : new Converted(null, null, null, null, L("action definition missing", "Aktionsdefinition fehlt"));
                case "@Generic" when name == "@Macro":
                    return parameter is not null && _macros.TryGetValue(Guid(parameter), out var macro)
                        ? Macro(macro, depth)
                        : new Converted(null, null, null, null, L("macro definition missing", "Makrodefinition fehlt"));
                case "@Generic" when name == "@ExecuteApplication":
                    return string.IsNullOrWhiteSpace(parameter)
                        ? new Converted(null, null, null, null, L("no target", "kein Ziel"))
                        : new Converted(new LaunchAction { Target = parameter }, LabelFromTarget(parameter), null, null, null);
                case "@Generic" when name.StartsWith("@Adjustment", StringComparison.Ordinal):
                    return new Converted(null, null, null, null, L("dial adjustment (not possible in a ring)", "Drehregler-Funktion (im Ring nicht möglich)"));
                default:
                    return new Converted(null, Humanize(name), null, null,
                        L($"action of the \"{group}\" plugin is not supported", $"Aktion des Plugins „{group}“ wird nicht unterstützt"));
            }
        }

        private Converted FromTemplate(string? template, IReadOnlyDictionary<string, string> p, string? label)
        {
            switch (template)
            {
                case "$@Generic___@KeyboardKey":
                    if (OptionsPlusKeys.TryFromKeyboardKey(p.GetValueOrDefault("keyboardKey"), out var keys, out var macOnly))
                        return new Converted(new KeysAction { Keys = keys }, label ?? keys, null, null, null);
                    return new Converted(null, label, null, null, macOnly
                        ? L("macOS keyboard shortcut", "macOS-Tastenkürzel")
                        : L("keyboard shortcut not recognized", "Tastenkürzel nicht erkannt"));
                case "$@Generic___@SendText":
                    var text = p.GetValueOrDefault("text") ?? "";
                    var paste = string.Equals(p.GetValueOrDefault("useClipboard"), "true", StringComparison.OrdinalIgnoreCase);
                    return new Converted(new SnippetAction { Text = text, Mode = paste ? SnippetMode.Paste : SnippetMode.Type }, label, null, null, null);
                case "$@Generic___@ShellExecute":
                case "$@Generic___@ExecuteApplication":
                    var target = p.GetValueOrDefault("filePath") ?? p.GetValueOrDefault("application") ?? p.Values.FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(target)) return new Converted(null, label, null, null, L("no target", "kein Ziel"));
                    if (target.StartsWith('/')) return new Converted(null, label, null, null, L("macOS path", "macOS-Pfad"));
                    return new Converted(new LaunchAction { Target = target }, label ?? LabelFromTarget(target), null, null, null);
                case "$@Generic___@OpenFolder":
                    return p.GetValueOrDefault("folderName") is { Length: > 0 } folder
                        ? new Converted(null, label, null, folder, null)
                        : new Converted(null, label, null, null, L("folder not found", "Ordner nicht gefunden"));
                case "$Spotify___StartPlaylistActionEditor":
                    return p.GetValueOrDefault("playlist") is { Length: > 0 } playlist
                        ? new Converted(new LaunchAction { Target = playlist }, label ?? "Spotify", "Playlist", null, null)
                        : new Converted(null, label, null, null, L("no playlist", "keine Playlist"));
                case "$@Generic___@EasySwitch":
                    return new Converted(null, label ?? "Easy-Switch", null, null, L("Easy-Switch is not supported", "Easy-Switch wird nicht unterstützt"));
                default:
                    if (template is not null && SplitId(template) is { Group: "DefaultWin" or "Spotify" } inner && p.Count == 0)
                    {
                        var c = inner.Group == "DefaultWin" ? BuiltIn(inner.Name) : Spotify(inner.Name);
                        return c with { Label = label ?? c.Label };
                    }
                    return new Converted(null, label, null, null, L($"action type \"{template}\" is not supported", $"Aktionstyp „{template}“ wird nicht unterstützt"));
            }
        }

        private Converted Macro(JsonElement macro, int depth)
        {
            var label = Str(macro, "displayName");
            var steps = new List<ActionDefinition>();
            foreach (var step in Items(Prop(macro, "actions")))
            {
                var id = step.ValueKind == JsonValueKind.String ? step.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;
                var c = depth < MaxDepth ? Convert(id, depth + 1) : default;
                if (c.Action is null || c.FolderName is not null)
                    return new Converted(null, label, null, null, L($"macro step not supported ({c.Problem ?? id})", $"Makroschritt nicht unterstützt ({c.Problem ?? id})"));
                steps.Add(c.Action);
            }
            return steps.Count switch
            {
                0 => new Converted(null, label, null, null, L("empty macro", "leeres Makro")),
                1 => new Converted(steps[0], label, null, null, null),
                _ => new Converted(new SequenceAction { Steps = steps }, label, null, null, null),
            };
        }

        private static Converted BuiltIn(string name)
        {
            static Converted A(ActionDefinition a, string en, string de, string? icon) => new(a, L(en, de), icon, null, null);
            static MediaAction Media(MediaKey k) => new() { Key = k };
            static SystemAction Sys(SystemCommand c) => new() { Command = c };
            return name switch
            {
                "MediaPlayPause" => A(Media(MediaKey.PlayPause), "Play/Pause", "Wiedergabe/Pause", "PlayPause"),
                "MediaNextTrack" => A(Media(MediaKey.Next), "Next track", "Nächster Titel", "Next"),
                "MediaPrevTrack" or "MediaPreviousTrack" => A(Media(MediaKey.Previous), "Previous track", "Vorheriger Titel", "Previous"),
                "MediaStop" => A(Media(MediaKey.Stop), "Stop", "Stopp", "Stop"),
                "VolumeUp" => A(Media(MediaKey.VolumeUp), "Volume up", "Lauter", "VolumeUp"),
                "VolumeDown" => A(Media(MediaKey.VolumeDown), "Volume down", "Leiser", "VolumeDown"),
                "VolumeMute" or "Mute" or "ToggleMute" => A(Media(MediaKey.Mute), "Mute", "Stumm", "Mute"),
                "WindowsEmoji" => A(Sys(SystemCommand.EmojiPanel), "Emoji", "Emoji", "Emoji"),
                "LockWorkstation" or "LockScreen" => A(Sys(SystemCommand.Lock), "Lock", "Sperren", "Lock"),
                "WindowsScreenshot" or "Screenshot" or "ScreenSnip" => A(new ScreenshotAction(), "Screenshot", "Bildschirmfoto", "Screenshot"),
                "WindowsExplorer" or "FileExplorer" => A(new LaunchAction { Target = "explorer.exe" }, "Explorer", "Explorer", "Folder"),
                "ShowDesktop" or "WindowsShowDesktop" => A(Sys(SystemCommand.ShowDesktop), "Desktop", "Desktop", "Desktop"),
                "TaskView" or "WindowsTaskView" => A(Sys(SystemCommand.TaskView), "Task view", "Aktive Anwendungen", "TaskView"),
                "ClipboardHistory" or "WindowsClipboard" => A(Sys(SystemCommand.ClipboardHistory), "Clipboard", "Zwischenablage", "Clipboard"),
                "Calculator" or "WindowsCalculator" => A(new LaunchAction { Target = "calc.exe" }, "Calculator", "Rechner", null),
                "WindowsSettings" or "Settings" => A(new LaunchAction { Target = "ms-settings:" }, "Settings", "Einstellungen", "Settings"),
                "TaskManager" => A(new KeysAction { Keys = "Ctrl+Shift+Esc" }, "Task Manager", "Task-Manager", null),
                "Copy" => A(new KeysAction { Keys = "Ctrl+C" }, "Copy", "Kopieren", "Copy"),
                "Paste" => A(new KeysAction { Keys = "Ctrl+V" }, "Paste", "Einfügen", "Paste"),
                "Cut" => A(new KeysAction { Keys = "Ctrl+X" }, "Cut", "Ausschneiden", "Cut"),
                "Undo" => A(new KeysAction { Keys = "Ctrl+Z" }, "Undo", "Rückgängig", "Undo"),
                "Redo" => A(new KeysAction { Keys = "Ctrl+Y" }, "Redo", "Wiederholen", "Redo"),
                _ => new Converted(null, Humanize(name), null, null, L($"Windows action \"{name}\" is not supported", $"Windows-Aktion „{name}“ wird nicht unterstützt")),
            };
        }

        /// <summary>Spotify-Plugin (nutzt die Web-API): lokal über die Desktop-Hotkeys des Spotify-Fensters.</summary>
        private static Converted Spotify(string name)
        {
            static Converted Keys(string keys, string en, string de, string icon) =>
                new(new AppKeysAction { Process = "Spotify.exe", Keys = keys }, L(en, de), icon, null, null);
            return name switch
            {
                "ShufflePlay" or "ToggleShuffle" => Keys("Ctrl+S", "Shuffle", "Zufallswiedergabe", "Shuffle"),
                "ToggleLike" or "Like" => Keys("Alt+Shift+B", "Like", "Gefällt mir", "Heart"),
                "ChangeRepeatState" or "ToggleRepeat" => Keys("Ctrl+R", "Repeat", "Wiederholen", "Repeat"),
                "VolumeUp" => Keys("Ctrl+Up", "Volume up", "Lauter", "VolumeUp"),
                "VolumeDown" => Keys("Ctrl+Down", "Volume down", "Leiser", "VolumeDown"),
                "PlayPause" or "TogglePlayback" or "Play" => new(new MediaAction { Key = MediaKey.PlayPause }, L("Play/Pause", "Wiedergabe/Pause"), "PlayPause", null, null),
                "NextTrack" or "Next" => new(new MediaAction { Key = MediaKey.Next }, L("Next track", "Nächster Titel"), "Next", null, null),
                "PreviousTrack" or "Previous" => new(new MediaAction { Key = MediaKey.Previous }, L("Previous track", "Vorheriger Titel"), "Previous", null, null),
                _ => new(null, Humanize(name), null, null, L($"Spotify action \"{name}\" is not supported", $"Spotify-Aktion „{name}“ wird nicht unterstützt")),
            };
        }
    }

    // ------------------------------------------------------------------ JSON-Helfer

    /// <summary>"$Gruppe___Name___Parameter" (Parameter optional, darf selbst "___" enthalten).</summary>
    private static (string Group, string Name, string? Parameter) SplitId(string id)
    {
        var parts = id.TrimStart('$').Split("___");
        return (parts[0], parts.Length > 1 ? parts[1] : "", parts.Length > 2 ? string.Join("___", parts[2..]) : null);
    }

    /// <summary>GUID-Teil einer Aktions-ID ("$@Generic___@ProfileAction___ABC…" oder nur "ABC…").</summary>
    private static string Guid(string id) => id.Split("___")[^1];

    private static Dictionary<string, JsonElement> Index(JsonElement root, string property)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items(Prop(root, property)))
            if (Str(item, "name") is { } name) map[Guid(name)] = item;
        return map;
    }

    private static Dictionary<string, JsonElement> IndexFolders(JsonElement root)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("layout", out var layout))
            foreach (var page in Items(Prop(layout, "folderPages")))
                if (Str(page, "name") is { } name) map[name] = page;
        return map;
    }

    private static IReadOnlyDictionary<string, string> Parameters(JsonElement action)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (action.TryGetProperty("actionParameters", out var ap) && ap.ValueKind == JsonValueKind.Object &&
            ap.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object)
            foreach (var prop in p.EnumerateObject())
                if (!prop.Name.StartsWith('$') && prop.Value.ValueKind == JsonValueKind.String)
                    result[prop.Name] = prop.Value.GetString() ?? "";
        return result;
    }

    private static JsonElement Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    private static IEnumerable<JsonElement> Items(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : -1;

    /// <summary>"ShufflePlay" → "Shuffle Play".</summary>
    private static string Humanize(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.TrimStart('@'))
        {
            if (char.IsUpper(c) && sb.Length > 0 && !char.IsUpper(sb[^1])) sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string LabelFromTarget(string target)
    {
        try
        {
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return uri.Host.Replace("www.", "");
            return Path.GetFileNameWithoutExtension(target.Trim('"')) is { Length: > 0 } file ? file : target;
        }
        catch (ArgumentException)
        {
            return target;
        }
    }

    /// <summary>Ringname aus einer Beschriftung: klein, ASCII, Bindestriche ("Mein Ordner" → "mein-ordner").</summary>
    public static string Slug(string text)
    {
        var normalized = text.Trim().ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        var sb = new StringBuilder();
        foreach (var c in normalized)
            sb.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-');
        var slug = string.Join("-", sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "submenu" : slug.Length > 32 ? slug[..32].TrimEnd('-') : slug;
    }
}

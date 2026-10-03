using System.Text.Json;
using RingMouse.Core.Config;
using RingMouse.HidPlusPlus.Features;
using static RingMouse.Core.Localization.Lang;

namespace RingMouse.Core.Import;

/// <summary>Eine Maus mit Belegungen in Options+ (Slot-Präfix "mx-vertical-eb020" → "MX Vertical").</summary>
public sealed record OptionsPlusDevice(string SlotPrefix, string DisplayName);

/// <summary>Ein Actions-Ring-Profil aus dem Logi Plugin Service ("@_defaultwin" = Standard, sonst eine App).</summary>
public sealed record OptionsPlusRingSource(string ApplicationName, string DisplayName, string? ProcessName, string ProfileInfoJson);

/// <summary>
/// Import aus einer installierten Logi Options+: Actions Ring (Logi Plugin Service) sowie Tastenbelegungen und
/// App-Profile (Options+-Einstellungsdokument aus settings.db). Gelesen werden nur Profile und Anwendungen –
/// keine Konto-, Analyse- oder Telemetriedaten.
/// </summary>
public sealed class OptionsPlusImporter : IDisposable
{
    public const string DefaultRingApplication = "@_defaultwin";

    private readonly JsonDocument? _settings;
    private readonly IReadOnlyList<OptionsPlusRingSource> _rings;

    public OptionsPlusImporter(string? settingsJson, IReadOnlyList<OptionsPlusRingSource> rings)
    {
        _settings = string.IsNullOrWhiteSpace(settingsJson) ? null : JsonDocument.Parse(settingsJson);
        _rings = rings;
        Mice = FindMice();
    }

    /// <summary>Mäuse, für die Options+ Belegungen gespeichert hat.</summary>
    public IReadOnlyList<OptionsPlusDevice> Mice { get; }

    public bool HasAnything => _rings.Count > 0 || Mice.Count > 0;

    /// <summary>Teil-Config bauen; Tastenbelegungen kommen von der Maus mit <paramref name="slotPrefix"/>.</summary>
    public ImportResult Build(string? slotPrefix)
    {
        var result = new ImportResult(L("Logi Options+ (installed)", "Logi Options+ (installiert)"));
        var profiles = new List<ProfileDefinition>();

        foreach (var ring in _rings.OrderBy(r => !r.ApplicationName.Equals(DefaultRingApplication, StringComparison.OrdinalIgnoreCase)))
            ImportRing(ring, result, profiles);

        if (slotPrefix is not null && Root is { } root)
        {
            var device = Mice.FirstOrDefault(m => m.SlotPrefix == slotPrefix)?.DisplayName ?? slotPrefix;
            ImportButtons(root, slotPrefix, device, result, profiles);
        }

        foreach (var profile in profiles)
        {
            result.Config.Profiles.Add(profile);
            var parts = profile.Buttons
                .Select(b => ControlIds.TryParse(b.Key, out var cid) ? $"{ControlIds.GetName(cid)} → {b.Value.Describe()}" : $"{b.Key} → {b.Value.Describe()}")
                .ToList();
            if (profile.Rings.GetValueOrDefault(DefaultConfig.MainRing) is { } ring) parts.Add(L($"ring \"{ring}\"", $"Ring „{ring}“"));
            result.Imported(L($"Profile \"{profile.Name}\" ({string.Join(", ", profile.Processes)}): {string.Join("; ", parts)}",
                $"Profil „{profile.Name}“ ({string.Join(", ", profile.Processes)}): {string.Join("; ", parts)}"));
        }
        return result;
    }

    private JsonElement? Root => _settings?.RootElement;

    // ------------------------------------------------------------------ Ringe

    private static void ImportRing(OptionsPlusRingSource ring, ImportResult result, List<ProfileDefinition> profiles)
    {
        if (ring.ApplicationName.Equals(DefaultRingApplication, StringComparison.OrdinalIgnoreCase))
        {
            ActionsRingImporter.ImportRing(ring.ProfileInfoJson, DefaultConfig.MainRing, result, L("Actions Ring", "Actions Ring"));
            return;
        }

        var context = L($"Ring for {ring.DisplayName}", $"Ring für {ring.DisplayName}");
        if (ProcessFor(ring) is not { } process)
        {
            result.Skipped(L($"{context}: program unknown – not imported", $"{context}: Programm unbekannt – nicht übernommen"));
            return;
        }
        var ringName = "main-" + ActionsRingImporter.Slug(ring.DisplayName);
        // Ein App-Ring mit höchstens einer Aktion würde den Standard-Ring nur verschlechtern
        if (ActionsRingImporter.ImportRing(ring.ProfileInfoJson, ringName, result, context, minActions: 2) < 2)
        {
            result.Skipped(L($"{context}: fewer than 2 transferable actions – the default ring applies",
                $"{context}: weniger als 2 übertragbare Aktionen – es gilt der Standard-Ring"));
            return;
        }
        ProfileFor(profiles, ring.DisplayName, [process]).Rings[DefaultConfig.MainRing] = ringName;
    }

    /// <summary>Prozess einer Plugin-Service-App: angegeben oder für die mitgelieferten Office-/Adobe-/Browser-Plugins bekannt.</summary>
    private static string? ProcessFor(OptionsPlusRingSource ring)
    {
        if (!string.IsNullOrWhiteSpace(ring.ProcessName))
            return ring.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? ring.ProcessName : ring.ProcessName + ".exe";
        var app = ring.ApplicationName.ToLowerInvariant();
        string[][] known =
        [
            ["@_excel", "excel.exe"], ["@_word", "winword.exe"], ["@_powerpoint", "powerpnt.exe"], ["@_outlook", "outlook.exe"],
            ["@_onenote", "onenote.exe"], ["@_photoshop", "photoshop.exe"], ["@_illustrator", "illustrator.exe"],
            ["@_premiere", "adobe premiere pro.exe"], ["@_lightroom", "lightroom.exe"], ["@_chrome", "chrome.exe"],
            ["@_edge", "msedge.exe"], ["@_firefox", "firefox.exe"], ["@_zoom", "zoom.exe"],
        ];
        return known.FirstOrDefault(k => app.StartsWith(k[0], StringComparison.Ordinal))?[1];
    }

    // ------------------------------------------------------------------ Tasten und App-Profile

    private sealed record Mapped(ActionDefinition? Action, bool Native, string Label, string? Problem);

    private static void ImportButtons(JsonElement root, string prefix, string device, ImportResult result, List<ProfileDefinition> profiles)
    {
        var defaultProfile = DefaultProfile(root);
        if (defaultProfile is not { } dp)
        {
            result.Skipped(L("Options+: no default profile found", "Options+: kein Standardprofil gefunden"));
            return;
        }

        var defaults = MapAssignments(dp, prefix);
        foreach (var (cid, m) in defaults)
        {
            var button = $"{ControlIds.Format(cid)} {ControlIds.GetName(cid)}";
            if (m.Action is { } action)
            {
                result.Config.Buttons[ControlIds.Format(cid)] = action;
                result.Imported(L($"{device}: {button} → {action.Describe()}", $"{device}: {button} → {action.Describe()}"));
            }
            else if (m.Problem is { } problem)
            {
                result.Skipped(L($"{device}: {button} \"{m.Label}\" – {problem}", $"{device}: {button} „{m.Label}“ – {problem}"));
            }
        }

        var apps = Applications(root);
        foreach (var property in root.EnumerateObject().Where(p => p.Name.StartsWith("profile-application", StringComparison.OrdinalIgnoreCase)))
        {
            var appId = Str(property.Value, "applicationId") ?? "";
            if (!apps.TryGetValue(appId, out var app) || app.Processes.Count == 0)
            {
                result.Skipped(L($"App profile {appId}: program unknown – not imported", $"App-Profil {appId}: Programm unbekannt – nicht übernommen"));
                continue;
            }

            var overrides = new Dictionary<string, ActionDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var (cid, m) in MapAssignments(property.Value, prefix))
            {
                var inherited = defaults.GetValueOrDefault(cid);
                if (m.Action is { } action)
                {
                    if (inherited?.Action is { } d && Same(d, action)) continue;
                    overrides[ControlIds.Format(cid)] = action;
                }
                else if (m.Native)
                {
                    if (inherited?.Action is not null) overrides[ControlIds.Format(cid)] = new NativeAction();
                }
                else if (m.Problem is { } problem && !(inherited?.Problem is not null && inherited.Label == m.Label))
                {
                    result.Skipped(L($"{app.Name}: {ControlIds.Format(cid)} \"{m.Label}\" – {problem}; the default applies",
                        $"{app.Name}: {ControlIds.Format(cid)} „{m.Label}“ – {problem}; es gilt der Standard"));
                }
            }
            if (overrides.Count == 0) continue;

            var profile = ProfileFor(profiles, app.Name, app.Processes);
            foreach (var (key, action) in overrides) profile.Buttons[key] = action;
        }
    }

    private static Dictionary<ushort, Mapped> MapAssignments(JsonElement profile, string prefix)
    {
        var map = new Dictionary<ushort, Mapped>();
        foreach (var a in Items(Prop(profile, "assignments")))
        {
            var slot = Str(a, "slotId") ?? "";
            if (!slot.StartsWith(prefix + "_c", StringComparison.OrdinalIgnoreCase) ||
                !ushort.TryParse(slot[(prefix.Length + 2)..], out var cid)) continue;
            map[cid] = MapCard(Prop(a, "card"), cid);
        }
        return map;
    }

    private static Mapped MapCard(JsonElement card, ushort cid)
    {
        var label = Humanize(Str(card, "name") ?? "");
        if (!string.Equals(Str(card, "attribute"), "MACRO_PLAYBACK", StringComparison.OrdinalIgnoreCase))
            return new Mapped(null, false, label, L("setting, not an action", "Einstellung, keine Aktion"));

        var macro = Prop(card, "macro");
        switch (Str(macro, "type"))
        {
            case "SYSTEM":
                var system = Str(Prop(macro, "system"), "action") ?? "";
                return MapSystem(system, label);
            case "MOUSE":
                var mouse = Prop(macro, "mouse");
                MouseButtonKind? kind = (Str(mouse, "action") ?? "") switch
                {
                    "WIN_BACK" => MouseButtonKind.Back,
                    "WIN_FORWARD" => MouseButtonKind.Forward,
                    "BUTTON" => Int(mouse, "hidUsage") switch
                    {
                        1 => MouseButtonKind.Left,
                        2 => MouseButtonKind.Right,
                        3 => MouseButtonKind.Middle,
                        4 => MouseButtonKind.Back,
                        5 => MouseButtonKind.Forward,
                        _ => null,
                    },
                    _ => null,
                };
                if (kind is not { } k) return new Mapped(null, false, label, L("mouse function not supported", "Mausfunktion nicht unterstützt"));
                return IsNative(cid, k) ? new Mapped(null, true, label, null) : new Mapped(new MouseAction { Button = k }, false, label, null);
            case "KEYSTROKE":
                var stroke = Prop(macro, "keystroke");
                if (OptionsPlusKeys.TryFromDisplay(Str(macro, "actionName"), out var keys) ||
                    OptionsPlusKeys.TryFromHid(Int(stroke, "code"), Items(Prop(stroke, "modifiers")).Select(m => m.TryGetInt32(out var v) ? v : -1), out keys))
                    return new Mapped(new KeysAction { Keys = keys }, false, label, null);
                return new Mapped(null, false, label, L("keyboard shortcut not recognized", "Tastenkürzel nicht erkannt"));
            case "QUICK_LAUNCH":
                var target = FindString(Prop(macro, "quickLaunch"), "path", "url", "applicationPath", "target", "executable");
                return target is null
                    ? new Mapped(null, false, label, L("quick launch without target", "Schnellstart ohne Ziel"))
                    : new Mapped(new LaunchAction { Target = target }, false, label, null);
            case "ADVANCED_CLICK":
                return new Mapped(null, false, label, L("double click and similar are not supported", "Doppelklick u. Ä. wird nicht unterstützt"));
            default:
                return new Mapped(null, false, label, L("not transferable", "nicht übertragbar"));
        }
    }

    private static Mapped MapSystem(string action, string label)
    {
        ActionDefinition? a = action switch
        {
            "SHOW_RADIAL_MENU" => new OpenRingAction { Ring = DefaultConfig.MainRing },
            _ when action.Contains("DESKTOP", StringComparison.Ordinal) => new SystemAction { Command = SystemCommand.ShowDesktop },
            _ when action.Contains("TASK_VIEW", StringComparison.Ordinal) => new SystemAction { Command = SystemCommand.TaskView },
            _ when action.Contains("LOCK", StringComparison.Ordinal) => new SystemAction { Command = SystemCommand.Lock },
            _ when action.Contains("EMOJI", StringComparison.Ordinal) => new SystemAction { Command = SystemCommand.EmojiPanel },
            _ when action.Contains("SCREEN", StringComparison.Ordinal) && action.Contains("SHOT", StringComparison.Ordinal) => new ScreenshotAction(),
            _ when action.Contains("PLAY_PAUSE", StringComparison.Ordinal) => new MediaAction { Key = MediaKey.PlayPause },
            _ when action.Contains("NEXT_TRACK", StringComparison.Ordinal) => new MediaAction { Key = MediaKey.Next },
            _ when action.Contains("PREV", StringComparison.Ordinal) => new MediaAction { Key = MediaKey.Previous },
            _ when action.Contains("VOLUME_UP", StringComparison.Ordinal) => new MediaAction { Key = MediaKey.VolumeUp },
            _ when action.Contains("VOLUME_DOWN", StringComparison.Ordinal) => new MediaAction { Key = MediaKey.VolumeDown },
            _ when action.Contains("MUTE", StringComparison.Ordinal) => new MediaAction { Key = MediaKey.Mute },
            _ => null,
        };
        return a is null
            ? new Mapped(null, false, label, L($"system function \"{action}\" is not supported", $"Systemfunktion „{action}“ wird nicht unterstützt"))
            : new Mapped(a, false, label, null);
    }

    private static bool IsNative(ushort cid, MouseButtonKind kind) => ControlIds.GetStandardMouseButton(cid) switch
    {
        StandardMouseButton.Left => kind == MouseButtonKind.Left,
        StandardMouseButton.Right => kind == MouseButtonKind.Right,
        StandardMouseButton.Middle => kind == MouseButtonKind.Middle,
        StandardMouseButton.Back => kind == MouseButtonKind.Back,
        StandardMouseButton.Forward => kind == MouseButtonKind.Forward,
        _ => false,
    };

    private static bool Same(ActionDefinition a, ActionDefinition b) =>
        JsonSerializer.Serialize(a, ConfigSerializer.Options) == JsonSerializer.Serialize(b, ConfigSerializer.Options);

    private static JsonElement? DefaultProfile(JsonElement root)
    {
        JsonElement? first = null;
        foreach (var p in root.EnumerateObject())
        {
            if (!p.Name.StartsWith("profile-", StringComparison.OrdinalIgnoreCase) ||
                p.Name.StartsWith("profile-application", StringComparison.OrdinalIgnoreCase) ||
                p.Value.ValueKind != JsonValueKind.Object) continue;
            if (Str(p.Value, "name") == "PROFILE_NAME_DEFAULT") return p.Value;
            first ??= p.Value;
        }
        return first;
    }

    private sealed record App(string Name, List<string> Processes);

    private static Dictionary<string, App> Applications(JsonElement root)
    {
        var map = new Dictionary<string, App>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in Items(Prop(Prop(root, "applications"), "applications")))
        {
            if (Str(a, "applicationId") is not { } id) continue;
            var paths = Items(Prop(a, "applicationPathsList")).Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() : null)
                .Append(Str(a, "applicationPath"))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => Path.GetFileName(p!.Trim('"')).ToLowerInvariant())
                .Where(p => p.EndsWith(".exe", StringComparison.Ordinal))
                .Distinct()
                .ToList();
            map[id] = new App(Str(a, "name") ?? id, paths);
        }
        return map;
    }

    private static ProfileDefinition ProfileFor(List<ProfileDefinition> profiles, string name, IReadOnlyList<string> processes)
    {
        static string Norm(string p) => ProcessPatternKey(p);
        var keys = processes.Select(Norm).ToHashSet();
        var profile = profiles.FirstOrDefault(p => p.Processes.Any(x => keys.Contains(Norm(x))));
        if (profile is null)
        {
            profile = new ProfileDefinition { Name = name };
            profiles.Add(profile);
        }
        foreach (var process in processes.Where(p => !profile.Processes.Any(x => Norm(x) == Norm(p))))
            profile.Processes.Add(process);
        return profile;
    }

    private static string ProcessPatternKey(string process)
    {
        var name = Path.GetFileName(process.Trim()).ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
    }

    // ------------------------------------------------------------------ Mäuse

    private IReadOnlyList<OptionsPlusDevice> FindMice()
    {
        if (Root is not { } root || DefaultProfile(root) is not { } profile) return [];
        var prefixes = Items(Prop(profile, "assignments"))
            .Select(a => Str(a, "slotId") ?? "")
            .Where(s => s.EndsWith("_mouse_settings", StringComparison.OrdinalIgnoreCase))
            .Select(s => s[..^"_mouse_settings".Length])
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return prefixes.Select(p => new OptionsPlusDevice(p, DeviceName(p))).ToList();
    }

    /// <summary>"mx-vertical-eb020" → "MX Vertical", "m720-triathlon-6b015" → "M720 Triathlon".</summary>
    public static string DeviceName(string slotPrefix)
    {
        var tokens = slotPrefix.Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (tokens.Count > 1 && tokens[^1].Length >= 4 && tokens[^1].All(Uri.IsHexDigit)) tokens.RemoveAt(tokens.Count - 1);
        return string.Join(" ", tokens.Select(t => t.Length <= 2 || t.Any(char.IsDigit)
            ? t.ToUpperInvariant()
            : char.ToUpperInvariant(t[0]) + t[1..]));
    }

    // ------------------------------------------------------------------ JSON-Helfer

    private static string Humanize(string assignmentName)
    {
        var name = assignmentName.StartsWith("ASSIGNMENT_NAME_", StringComparison.Ordinal) ? assignmentName[16..] : assignmentName;
        name = name.Replace('_', ' ').ToLowerInvariant();
        return name.Length == 0 ? "?" : char.ToUpperInvariant(name[0]) + name[1..];
    }

    private static string? FindString(JsonElement e, params string[] names)
    {
        foreach (var n in names)
            if (Str(e, n) is { Length: > 0 } s) return s;
        return null;
    }

    private static JsonElement Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    private static IEnumerable<JsonElement> Items(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

    public void Dispose() => _settings?.Dispose();
}

using System.Text.RegularExpressions;
using RingMouse.Core.Config;
using RingMouse.HidPlusPlus.Features;

namespace RingMouse.Core.Profiles;

/// <summary>Effektive Belegung für einen Vordergrundprozess (Profil + geerbter Standard).</summary>
public sealed class ResolvedProfile
{
    private readonly RingMouseConfig _config;
    private readonly ProfileDefinition? _profile;

    internal ResolvedProfile(RingMouseConfig config, ProfileDefinition? profile, IReadOnlyDictionary<ushort, ActionDefinition> buttons)
    {
        _config = config;
        _profile = profile;
        Buttons = buttons;
    }

    /// <summary>"Standard" oder Name des aktiven Profils.</summary>
    public string Name => _profile?.Name ?? "Standard";

    public bool IsDefault => _profile is null;

    public IReadOnlyDictionary<ushort, ActionDefinition> Buttons { get; }

    public ActionDefinition? ActionFor(ushort controlId) => Buttons.GetValueOrDefault(controlId);

    /// <summary>Ring nach Name – berücksichtigt Ring-Ersetzungen des Profils.</summary>
    public RingDefinition? Ring(string name)
    {
        if (_profile is not null && _profile.Rings.TryGetValue(name, out var replacement) &&
            _config.Rings.TryGetValue(replacement, out var replaced))
            return replaced;
        return _config.Rings.GetValueOrDefault(name);
    }
}

/// <summary>Löst Prozessnamen → Profil auf. Profile erben alles, was sie nicht explizit überschreiben.</summary>
public sealed class ProfileResolver
{
    private readonly RingMouseConfig _config;
    private readonly IReadOnlyDictionary<ushort, ActionDefinition> _defaultButtons;
    private readonly List<(ProfileDefinition Profile, Regex[] Patterns, IReadOnlyDictionary<ushort, ActionDefinition> Buttons)> _profiles = [];
    private readonly ResolvedProfile _default;

    public ProfileResolver(RingMouseConfig config)
    {
        _config = config;
        _defaultButtons = ParseButtons(config.Buttons);
        _default = new ResolvedProfile(config, null, _defaultButtons);

        foreach (var p in config.Profiles.Where(p => p.Enabled && p.Processes.Count > 0))
        {
            var merged = new Dictionary<ushort, ActionDefinition>(_defaultButtons);
            foreach (var (cid, action) in ParseButtons(p.Buttons)) merged[cid] = action;
            _profiles.Add((p, p.Processes.Select(ProcessPattern.ToRegex).ToArray(), merged));
        }
    }

    public ResolvedProfile Default => _default;

    public ResolvedProfile Resolve(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return _default;
        var name = ProcessPattern.Normalize(processName);
        foreach (var (profile, patterns, buttons) in _profiles)
            if (patterns.Any(r => r.IsMatch(name)))
                return new ResolvedProfile(_config, profile, buttons);
        return _default;
    }

    /// <summary>
    /// Tasten, die umgeleitet werden müssen: alle, die im Standard oder in einem Profil eine andere Belegung
    /// als "native" haben. In Profilen ohne eigene Belegung wird die Originalfunktion nachgebildet.
    /// </summary>
    public IReadOnlySet<ushort> ControlsToDivert()
    {
        var set = new HashSet<ushort>();
        foreach (var (cid, action) in _defaultButtons)
            if (action is not NativeAction) set.Add(cid);
        foreach (var (_, _, buttons) in _profiles)
            foreach (var (cid, action) in buttons)
                if (action is not NativeAction) set.Add(cid);
        return set;
    }

    /// <summary>Tasten, die einen Ring öffnen (für Raw-XY).</summary>
    public IReadOnlySet<ushort> RingControls()
    {
        var set = new HashSet<ushort>();
        foreach (var (cid, action) in _defaultButtons)
            if (action is OpenRingAction) set.Add(cid);
        foreach (var (_, _, buttons) in _profiles)
            foreach (var (cid, action) in buttons)
                if (action is OpenRingAction) set.Add(cid);
        return set;
    }

    public static IReadOnlyDictionary<ushort, ActionDefinition> ParseButtons(IReadOnlyDictionary<string, ActionDefinition> buttons)
    {
        var result = new Dictionary<ushort, ActionDefinition>();
        foreach (var (key, action) in buttons)
            if (ControlIds.TryParse(key, out var cid) && action is not null)
                result[cid] = action;
        return result;
    }
}

public static class ProcessPattern
{
    /// <summary>Nur Dateiname, klein, ohne ".exe".</summary>
    public static string Normalize(string processName)
    {
        var name = Path.GetFileName(processName.Trim()).ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
    }

    public static Regex ToRegex(string pattern)
    {
        var p = Normalize(pattern);
        var regex = "^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

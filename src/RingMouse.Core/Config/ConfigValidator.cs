using RingMouse.Core.Input;
using RingMouse.HidPlusPlus.Features;
using static RingMouse.Core.Localization.Lang;

namespace RingMouse.Core.Config;

public enum IssueSeverity
{
    Warning,
    Error,
}

public sealed record ConfigIssue(IssueSeverity Severity, string Path, string Message)
{
    public override string ToString() => $"{(Severity == IssueSeverity.Error ? L("Error", "Fehler") : L("Note", "Hinweis"))} {Path}: {Message}";
}

/// <summary>Semantische Prüfung nach dem Einlesen (Verweise, Tastenkürzel, Wertebereiche).</summary>
public static class ConfigValidator
{
    public static IReadOnlyList<ConfigIssue> Validate(RingMouseConfig c)
    {
        var issues = new List<ConfigIssue>();
        void Error(string path, string msg) => issues.Add(new ConfigIssue(IssueSeverity.Error, path, msg));
        void Warn(string path, string msg) => issues.Add(new ConfigIssue(IssueSeverity.Warning, path, msg));

        // Ring-Einstellungen
        if (c.Ring.Radius is < 80 or > 400) Error("ring.radius", L("must be between 80 and 400", "muss zwischen 80 und 400 liegen"));
        if (c.Ring.Deadzone < 5 || c.Ring.Deadzone > c.Ring.Radius * 0.6) Error("ring.deadzone", L("must be between 5 and 60% of the radius", "muss zwischen 5 und 60 % des Radius liegen"));
        if (c.Ring.TapThresholdMs is < 50 or > 2000) Error("ring.tapThresholdMs", L("must be between 50 and 2000", "muss zwischen 50 und 2000 liegen"));
        if (c.Ring.RawXYScale is <= 0 or > 10) Error("ring.rawXYScale", L("must be between 0 and 10", "muss zwischen 0 und 10 liegen"));
        if (c.Ring.AutoCloseSeconds is < 0 or > 600) Error("ring.autoCloseSeconds", L("must be between 0 and 600", "muss zwischen 0 und 600 liegen"));
        if (c.Ring.Opacity is < 30 or > 100) Error("ring.opacity", L("must be between 30 and 100", "muss zwischen 30 und 100 liegen"));
        if (c.Ring.TextScale is < 50 or > 200) Error("ring.textScale", L("must be between 50 and 200", "muss zwischen 50 und 200 liegen"));
        if (c.Ring.PointerSize is < 2 or > 40) Error("ring.pointerSize", L("must be between 2 and 40", "muss zwischen 2 und 40 liegen"));
        foreach (var (value, path) in new[] { (c.Ring.RingColor, "ring.ringColor"), (c.Ring.AccentColor, "ring.accentColor"), (c.Ring.PointerColor, "ring.pointerColor") })
        {
            if (!string.IsNullOrWhiteSpace(value) && !ColorValue.TryParse(value, out _))
                Warn(path, L($"\"{value}\" is not a color (#RRGGBB) – the default applies", $"\"{value}\" ist keine Farbe (#RRGGBB) – es gilt der Standard"));
        }

        // Tasten
        ValidateButtons(c, c.Buttons, "buttons", Error, Warn);

        // Ringe
        foreach (var (name, ring) in c.Rings)
        {
            var path = $"rings.{name}";
            if (ring.Segments.Count is < 2 or > 8) Error($"{path}.segments", L($"needs 2–8 segments (has {ring.Segments.Count})", $"braucht 2–8 Segmente (hat {ring.Segments.Count})"));
            for (var i = 0; i < ring.Segments.Count; i++)
            {
                var seg = ring.Segments[i];
                if (seg is null) continue;
                var sp = $"{path}.segments[{i}]";
                if (string.IsNullOrWhiteSpace(seg.Label)) Warn($"{sp}.label", L("no label", "ohne Beschriftung"));
                if (seg.Action is null) continue;
                if (seg.Action is OpenRingAction) Error($"{sp}.action", L("\"ring\" is only allowed as a button assignment – use \"submenu\" in a ring", "\"ring\" ist nur als Tastenbelegung erlaubt – im Ring \"submenu\" verwenden"));
                if (seg.Action is NativeAction) Error($"{sp}.action", L("\"native\" only makes sense as a button assignment", "\"native\" ist nur als Tastenbelegung sinnvoll"));
                ValidateAction(c, seg.Action, $"{sp}.action", Error, Warn, inRing: true);
            }
        }

        // Profile
        for (var i = 0; i < c.Profiles.Count; i++)
        {
            var p = c.Profiles[i];
            var path = $"profiles[{i}]";
            if (string.IsNullOrWhiteSpace(p.Name)) Warn($"{path}.name", L("no name", "ohne Namen"));
            if (p.Processes.Count == 0) Warn($"{path}.processes", L("no processes – the profile never applies", "keine Prozesse – Profil greift nie"));
            ValidateButtons(c, p.Buttons, $"{path}.buttons", Error, Warn);
            foreach (var (from, to) in p.Rings)
            {
                if (!c.Rings.ContainsKey(to)) Error($"{path}.rings.{from}", L($"ring \"{to}\" does not exist", $"Ring \"{to}\" existiert nicht"));
            }
        }

        // Geräte
        foreach (var (key, dev) in c.Devices)
        {
            if (dev.Dpi is { } dpi && dpi is < 50 or > 32000) Error($"devices.{key}.dpi", L("implausible DPI value", "unplausibler DPI-Wert"));
        }

        // Akku
        foreach (var t in c.Battery.Thresholds)
            if (t is < 1 or > 99) Error("battery.thresholds", L($"threshold {t} must be between 1 and 99", $"Schwelle {t} muss zwischen 1 und 99 liegen"));
        if (c.Battery.PollMinutes is < 1 or > 1440) Error("battery.pollMinutes", L("must be between 1 and 1440", "muss zwischen 1 und 1440 liegen"));
        if (c.Battery.VoltageCurve is { Count: < 2 }) Error("battery.voltageCurve", L("needs at least two points", "braucht mindestens zwei Stützstellen"));

        // Updates
        if (c.General.UpdateIdleMinutes is < 1 or > 240) Error("general.updateIdleMinutes", L("must be between 1 and 240", "muss zwischen 1 und 240 liegen"));

        if (!c.Buttons.Values.Any(a => a is OpenRingAction) && !c.Profiles.Any(p => p.Buttons.Values.Any(a => a is OpenRingAction)))
            Warn("buttons", L("no button opens a ring (Settings → Buttons → “Press button …”)", "keine Taste öffnet einen Ring (Einstellungen → Tasten → „Taste drücken …“)"));

        return issues;
    }

    private static void ValidateButtons(RingMouseConfig c, Dictionary<string, ActionDefinition> buttons, string path,
        Action<string, string> error, Action<string, string> warn)
    {
        foreach (var (key, action) in buttons)
        {
            var bp = $"{path}.{key}";
            if (!ControlIds.TryParse(key, out var cid))
            {
                error(bp, L("is not a valid CID (example: \"0x00FD\")", "ist keine gültige CID (Beispiel: \"0x00FD\")"));
                continue;
            }
            if (cid is 0x0050) warn(bp, L("the left mouse button usually cannot be diverted", "die linke Maustaste lässt sich in der Regel nicht umleiten"));
            if (action is SubmenuAction) error(bp, L("\"submenu\" is only allowed in a ring – use \"ring\" for a button", "\"submenu\" ist nur im Ring erlaubt – als Taste \"ring\" verwenden"));
            ValidateAction(c, action, bp, error, warn, inRing: false);
        }
    }

    private static void ValidateAction(RingMouseConfig c, ActionDefinition action, string path,
        Action<string, string> error, Action<string, string> warn, bool inRing)
    {
        switch (action)
        {
            case OpenRingAction r when !c.Rings.ContainsKey(r.Ring):
                error($"{path}.ring", L($"ring \"{r.Ring}\" does not exist", $"Ring \"{r.Ring}\" existiert nicht"));
                break;
            case SubmenuAction s when !c.Rings.ContainsKey(s.Ring):
                error($"{path}.ring", L($"ring \"{s.Ring}\" does not exist", $"Ring \"{s.Ring}\" existiert nicht"));
                break;
            case KeysAction k when !KeyChordParser.TryParse(k.Keys, out _, out var err):
                error($"{path}.keys", err ?? L("invalid", "ungültig"));
                break;
            case AppKeysAction a:
                if (string.IsNullOrWhiteSpace(a.Process)) error($"{path}.process", L("missing", "fehlt"));
                if (!KeyChordParser.TryParse(a.Keys, out _, out var appErr)) error($"{path}.keys", appErr ?? L("invalid", "ungültig"));
                break;
            case LaunchAction l when string.IsNullOrWhiteSpace(l.Target):
                error($"{path}.target", L("missing", "fehlt"));
                break;
            case PowerShellAction ps when string.IsNullOrWhiteSpace(ps.Script) && string.IsNullOrWhiteSpace(ps.Command):
                error(path, L("needs \"script\" or \"command\"", "braucht \"script\" oder \"command\""));
                break;
            case SnippetAction sn when string.IsNullOrEmpty(sn.Text):
                warn($"{path}.text", L("empty", "leer"));
                break;
            case DpiAction d:
                if (d.Values.Count == 0) error($"{path}.values", L("at least one DPI value required", "mindestens ein DPI-Wert"));
                if (d.Values.Any(v => v is < 50 or > 32000)) error($"{path}.values", L("implausible DPI value", "unplausibler DPI-Wert"));
                break;
            case DelayAction dl when dl.Ms is < 0 or > 60000:
                error($"{path}.ms", "0–60000 ms");
                break;
            case SequenceAction seq:
                if (seq.Steps.Count == 0) warn($"{path}.steps", L("empty", "leer"));
                for (var i = 0; i < seq.Steps.Count; i++)
                {
                    var step = seq.Steps[i];
                    if (step is OpenRingAction or SubmenuAction or NativeAction or SequenceAction)
                        error($"{path}.steps[{i}]", L($"\"{step.TypeName}\" is not allowed in a sequence", $"\"{step.TypeName}\" ist in einer Sequenz nicht erlaubt"));
                    else
                        ValidateAction(c, step, $"{path}.steps[{i}]", error, warn, inRing);
                }
                break;
        }
    }
}

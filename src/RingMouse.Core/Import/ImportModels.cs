using RingMouse.Core.Config;
using RingMouse.HidPlusPlus.Features;
using static RingMouse.Core.Localization.Lang;

namespace RingMouse.Core.Import;

/// <summary>Teile einer Config, die ein Import übernehmen kann (Auswahl im Import-Fenster).</summary>
[Flags]
public enum ImportParts
{
    None = 0,
    Rings = 1,
    Buttons = 2,
    Profiles = 4,
    Devices = 8,
    /// <summary>Allgemein, Ring-Verhalten/-Aussehen, Akku, Debug – nur aus RingMouse-Dateien.</summary>
    Settings = 16,
}

public enum ImportNoteKind
{
    Imported,
    Skipped,
}

public sealed record ImportNote(ImportNoteKind Kind, string Text);

/// <summary>Ergebnis eines Imports: Teil-Config plus Bericht, was übernommen wurde und was nicht.</summary>
public sealed class ImportResult(string source)
{
    public string Source { get; } = source;
    public RingMouseConfig Config { get; init; } = new();
    public List<ImportNote> Notes { get; } = [];

    /// <summary>Die Quelle bringt allgemeine Einstellungen mit (nur ganze RingMouse-Configs).</summary>
    public bool HasSettings { get; init; }

    public ImportParts Available =>
        (Config.Rings.Count > 0 ? ImportParts.Rings : 0) |
        (Config.Buttons.Count > 0 ? ImportParts.Buttons : 0) |
        (Config.Profiles.Count > 0 ? ImportParts.Profiles : 0) |
        (Config.Devices.Count > 0 ? ImportParts.Devices : 0) |
        (HasSettings ? ImportParts.Settings : 0);

    public void Imported(string text) => Notes.Add(new ImportNote(ImportNoteKind.Imported, text));
    public void Skipped(string text) => Notes.Add(new ImportNote(ImportNoteKind.Skipped, text));
}

/// <summary>Führt importierte Teile in eine bestehende Config zusammen.</summary>
public static class ConfigMerge
{
    /// <summary>
    /// Übernimmt die gewählten Teile aus <paramref name="source"/> in eine Kopie von <paramref name="target"/>:
    /// gleichnamige Ringe, Profile und Geräte-Einträge sowie dieselben Tasten werden ersetzt, alles andere bleibt.
    /// Allgemeine Einstellungen werden als Ganzes ersetzt.
    /// </summary>
    public static RingMouseConfig Apply(RingMouseConfig target, RingMouseConfig source, ImportParts parts)
    {
        var result = ConfigSerializer.Clone(target);
        var src = ConfigSerializer.Clone(source);

        if (parts.HasFlag(ImportParts.Rings))
            foreach (var (name, ring) in src.Rings) result.Rings[name] = ring;

        if (parts.HasFlag(ImportParts.Buttons))
            foreach (var (key, action) in src.Buttons)
            {
                // dieselbe Taste in anderer Schreibweise ("0x00FD", "c253") nicht doppelt belegen
                if (ControlIds.TryParse(key, out var cid))
                    foreach (var existing in result.Buttons.Keys.Where(k => ControlIds.TryParse(k, out var c) && c == cid).ToList())
                        result.Buttons.Remove(existing);
                result.Buttons[key] = action;
            }

        if (parts.HasFlag(ImportParts.Profiles))
            foreach (var profile in src.Profiles)
            {
                result.Profiles.RemoveAll(p => p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
                result.Profiles.Add(profile);
            }

        if (parts.HasFlag(ImportParts.Devices))
            foreach (var (key, settings) in src.Devices) result.Devices[key] = settings;

        if (parts.HasFlag(ImportParts.Settings))
        {
            result.General = src.General;
            result.Ring = src.Ring;
            result.Battery = src.Battery;
            result.Debug = src.Debug;
        }
        return ConfigSerializer.Normalize(result);
    }
}

/// <summary>Import einer RingMouse-Config-Datei (Export eines anderen Rechners oder eine Sicherung).</summary>
public static class RingMouseFileImporter
{
    public static ImportResult Load(string json, string sourceName)
    {
        var parsed = ConfigStore.Parse(json);
        if (parsed.Config is null)
            throw new InvalidDataException(parsed.ErrorMessage ?? L("Not a RingMouse configuration.", "Keine RingMouse-Konfiguration."));

        var config = parsed.Config;
        var result = new ImportResult(sourceName) { Config = config, HasSettings = true };
        result.Imported(L($"{config.Rings.Count} ring(s), {config.Buttons.Count} button assignment(s), {config.Profiles.Count} profile(s), " +
                          $"{config.Devices.Count} device setting(s)",
            $"{config.Rings.Count} Ring(e), {config.Buttons.Count} Tastenbelegung(en), {config.Profiles.Count} Profil(e), " +
            $"{config.Devices.Count} Geräte-Einstellung(en)"));
        foreach (var issue in parsed.Issues.Where(i => i.Severity == IssueSeverity.Error))
            result.Skipped(issue.ToString());
        return result;
    }
}

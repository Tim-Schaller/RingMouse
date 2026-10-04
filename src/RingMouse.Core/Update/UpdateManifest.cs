using System.Text.RegularExpressions;

namespace RingMouse.Core.Update;

/// <summary>Ungültiges/nicht erreichbares Manifest, falsche Signatur oder Prüfsumme.</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Signierter Inhalt eines <c>latest.json</c>: beschreibt das neueste Release. Entspricht dem "payload" aus
/// Zeitspurs Update-Manifest – nur dieser Teil wird signiert.
/// </summary>
public sealed partial record UpdateManifest(string Version, string File, string Sha256, long Size, string Notes, string? Url)
{
    /// <summary>Obergrenzen – Schutz gegen aufgeblähte/bösartige Manifeste und Downloads.</summary>
    public const long MaxSetupBytes = 500L * 1024 * 1024;
    public const int MaxNotesChars = 4000;

    [GeneratedRegex(@"\d{1,5}(\.\d{1,5}){1,3}")] private static partial Regex VersionRegex();
    [GeneratedRegex(@"[A-Za-z0-9][A-Za-z0-9._-]{0,120}\.exe")] private static partial Regex FileRegex();
    [GeneratedRegex("[0-9a-f]{64}")] private static partial Regex Sha256Regex();

    /// <summary>Prüft alle Felder; wirft <see cref="UpdateException"/> bei ungültigen Werten.</summary>
    public void Validate()
    {
        if (!IsValidVersion(Version)) throw new UpdateException($"Invalid version in manifest: {Version}");
        if (string.IsNullOrEmpty(File) || !FileRegex().IsMatch(File) || !FullMatch(FileRegex(), File))
            throw new UpdateException($"Invalid file name in manifest: {File}");
        if (string.IsNullOrEmpty(Sha256) || !FullMatch(Sha256Regex(), Sha256))
            throw new UpdateException("Invalid checksum in manifest");
        if (Size <= 0 || Size > MaxSetupBytes) throw new UpdateException($"Invalid size in manifest: {Size}");
        if (Notes.Length > MaxNotesChars) throw new UpdateException("Change notes in manifest too long");
        if (Url is not null && !Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("Download URL in manifest must be https");
    }

    public static bool IsValidVersion(string? version) => version is not null && FullMatch(VersionRegex(), version);

    private static bool FullMatch(Regex regex, string value)
    {
        var m = regex.Match(value);
        return m.Success && m.Index == 0 && m.Length == value.Length;
    }

    /// <summary>true, wenn <paramref name="candidate"/> eine neuere Version als <paramref name="current"/> ist.</summary>
    public static bool IsNewer(string candidate, string current)
    {
        var a = ParseVersion(candidate);
        var b = ParseVersion(current);
        var n = Math.Max(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            var ai = i < a.Length ? a[i] : 0;
            var bi = i < b.Length ? b[i] : 0;
            if (ai != bi) return ai > bi;
        }
        return false;
    }

    public static int[] ParseVersion(string text)
    {
        if (!IsValidVersion(text)) throw new UpdateException($"Invalid version number: {text}");
        return text.Split('.').Select(int.Parse).ToArray();
    }
}

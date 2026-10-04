using System.Text.Json;

namespace RingMouse.Core.Update;

/// <summary>Was gerade installiert wird (überdauert den Neustart).</summary>
public sealed record PendingInfo(string From, string To, long Ts, bool Show);

/// <summary>Ergebnis eines Update-Versuchs beim nächsten Start.</summary>
public sealed record PendingResult(string Status, string Version, bool Show); // Status: "ok" | "failed"

/// <summary>
/// Zustand des Update-Vorgangs in Dateien im updates-Ordner: pending.json (gerade installiert, für die Prüfung
/// nach dem Neustart) und failed.json (eine gescheiterte Version eine Weile nicht erneut automatisch versuchen).
/// </summary>
public sealed class UpdateStore(string folder)
{
    private static readonly TimeSpan FailedCooldown = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Folder { get; } = folder;
    private string PendingPath => Path.Combine(Folder, "pending.json");
    private string FailedPath => Path.Combine(Folder, "failed.json");

    public void WritePending(string from, string to, bool show) =>
        Write(PendingPath, new PendingInfo(from, to, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), show));

    public void ClearPending() => TryDelete(PendingPath);

    /// <summary>Das gerade installierte Update, ohne es zu verbrauchen (für ein Startfenster danach).</summary>
    public PendingInfo? ReadPending() => Read<PendingInfo>(PendingPath) is { To.Length: > 0 } p ? p : null;

    /// <summary>
    /// Nach einem Update-Versuch beim nächsten Start: ("ok", aktuelle Version, Fenster zeigen?) wenn die Zielversion
    /// angekommen ist, sonst ("failed", Zielversion, …). null, wenn gar kein Versuch anstand.
    /// </summary>
    public PendingResult? ConsumePending(string current)
    {
        var info = Read<PendingInfo>(PendingPath);
        TryDelete(PendingPath);
        if (info is null || string.IsNullOrEmpty(info.To)) return null;
        bool arrived;
        try { arrived = !UpdateManifest.IsNewer(info.To, current); }
        catch (UpdateException) { return null; }
        if (arrived)
        {
            RemoveSetups(keep: null);
            TryDelete(FailedPath);
            return new PendingResult("ok", current, info.Show);
        }
        MarkFailed(info.To);
        return new PendingResult("failed", info.To, info.Show);
    }

    public void MarkFailed(string version) =>
        Write(FailedPath, new FailedInfo(version, DateTimeOffset.UtcNow.Add(FailedCooldown).ToUnixTimeSeconds()));

    public bool InCooldown(string version)
    {
        var info = Read<FailedInfo>(FailedPath);
        return info is not null && info.Version == version && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < info.Until;
    }

    /// <summary>Löscht geladene Setup-Dateien (alle außer <paramref name="keep"/>).</summary>
    public void RemoveSetups(string? keep)
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (var file in Directory.EnumerateFiles(Folder, "*.exe*"))
                if (keep is null || !string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
                    TryDelete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private sealed record FailedInfo(string Version, long Until);

    private static void Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}

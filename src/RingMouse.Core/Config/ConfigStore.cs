using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RingMouse.Core.Config;

public sealed record ConfigLoadResult(RingMouseConfig? Config, IReadOnlyList<ConfigIssue> Issues, string? ErrorMessage)
{
    public bool Success => Config is not null && Issues.All(i => i.Severity != IssueSeverity.Error);
}

/// <summary>
/// Lädt/speichert config.json, überwacht die Datei (Hot-Reload mit Entprellung) und hält bei Fehlern
/// die zuletzt gültige Config aktiv.
/// </summary>
public sealed class ConfigStore : IDisposable
{
    private readonly ILogger? _logger;
    private readonly object _lock = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private string? _lastWrittenHash;

    public ConfigStore(string path, ILogger? logger = null)
    {
        FilePath = path;
        _logger = logger;
        Current = DefaultConfig.Create();
    }

    public string FilePath { get; }
    public RingMouseConfig Current { get; private set; }
    public IReadOnlyList<ConfigIssue> CurrentIssues { get; private set; } = [];

    /// <summary>Neue gültige Config geladen (Hot-Reload oder Speichern).</summary>
    public event Action<RingMouseConfig>? Changed;

    /// <summary>Datei fehlerhaft – die bisherige Config bleibt aktiv.</summary>
    public event Action<ConfigLoadResult>? LoadFailed;

    /// <summary>Lädt die Datei; legt sie mit der Default-Config an, wenn sie fehlt.</summary>
    public ConfigLoadResult LoadOrCreate()
    {
        if (!File.Exists(FilePath))
        {
            _logger?.LogInformation("Keine Config gefunden – lege Standard-Config an: {Path}", FilePath);
            Save(DefaultConfig.Create());
        }
        var result = LoadFromDisk();
        if (result.Success)
        {
            Current = result.Config!;
            CurrentIssues = result.Issues;
        }
        return result;
    }

    public static ConfigLoadResult Parse(string json)
    {
        try
        {
            var config = ConfigSerializer.Deserialize(json);
            return new ConfigLoadResult(config, ConfigValidator.Validate(config), null);
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $" (Zeile {line + 1}, Spalte {(ex.BytePositionInLine ?? 0) + 1})" : "";
            return new ConfigLoadResult(null, [], $"JSON-Fehler{where}: {FirstLine(ex.Message)}");
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return new ConfigLoadResult(null, [], $"Config ungültig: {FirstLine(ex.Message)}");
        }
    }

    private ConfigLoadResult LoadFromDisk()
    {
        string json;
        try
        {
            json = ReadWithRetry();
        }
        catch (Exception ex)
        {
            return new ConfigLoadResult(null, [], $"Config konnte nicht gelesen werden: {ex.Message}");
        }
        return Parse(json);
    }

    /// <summary>Speichert atomar (temp + replace) und übernimmt die Config sofort.</summary>
    public void Save(RingMouseConfig config)
    {
        lock (_lock)
        {
            var json = ConfigSerializer.Serialize(config);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null, ignoreMetadataErrors: true);
            else File.Move(tmp, FilePath);
            _lastWrittenHash = Hash(json);
            Current = ConfigSerializer.Normalize(config);
            CurrentIssues = ConfigValidator.Validate(Current);
        }
        Changed?.Invoke(Current);
    }

    /// <summary>
    /// Kopiert die gespeicherte Datei nach "config.backup-JJJJMMTT-HHMMSS.json" daneben (z.B. vor dem Zurücksetzen).
    /// Liefert den Pfad der Sicherung oder null, wenn es noch keine Datei gibt.
    /// </summary>
    public string? CreateBackup()
    {
        lock (_lock)
        {
            if (!File.Exists(FilePath)) return null;
            var backup = Path.Combine(Path.GetDirectoryName(FilePath)!,
                $"{Path.GetFileNameWithoutExtension(FilePath)}.backup-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(FilePath, backup, overwrite: true);
            _logger?.LogInformation("Config gesichert: {Path}", backup);
            return backup;
        }
    }

    /// <summary>JSON-Schema neben die Config legen (nur schreiben, wenn geändert).</summary>
    public void WriteSchema(string schemaPath)
    {
        try
        {
            var schema = ConfigSerializer.ExportSchema();
            if (File.Exists(schemaPath) && File.ReadAllText(schemaPath) == schema) return;
            File.WriteAllText(schemaPath, schema, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "JSON-Schema konnte nicht geschrieben werden");
        }
    }

    public void StartWatching()
    {
        if (_watcher is not null) return;
        var dir = Path.GetDirectoryName(FilePath)!;
        _watcher = new FileSystemWatcher(dir, Path.GetFileName(FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.EnableRaisingEvents = true;
        _debounce = new Timer(_ => ReloadFromWatcher(), null, Timeout.Infinite, Timeout.Infinite);
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => _debounce?.Change(400, Timeout.Infinite);

    private void ReloadFromWatcher()
    {
        string json;
        try
        {
            json = ReadWithRetry();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Config-Änderung konnte nicht gelesen werden");
            return;
        }

        lock (_lock)
        {
            if (Hash(json) == _lastWrittenHash) return; // eigene Änderung
        }

        var result = Parse(json);
        if (!result.Success)
        {
            _logger?.LogWarning("Config-Änderung verworfen: {Error} {Issues}", result.ErrorMessage,
                string.Join("; ", result.Issues.Where(i => i.Severity == IssueSeverity.Error)));
            LoadFailed?.Invoke(result);
            return;
        }

        lock (_lock)
        {
            Current = result.Config!;
            CurrentIssues = result.Issues;
            _lastWrittenHash = Hash(json);
        }
        _logger?.LogInformation("Config neu geladen ({Warnings} Hinweise)", result.Issues.Count);
        Changed?.Invoke(Current);
    }

    private string ReadWithRetry()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 8)
            {
                Thread.Sleep(75); // Editor schreibt gerade
            }
        }
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string FirstLine(string text)
    {
        var nl = text.IndexOfAny(['\r', '\n']);
        return nl < 0 ? text : text[..nl];
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}

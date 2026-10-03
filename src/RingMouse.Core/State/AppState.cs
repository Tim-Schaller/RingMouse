using System.Text.Json;
using Microsoft.Extensions.Logging;
using RingMouse.Core.Battery;
using RingMouse.Core.Config;

namespace RingMouse.Core.State;

/// <summary>Letzter bekannter Akkustand eines Geräts (für die Anzeige, während es schläft).</summary>
public sealed class DeviceBatteryState
{
    public string DeviceName { get; set; } = "";
    public int? Percent { get; set; }
    public string State { get; set; } = "";
    public string Connection { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public BatteryAlertState Alerts { get; set; } = new();
}

/// <summary>Laufzeit-Zustand, der Neustarts überdauert (%APPDATA%\RingMouse\state.json).</summary>
public sealed class AppState
{
    public Dictionary<string, DeviceBatteryState> Batteries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool FirstRunCompleted { get; set; }

    /// <summary>Ersteinrichtung der Ring-Taste wurde angeboten (unabhängig vom Ergebnis nur einmal automatisch).</summary>
    public bool RingSetupOffered { get; set; }
}

public sealed class StateStore
{
    private readonly string _path;
    private readonly ILogger? _logger;
    private readonly object _lock = new();

    public StateStore(string path, ILogger? logger = null)
    {
        _path = path;
        _logger = logger;
        State = Load();
    }

    public AppState State { get; }

    private AppState Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppState();
            var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(_path), ConfigSerializer.Options) ?? new AppState();
            state.Batteries = new Dictionary<string, DeviceBatteryState>(state.Batteries ?? [], StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "state.json konnte nicht gelesen werden – starte mit leerem Zustand");
            return new AppState();
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(State, ConfigSerializer.Options));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "state.json konnte nicht geschrieben werden");
            }
        }
    }
}

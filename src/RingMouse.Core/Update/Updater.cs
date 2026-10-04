using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RingMouse.Core.Update;

/// <summary>Holt Manifest und Setup (HTTPS) – von der Plattform bereitgestellt.</summary>
public interface IUpdateTransport
{
    Task<byte[]> FetchManifestAsync(string url, int limit, CancellationToken ct);
    Task DownloadAsync(string url, string dest, string sha256, long size, Action<double>? progress, CancellationToken ct);
    bool FileMatches(string path, string sha256, long size);
}

/// <summary>Installiert ein geladenes Setup (Exe ersetzen und neu starten) – von der App bereitgestellt.</summary>
public interface IUpdateInstaller
{
    /// <summary>Kann sich diese Installation selbst ersetzen? false bei uiAccess oder geschütztem Ort → nur melden.</summary>
    bool CanInstall { get; }

    /// <summary>Ersetzt die Exe und startet RingMouse neu. Wirft bei Fehler.</summary>
    void Install(string setupPath, UpdateManifest manifest);
}

/// <summary>Momentaufnahme des Update-Zustands für die Oberfläche.</summary>
public sealed record UpdateSnapshot(string Status, string Current, string Version, string Notes, string Error,
    double Progress, bool Auto, bool CanInstall, string? ReleaseUrl);

/// <summary>Zeiten des Update-Loops (für Tests überschreibbar).</summary>
public sealed record UpdateTiming(TimeSpan InitialDelay, TimeSpan Interval, TimeSpan Tick, double? IdleOverrideSeconds = null)
{
    public static UpdateTiming Default { get; } =
        new(TimeSpan.FromMinutes(5), TimeSpan.FromHours(12), TimeSpan.FromSeconds(15));
}

/// <summary>
/// Prüft, lädt und installiert Updates auf einem eigenen Thread. Ablauf wie in Zeitspur: einige Minuten nach dem
/// Start und danach regelmäßig das signierte latest.json holen; ist eine neuere Version signiert und geprüft, das
/// Setup laden und – sofern automatische Updates an sind – installieren, sobald der PC eine Weile unbenutzt ist.
/// Status "idle | checking | current | available | downloading | ready | installing | error".
/// </summary>
public sealed class Updater
{
    public const string Repo = "Tim-Schaller/RingMouse";
    public const string DefaultManifestUrl = $"https://github.com/{Repo}/releases/latest/download/latest.json";

    private readonly IUpdateTransport _transport;
    private readonly IUpdateInstaller _installer;
    private readonly UpdateStore _store;
    private readonly Func<double> _idleSeconds;
    private readonly Action<string> _notify;
    private readonly Func<bool> _autoUpdate;
    private readonly Func<int> _idleMinutes;
    private readonly Func<bool> _windowVisible;
    private readonly string _manifestUrl;
    private readonly string _publicKey;
    private readonly string _current;
    private readonly UpdateTiming _timing;
    private readonly ILogger _log;

    private readonly object _lock = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);
    private Thread? _thread;

    private UpdateManifest? _manifest;
    private string? _setup;
    private volatile bool _checkRequested;
    private volatile bool _manualCheck;
    private volatile bool _installRequested;

    // unter _lock
    private string _status = "idle";
    private string _version = "";
    private string _notes = "";
    private string _error = "";
    private double _progress;

    public Updater(IUpdateTransport transport, IUpdateInstaller installer, UpdateStore store, string current,
        Func<bool> autoUpdate, Func<int> idleMinutes, Action<string> notify,
        Func<double> idleSeconds, Func<bool> windowVisible,
        string? manifestUrl = null, string? publicKey = null, UpdateTiming? timing = null, ILogger? logger = null)
    {
        _transport = transport;
        _installer = installer;
        _store = store;
        _current = current;
        _autoUpdate = autoUpdate;
        _idleMinutes = idleMinutes;
        _notify = notify;
        _idleSeconds = idleSeconds;
        _windowVisible = windowVisible;
        _manifestUrl = manifestUrl ?? Environment.GetEnvironmentVariable("RINGMOUSE_UPDATE_URL") ?? DefaultManifestUrl;
        _publicKey = publicKey ?? ManifestVerifier.PublicKey;
        _timing = timing ?? UpdateTiming.Default;
        _log = logger ?? NullLogger.Instance;
    }

    public void Start()
    {
        if (_thread is not null) return;
        _log.LogInformation("Updates: source {Url}, automatic {Auto}", _manifestUrl, _autoUpdate() ? "on" : "off");
        _thread = new Thread(Run) { IsBackground = true, Name = "RingMouse updater" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop.Cancel();
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
    }

    public void CheckNow(bool manual = true)
    {
        _checkRequested = true;
        _manualCheck = manual;
        _wake.Set();
    }

    /// <summary>Sofort installieren, ohne auf den Leerlauf zu warten ("Jetzt aktualisieren").</summary>
    public void InstallNow()
    {
        _installRequested = true;
        if (_manifest is null) _checkRequested = true;
        _wake.Set();
    }

    public string Status { get { lock (_lock) return _status; } }

    public UpdateSnapshot Snapshot()
    {
        lock (_lock)
            return new UpdateSnapshot(_status, _current, _version, _notes, _error, _progress,
                _autoUpdate(), _installer.CanInstall, ReleasePage(_version));
    }

    public static string? ReleasePage(string version) =>
        UpdateManifest.IsValidVersion(version) ? $"https://github.com/{Repo}/releases/tag/v{version}" : null;

    private double IdleNeededSeconds => _timing.IdleOverrideSeconds ?? Math.Max(1, _idleMinutes()) * 60.0;

    // ---- Ablauf -------------------------------------------------------------------------------------

    private void Run()
    {
        var nextCheck = Environment.TickCount64 + (long)_timing.InitialDelay.TotalMilliseconds;
        while (!_stop.IsCancellationRequested)
        {
            var due = Environment.TickCount64 >= nextCheck;
            if (due) nextCheck = Environment.TickCount64 + (long)_timing.Interval.TotalMilliseconds;
            try
            {
                Step(due);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Update: unexpected error");
            }
            _wake.Wait(_timing.Tick);
            _wake.Reset();
        }
    }

    /// <summary>Ein Durchgang: ggf. prüfen, laden, im Leerlauf installieren. Für Tests einzeln aufrufbar.</summary>
    public void Step(bool checkDue = false)
    {
        if (checkDue || _checkRequested)
        {
            var manual = _manualCheck;
            _checkRequested = false;
            _manualCheck = false;
            Check(manual);
        }
        if (_installRequested && Status is "available" or "error") FetchSetup();
        if (ShouldInstall()) Install();
    }

    public void Check(bool manual = false)
    {
        var before = Status;
        if (before is "downloading" or "installing") return;
        Set(status: "checking", error: "");
        UpdateManifest manifest;
        try
        {
            var raw = _transport.FetchManifestAsync(_manifestUrl, ManifestVerifier.MaxManifestBytes, _stop.Token)
                .GetAwaiter().GetResult();
            manifest = ManifestVerifier.Verify(raw, _publicKey);
        }
        catch (UpdateException e)
        {
            _log.LogWarning("Update check: {Error}", e.Message);
            var known = before is "available" or "ready";
            // Ein kurzer Netzaussetzer soll ein bereits gefundenes Update nicht vergessen lassen.
            Set(status: known ? before : "error", error: e.Message);
            if (!known) _installRequested = false;
            if (manual) _notify($"Update check failed: {e.Message}");
            return;
        }

        if (!UpdateManifest.IsNewer(manifest.Version, _current))
        {
            _manifest = null;
            _setup = null;
            Set(status: "current", version: "", notes: "");
            _log.LogInformation("Update check: {Version} is current", _current);
            if (manual) _notify($"RingMouse is up to date ({_current}).");
            _installRequested = false;
            return;
        }

        var alreadyReady = _manifest is not null && _manifest.Version == manifest.Version && before == "ready";
        _manifest = manifest;
        if (alreadyReady)
        {
            Set(status: "ready");
        }
        else
        {
            _setup = null;
            Set(status: "available", version: manifest.Version, notes: manifest.Notes, progress: 0);
            _log.LogInformation("Update available: {From} -> {To}", _current, manifest.Version);
        }
        Announce(manifest.Version, again: manual);
        if ((_autoUpdate() && _installer.CanInstall) || _installRequested) FetchSetup();
    }

    public void FetchSetup()
    {
        var manifest = _manifest;
        if (manifest is null || Status is not ("available" or "error")) return;
        var dest = Path.Combine(_store.Folder, manifest.File);
        if (!_transport.FileMatches(dest, manifest.Sha256, manifest.Size))
        {
            Set(status: "downloading", progress: 0, error: "");
            try
            {
                _transport.DownloadAsync(SetupUrl(manifest), dest, manifest.Sha256, manifest.Size,
                    frac => Set(progress: Math.Round(frac, 3)), _stop.Token).GetAwaiter().GetResult();
            }
            catch (UpdateException e)
            {
                _log.LogWarning("Update {Version}: {Error}", manifest.Version, e.Message);
                Set(status: "available", error: e.Message);
                if (_installRequested)
                {
                    _installRequested = false;
                    _notify($"Update could not be downloaded: {e.Message}");
                }
                return;
            }
        }
        _setup = dest;
        _store.RemoveSetups(keep: dest);
        Set(status: "ready", progress: 1, error: "");
        _log.LogInformation("Update {Version} downloaded and verified", manifest.Version);
    }

    private bool ShouldInstall()
    {
        if (Status != "ready" || _setup is null || _manifest is null) return false;
        if (_installRequested) return _installer.CanInstall;
        if (!_autoUpdate() || !_installer.CanInstall || _store.InCooldown(_manifest.Version)) return false;
        return _idleSeconds() >= IdleNeededSeconds;
    }

    public void Install()
    {
        var (manifest, setup) = (_manifest, _setup);
        var manual = _installRequested;
        _installRequested = false;
        if (manifest is null || setup is null || !_installer.CanInstall) return;
        if (!_transport.FileMatches(setup, manifest.Sha256, manifest.Size)) // seit dem Laden verändert?
        {
            _log.LogWarning("Update {Version}: setup changed since download – re-downloading", manifest.Version);
            _setup = null;
            Set(status: "available");
            FetchSetup();
            return;
        }
        var show = manual || _windowVisible();
        _store.WritePending(_current, manifest.Version, show);
        _log.LogInformation("Installing update {From} -> {To}", _current, manifest.Version);
        try
        {
            _installer.Install(setup, manifest); // ersetzt die Exe und löst den Neustart aus
        }
        catch (Exception e)
        {
            _store.ClearPending();
            _log.LogError(e, "Update setup could not be started");
            Set(status: "error", error: $"Update could not be started: {e.Message}");
            return;
        }
        Set(status: "installing");
    }

    private void Announce(string version, bool again = false)
    {
        if (!_announced.Add(version) && !again) return;
        if (_autoUpdate() && _installer.CanInstall)
            _notify($"RingMouse {version} is available and will be installed once the PC is idle.");
        else if (_installer.CanInstall)
            _notify($"RingMouse {version} is available – install it via Settings → “Update now”.");
        else
            _notify($"RingMouse {version} is available – please update manually (this installation can't update itself).");
    }

    private string SetupUrl(UpdateManifest manifest) =>
        manifest.Url ?? new Uri(new Uri(_manifestUrl), manifest.File).ToString();

    private void Set(string? status = null, string? version = null, string? notes = null, string? error = null, double? progress = null)
    {
        lock (_lock)
        {
            if (status is not null) _status = status;
            if (version is not null) _version = version;
            if (notes is not null) _notes = notes;
            if (error is not null) _error = error;
            if (progress is not null) _progress = progress.Value;
        }
    }
}

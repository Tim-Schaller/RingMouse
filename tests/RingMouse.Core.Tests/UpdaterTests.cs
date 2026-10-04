using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RingMouse.Core.Update;
using Xunit;

namespace RingMouse.Core.Tests;

/// <summary>Orchestrierung des Updaters: prüfen, laden, im Leerlauf installieren, Sonderfälle (uiAccess, Fehler).</summary>
public sealed class UpdaterTests : IDisposable
{
    private const string Current = "1.1.2";
    private const string Newer = "1.2.0";
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const long Size = 1000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ringmouse-updater-test-" + Guid.NewGuid().ToString("N"));
    private readonly RSA _key = RSA.Create(3072);

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* egal */ }
    }

    private (byte[] Raw, string Pub) SignedManifest(string version)
    {
        var payload = $$"""{"version":"{{version}}","file":"RingMouse.exe","sha256":"{{Sha}}","size":{{Size}},"notes":"Neu"}""";
        using var doc = JsonDocument.Parse(payload);
        var sig = ManifestVerifier.Sign(doc.RootElement, _key);
        return (Encoding.UTF8.GetBytes($$"""{"payload":{{payload}},"signature":"{{sig}}"}"""),
            Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()));
    }

    private sealed class FakeTransport(byte[] manifest) : IUpdateTransport
    {
        public byte[] Manifest = manifest;
        public int Downloads;
        public bool FailDownload;
        public Exception? FetchError;

        public Task<byte[]> FetchManifestAsync(string url, int limit, CancellationToken ct) =>
            FetchError is not null ? Task.FromException<byte[]>(FetchError) : Task.FromResult(Manifest);

        public Task DownloadAsync(string url, string dest, string sha256, long size, Action<double>? progress, CancellationToken ct)
        {
            Downloads++;
            if (FailDownload) throw new UpdateException("download failed");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, new byte[size]);
            progress?.Invoke(1.0);
            return Task.CompletedTask;
        }

        public bool FileMatches(string path, string sha256, long size) =>
            File.Exists(path) && new FileInfo(path).Length == size;
    }

    private sealed class FakeInstaller : IUpdateInstaller
    {
        public bool CanInstall { get; set; } = true;
        public int Installs;
        public bool Throw;
        public void Install(string setupPath, UpdateManifest manifest)
        {
            Installs++;
            if (Throw) throw new IOException("boom");
        }
    }

    private Updater Build(FakeTransport transport, FakeInstaller installer, string pub,
        bool autoUpdate = true, double idleSeconds = 0, int idleMinutes = 5, List<string>? notes = null) =>
        new(transport, installer, new UpdateStore(_dir), Current,
            autoUpdate: () => autoUpdate, idleMinutes: () => idleMinutes,
            notify: m => notes?.Add(m), idleSeconds: () => idleSeconds, windowVisible: () => false,
            manifestUrl: "https://example.org/latest.json", publicKey: pub);

    [Fact]
    public void Check_CurrentVersion_ReportsCurrent()
    {
        var (raw, pub) = SignedManifest(Current);
        var transport = new FakeTransport(raw);
        var updater = Build(transport, new FakeInstaller(), pub);

        updater.Step(checkDue: true);

        Assert.Equal("current", updater.Status);
        Assert.Equal(0, transport.Downloads);
    }

    [Fact]
    public void Check_NewerVersion_AutoUpdate_DownloadsAndBecomesReady()
    {
        var (raw, pub) = SignedManifest(Newer);
        var transport = new FakeTransport(raw);
        var updater = Build(transport, new FakeInstaller(), pub, idleSeconds: 0); // noch nicht im Leerlauf

        updater.Step(checkDue: true);

        Assert.Equal("ready", updater.Status);
        Assert.Equal(1, transport.Downloads);
        Assert.Equal(Newer, updater.Snapshot().Version);
    }

    [Fact]
    public void Ready_WhenIdle_Installs_AndWritesPending()
    {
        var (raw, pub) = SignedManifest(Newer);
        var transport = new FakeTransport(raw);
        var installer = new FakeInstaller();
        var updater = Build(transport, installer, pub, idleSeconds: 10_000); // lange im Leerlauf

        updater.Step(checkDue: true); // lädt, ready, und installiert gleich (idle erreicht)

        Assert.Equal(1, installer.Installs);
        Assert.Equal("installing", updater.Status);
        Assert.True(File.Exists(Path.Combine(_dir, "pending.json")));
    }

    [Fact]
    public void Ready_WhenBusy_DoesNotInstall()
    {
        var (raw, pub) = SignedManifest(Newer);
        var installer = new FakeInstaller();
        var updater = Build(new FakeTransport(raw), installer, pub, idleSeconds: 5, idleMinutes: 5); // 5s << 300s

        updater.Step(checkDue: true);

        Assert.Equal(0, installer.Installs);
        Assert.Equal("ready", updater.Status);
    }

    [Fact]
    public void AutoUpdateOff_DoesNotDownload_UntilInstallNow()
    {
        var (raw, pub) = SignedManifest(Newer);
        var transport = new FakeTransport(raw);
        var installer = new FakeInstaller();
        var updater = Build(transport, installer, pub, autoUpdate: false, idleSeconds: 10_000);

        updater.Step(checkDue: true);
        Assert.Equal("available", updater.Status);
        Assert.Equal(0, transport.Downloads);

        updater.InstallNow();
        updater.Step();
        Assert.Equal(1, transport.Downloads);
        Assert.Equal(1, installer.Installs);
    }

    [Fact]
    public void CannotInstall_AnnouncesManual_AndDoesNotDownload()
    {
        var (raw, pub) = SignedManifest(Newer);
        var transport = new FakeTransport(raw);
        var notes = new List<string>();
        var updater = Build(transport, new FakeInstaller { CanInstall = false }, pub, idleSeconds: 10_000, notes: notes);

        updater.Step(checkDue: true);

        Assert.Equal("available", updater.Status);
        Assert.Equal(0, transport.Downloads);
        Assert.Contains(notes, m => m.Contains("manually", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DownloadFails_FallsBackToAvailable()
    {
        var (raw, pub) = SignedManifest(Newer);
        var transport = new FakeTransport(raw) { FailDownload = true };
        var installer = new FakeInstaller();
        var updater = Build(transport, installer, pub, idleSeconds: 10_000);

        updater.Step(checkDue: true);

        Assert.Equal("available", updater.Status);
        Assert.Equal(1, transport.Downloads);
        Assert.Equal(0, installer.Installs);
    }

    [Fact]
    public void InstallThrows_ReportsError_AndClearsPending()
    {
        var (raw, pub) = SignedManifest(Newer);
        var installer = new FakeInstaller { Throw = true };
        var updater = Build(new FakeTransport(raw), installer, pub, idleSeconds: 10_000);

        updater.Step(checkDue: true);

        Assert.Equal("error", updater.Status);
        Assert.False(File.Exists(Path.Combine(_dir, "pending.json")));
    }

    [Fact]
    public void BadSignature_ReportsError()
    {
        var (raw, _) = SignedManifest(Newer);
        using var otherKey = RSA.Create(3072);
        var updater = Build(new FakeTransport(raw), new FakeInstaller(), Convert.ToBase64String(otherKey.ExportSubjectPublicKeyInfo()));

        updater.Step(checkDue: true);

        Assert.Equal("error", updater.Status);
    }

    [Fact]
    public void NetworkError_ReportsError()
    {
        var (raw, pub) = SignedManifest(Newer);
        var transport = new FakeTransport(raw) { FetchError = new UpdateException("offline") };
        var updater = Build(transport, new FakeInstaller(), pub);

        updater.Step(checkDue: true);

        Assert.Equal("error", updater.Status);
    }

    [Fact]
    public void ConsumePending_AfterSuccessfulUpdate_ReportsOk()
    {
        var store = new UpdateStore(_dir);
        store.WritePending(from: "1.1.1", to: Current, show: true); // Ziel == aktuelle Version -> angekommen

        var result = store.ConsumePending(Current);

        Assert.NotNull(result);
        Assert.Equal("ok", result!.Status);
        Assert.False(File.Exists(Path.Combine(_dir, "pending.json")));
    }

    [Fact]
    public void ConsumePending_AfterFailedUpdate_ReportsFailed_AndSetsCooldown()
    {
        var store = new UpdateStore(_dir);
        store.WritePending(from: Current, to: Newer, show: false); // Ziel NICHT angekommen (noch alte Version)

        var result = store.ConsumePending(Current);

        Assert.Equal("failed", result!.Status);
        Assert.True(store.InCooldown(Newer));
    }
}

using System.Collections.Concurrent;
using RingMouse.Core.Config;
using Xunit;

namespace RingMouse.Core.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ringmouse-test-" + Guid.NewGuid().ToString("N"));

    public ConfigStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* egal */ }
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline && !condition()) await Task.Delay(25);
    }

    [Fact]
    public void LoadOrCreate_WritesDefaultConfig()
    {
        using var store = new ConfigStore(Path.Combine(_dir, "config.json"));
        var result = store.LoadOrCreate();
        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(store.FilePath));
        Assert.Equal(8, store.Current.Rings["main"].Segments.Count);
    }

    [Fact]
    public void CreateBackup_CopiesSavedFile_NextToIt()
    {
        using var store = new ConfigStore(Path.Combine(_dir, "config.json"));
        Assert.Null(store.CreateBackup()); // noch keine Datei

        store.Save(TestConfigs.Sample());
        var backup = store.CreateBackup();
        Assert.NotNull(backup);
        Assert.Equal(_dir, Path.GetDirectoryName(backup));
        Assert.Matches(@"^config\.backup-\d{8}-\d{6}\.json$", Path.GetFileName(backup));
        Assert.Equal(File.ReadAllText(store.FilePath), File.ReadAllText(backup));
    }

    [Fact]
    public async Task HotReload_AppliesValidChanges_RejectsBrokenJson_IgnoresOwnWrites()
    {
        using var store = new ConfigStore(Path.Combine(_dir, "config.json"));
        store.LoadOrCreate();
        var changed = new ConcurrentQueue<RingMouseConfig>();
        var failed = new ConcurrentQueue<ConfigLoadResult>();
        store.Changed += c => changed.Enqueue(c);
        store.LoadFailed += r => failed.Enqueue(r);
        store.StartWatching();

        // 1) externe gültige Änderung
        var json = File.ReadAllText(store.FilePath).Replace("\"radius\": 150", "\"radius\": 180");
        File.WriteAllText(store.FilePath, json);
        await Until(() => !changed.IsEmpty);
        Assert.Equal(180, store.Current.Ring.Radius);

        // 2) kaputtes JSON → verworfen, alte Config bleibt
        File.WriteAllText(store.FilePath, "{ \"ring\": { \"radius\": ");
        await Until(() => !failed.IsEmpty);
        Assert.NotNull(failed.First().ErrorMessage);
        Assert.Equal(180, store.Current.Ring.Radius);

        // 3) eigenes Speichern → genau ein Changed (direkt), kein zweites über den Watcher
        var before = changed.Count;
        var config = ConfigSerializer.Clone(store.Current);
        config.Ring.Radius = 200;
        store.Save(config);
        await Task.Delay(1200);
        Assert.Equal(before + 1, changed.Count);
        Assert.Equal(200, store.Current.Ring.Radius);
    }

    [Fact]
    public void InvalidSemantics_AreReportedButFileStays()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, """{ "buttons": { "0x00FD": { "type": "ring", "ring": "gibtsnicht" } } }""");
        using var store = new ConfigStore(path);
        var result = store.LoadOrCreate();
        Assert.False(result.Success);
        Assert.Contains(result.Issues, i => i.Path == "buttons.0x00FD.ring");
        Assert.Contains("gibtsnicht", File.ReadAllText(path)); // Datei wird nicht überschrieben
    }
}

using Microsoft.Win32;
using Stratus.Core.Models;
using Stratus.Core.Services;
using Xunit;

namespace Stratus.Tests;

/// <summary>
/// Concurrency of the backup store: two instances share one file, the way the app and the
/// CLI do. One process must never drop the other's saved original.
/// </summary>
public class BackupSafetyTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "stratus-backup-" + Guid.NewGuid().ToString("N") + ".json");

    private static BackupEntry Entry(string tweak, string key, string value) => new()
    {
        TweakId = tweak,
        ScopeType = RegistryService.ScopeType,
        ScopeId = "LocalMachine|path|" + key,
        Key = key,
        ValueKind = "4",
        ValueJson = value,
        Existed = true
    };

    [Fact]
    public void StaleStoreInstance_DoesNotDropAnotherInstanceEntry()
    {
        var first = new BackupStore(_path);
        var second = new BackupStore(_path);

        first.Append(Entry("tweak", "a", "1"));
        second.Append(Entry("tweak", "b", "2"));

        var reloaded = new BackupStore(_path);
        Assert.Equal(2, reloaded.All().Count);
        Assert.Equal("1", reloaded.GetLatest("tweak", RegistryService.ScopeType, "LocalMachine|path|a", "a")!.ValueJson);
        Assert.Equal("2", reloaded.GetLatest("tweak", RegistryService.ScopeType, "LocalMachine|path|b", "b")!.ValueJson);
    }

    [Fact]
    public void ParallelInstances_KeepEveryOriginal()
    {
        const int writes = 12;
        var stores = Enumerable.Range(0, 4).Select(_ => new BackupStore(_path)).ToList();

        Parallel.For(0, writes, i => stores[i % stores.Count].TryAddOriginal(Entry("tweak", "k" + i, i.ToString())));

        Assert.Equal(writes, new BackupStore(_path).All().Count);
    }

    [Fact]
    public void FailedSave_DoesNotPublishCandidate()
    {
        File.WriteAllText(_path, "[]");
        var store = new BackupStore(_path);
        using var held = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.ThrowsAny<Exception>(() => store.Append(Entry("tweak", "a", "1")));
        Assert.Empty(store.All());
        Assert.False(store.HasEntries("tweak"));
    }

    [Fact]
    public void UnreadableBackupFile_IsLeftInPlace()
    {
        File.WriteAllText(_path, "{ not a list");

        var store = new BackupStore(_path);

        Assert.True(File.Exists(_path));
        Assert.ThrowsAny<Exception>(() => store.Append(Entry("tweak", "a", "1")));
        Assert.Contains("not a list", File.ReadAllText(_path));
    }

    [Fact]
    public void LegacyBackupFile_StillLoads()
    {
        File.WriteAllText(_path, """
            [
              {
                "TweakId": "tweak",
                "ScopeType": "registry",
                "ScopeId": "LocalMachine|path|a",
                "Key": "a",
                "ValueKind": "4",
                "ValueJson": "1",
                "Existed": true,
                "SavedAtUtc": "2026-01-01T00:00:00Z"
              }
            ]
            """);

        var store = new BackupStore(_path);

        var entry = store.GetLatest("tweak", RegistryService.ScopeType, "LocalMachine|path|a", "a");
        Assert.NotNull(entry);
        Assert.Equal("1", entry!.ValueJson);
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { /* best effort */ }
        try { File.Delete(_path + ".corrupt"); } catch { /* best effort */ }
    }
}

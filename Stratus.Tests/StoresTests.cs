using Stratus.Core.Models;
using Stratus.Core.Services;
using Xunit;

namespace Stratus.Tests;

public sealed class TempFile : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "stratus-test-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        try { File.Delete(Path); } catch { /* best effort */ }
    }
}

public class JsonFileTests : IDisposable
{
    private readonly TempFile _file = new();

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var data = new List<string> { "one", "two" };
        JsonFile.Save(_file.Path, data);
        Assert.Equal(data, JsonFile.Load(_file.Path, "test file", () => new List<string>()));
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var loaded = JsonFile.Load(_file.Path, "test file", () => new List<string> { "empty" });
        Assert.Equal(new List<string> { "empty" }, loaded);
    }

    [Fact]
    public void Load_CorruptFile_QuarantinesAndReturnsEmpty()
    {
        File.WriteAllText(_file.Path, "{ not json");
        var loaded = JsonFile.Load(_file.Path, "test file", () => new List<string>());
        Assert.Empty(loaded);
        Assert.False(File.Exists(_file.Path));
        Assert.True(File.Exists(_file.Path + ".corrupt"));
        File.Delete(_file.Path + ".corrupt");
    }

    [Fact]
    public void Save_DoesNotLeaveTempFilesBehind()
    {
        JsonFile.Save(_file.Path, new List<string>());
        Assert.Empty(Directory.GetFiles(
            System.IO.Path.GetDirectoryName(_file.Path)!,
            System.IO.Path.GetFileName(_file.Path) + "*.tmp"));
    }

    public void Dispose() => _file.Dispose();
}

public class BackupStoreTests : IDisposable
{
    private readonly TempFile _file = new();

    private static BackupEntry Entry(string tweak, string key, string value, DateTime? at = null) => new()
    {
        TweakId = tweak,
        ScopeType = RegistryService.ScopeType,
        ScopeId = "hive|path|" + key,
        Key = key,
        ValueKind = "4",
        ValueJson = value,
        Existed = true,
        SavedAtUtc = at ?? DateTime.UtcNow
    };

    [Fact]
    public void Append_SameKey_ReplacesPreviousBackup()
    {
        var store = new BackupStore(_file.Path);
        store.Append(Entry("tweak", "StartType", "value1"));
        store.Append(Entry("tweak", "StartType", "value2"));

        var latest = store.LatestPerKey("tweak");
        var entry = Assert.Single(latest);
        Assert.Equal("value2", entry.ValueJson);
    }

    [Fact]
    public void RemoveOne_DropsOnlyThatKey()
    {
        var store = new BackupStore(_file.Path);
        store.Append(Entry("tweak", "a", "1"));
        store.Append(Entry("tweak", "b", "2"));

        store.RemoveOne("tweak", RegistryService.ScopeType, "hive|path|a", "a");

        var remaining = store.LatestPerKey("tweak");
        var entry = Assert.Single(remaining);
        Assert.Equal("b", entry.Key);
    }

    [Fact]
    public void RemoveOne_PersistsAcrossInstances()
    {
        new BackupStore(_file.Path).Append(Entry("tweak", "a", "1"));
        new BackupStore(_file.Path).RemoveOne("tweak", RegistryService.ScopeType, "hive|path|a", "a");
        Assert.False(new BackupStore(_file.Path).HasEntries("tweak"));
    }

    public void Dispose() => _file.Dispose();
}

public class ScoreHistoryStoreTests : IDisposable
{
    private readonly TempFile _file = new();

    [Fact]
    public void AppendIfChanged_SkipsDuplicateScore()
    {
        var store = new ScoreHistoryStore(_file.Path);
        Assert.True(store.AppendIfChanged(50));
        Assert.False(store.AppendIfChanged(50));
        Assert.True(store.AppendIfChanged(60));
        Assert.Equal(2, store.All().Count);
    }

    [Fact]
    public void AppendIfChanged_SurvivesReload()
    {
        new ScoreHistoryStore(_file.Path).AppendIfChanged(42);
        var reloaded = new ScoreHistoryStore(_file.Path);
        var point = Assert.Single(reloaded.All());
        Assert.Equal(42, point.Score);
    }

    public void Dispose() => _file.Dispose();
}

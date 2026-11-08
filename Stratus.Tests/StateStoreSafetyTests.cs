using Stratus.Core.Models;
using Stratus.Core.Services;
using Xunit;

namespace Stratus.Tests;

/// <summary>
/// The small state stores (settings, history, scores) share one file the way the app and the
/// CLI do: a second instance must build on the first one's update, a failed save must leave no
/// in-memory entry the file never received, and only unreadable content - never a locked file -
/// may be quarantined.
/// </summary>
public class StateStoreSafetyTests : IDisposable
{
    private readonly TempFile _file = new();

    // -- Cross-instance updates (app and CLI side by side) --

    [Fact]
    public void SettingsStore_SecondInstance_BuildsOnTheFirstUpdate()
    {
        new SettingsStore(_file.Path).Set("theme", "dark");
        new SettingsStore(_file.Path).Set("tray", "on");

        var reloaded = new SettingsStore(_file.Path);
        Assert.Equal("dark", reloaded.Get("theme"));
        Assert.Equal("on", reloaded.Get("tray"));
    }

    [Fact]
    public void HistoryStore_SecondInstance_KeepsTheFirstEntry()
    {
        new HistoryStore(_file.Path).Append(new HistoryEntry { TweakId = "a", TweakName = "A", Action = "Applied", Success = true });
        new HistoryStore(_file.Path).Append(new HistoryEntry { TweakId = "b", TweakName = "B", Action = "Applied", Success = true });

        var all = new HistoryStore(_file.Path).All();
        Assert.Equal(2, all.Count);
        Assert.Equal(["b", "a"], all.Select(e => e.TweakId));
    }

    [Fact]
    public void ScoreHistoryStore_SecondInstance_KeepsTheFirstPoints()
    {
        new ScoreHistoryStore(_file.Path).AppendIfChanged(40);
        new ScoreHistoryStore(_file.Path).AppendIfChanged(60);

        var reloaded = new ScoreHistoryStore(_file.Path);
        Assert.Equal([40, 60], reloaded.All().Select(p => p.Score));
    }

    // -- A failed save must not leave a memory-only entry --

    [Fact]
    public void HistoryStore_FailedAppend_LeavesNoMemoryOnlyEntry()
    {
        File.WriteAllText(_file.Path, "[]");
        var store = new HistoryStore(_file.Path);
        using var held = new FileStream(_file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);

        store.Append(new HistoryEntry { TweakId = "lost", TweakName = "L", Action = "Applied", Success = true });

        Assert.DoesNotContain(store.All(), e => e.TweakId == "lost");
    }

    [Fact]
    public void ScoreHistoryStore_FailedAppend_ReportsFalseAndKeepsNoFiction()
    {
        File.WriteAllText(_file.Path, "[]");
        var store = new ScoreHistoryStore(_file.Path);
        using var held = new FileStream(_file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.False(store.AppendIfChanged(50));
        Assert.Empty(store.All());
    }

    [Fact]
    public void SettingsStore_FailedSet_KeepsTheOldValue()
    {
        File.WriteAllText(_file.Path, "{}");
        var store = new SettingsStore(_file.Path);
        using var held = new FileStream(_file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.False(store.TrySet("theme", "dark"));
        Assert.Null(store.Get("theme"));
    }

    // -- Quarantine is for unreadable content, not for a locked file --

    [Fact]
    public void JsonFile_LoadWithALockedFile_ServesEmptyWithoutQuarantining()
    {
        File.WriteAllText(_file.Path, "[\"kept\"]");
        using (var held = new FileStream(_file.Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var loaded = JsonFile.Load(_file.Path, "test file", () => new List<string>());
            Assert.Empty(loaded);
        }

        Assert.True(File.Exists(_file.Path));
        Assert.False(File.Exists(_file.Path + ".corrupt"));
        Assert.Equal("[\"kept\"]", File.ReadAllText(_file.Path));
    }

    [Fact]
    public void JsonFile_RequiredLoadWithALockedFile_ThrowsAndLeavesTheFile()
    {
        File.WriteAllText(_file.Path, "[]");
        using var held = new FileStream(_file.Path, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.ThrowsAny<Exception>(() =>
            JsonFile.Load(_file.Path, "test file", () => new List<string>(), required: true));
        Assert.True(File.Exists(_file.Path));
    }

    public void Dispose() => _file.Dispose();
}

using Stratus.Core.Models;
using Stratus.Core.Services;
using Stratus.Core.Tweaks;
using Xunit;

namespace Stratus.Tests;

public class TweakCatalogTests : IDisposable
{
    private readonly TempFile _backupFile = new();
    private readonly TempFile _settingsFile = new();

    private List<Tweak> BuildCatalog() => TweakCatalog.Build(
        new RegistryService(new BackupStore(_backupFile.Path)),
        new PowerPlanService(),
        new SettingsStore(_settingsFile.Path),
        new BackupStore(_backupFile.Path));

    [Fact]
    public void Catalog_HasNoDuplicateIds()
    {
        var ids = BuildCatalog().Select(t => t.Id).ToList();
        var duplicates = ids.GroupBy(i => i, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
        Assert.Empty(duplicates);
    }

    [Fact]
    public void Catalog_EntriesAreDescribed()
    {
        foreach (var tweak in BuildCatalog())
        {
            Assert.False(string.IsNullOrWhiteSpace(tweak.Name), $"{tweak.Id} has no name");
            Assert.False(string.IsNullOrWhiteSpace(tweak.Description), $"{tweak.Id} has no description");
        }
    }

    /// <summary>
    /// Guards against a second entry silently repeating another entry's actions: the removed
    /// game-gamebar-startup-off duplicate slipped through exactly this way.
    /// </summary>
    [Fact]
    public void Catalog_RegistryTweaks_ShareNoIdenticalActionSets()
    {
        var signatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tweak in BuildCatalog().OfType<RegistryTweak>())
        {
            var signature = string.Join(";", typeof(RegistryTweak)
                .GetField("_actions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(tweak) is RegistryAction[] actions
                ? actions.Select(a => $"{a.Hive}|{a.Path}|{a.Name}|{a.TweakedValue}")
                : Array.Empty<string>());

            Assert.True(signatures.Add(signature),
                $"{tweak.Id} repeats the registry actions of an earlier tweak");
        }
    }

    /// <summary>
    /// A preset that names a tweak which no longer exists silently applies less than it
    /// promises, so every id is resolved against the live catalog.
    /// </summary>
    [Fact]
    public void Presets_ReferenceOnlyRealTweaks()
    {
        var ids = BuildCatalog().Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(PresetCatalog.All);

        foreach (var preset in PresetCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Name), $"{preset.Id} has no name");
            Assert.False(string.IsNullOrWhiteSpace(preset.Description), $"{preset.Id} has no description");
            Assert.NotEmpty(preset.TweakIds);

            foreach (var id in preset.TweakIds)
                Assert.True(ids.Contains(id), $"Preset {preset.Id} references unknown tweak {id}");
        }
    }

    [Fact]
    public void Presets_HaveNoDuplicateIdsOrNames()
    {
        var ids = PresetCatalog.All.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var names = PresetCatalog.All.Select(p => p.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Catalog_KnownIdsAreStable()
    {
        // Other components (presets, history, the score engine) reference tweak ids by string.
        var ids = BuildCatalog().Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("game-dvr-off", ids);
        Assert.Contains("net-smb1-off", ids);
        Assert.Contains("power-boost-mode", ids);
        Assert.DoesNotContain("game-gamebar-startup-off", ids);
    }

    public void Dispose()
    {
        _backupFile.Dispose();
        _settingsFile.Dispose();
    }
}

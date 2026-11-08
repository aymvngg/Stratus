using Microsoft.Win32;
using System.ServiceProcess;
using Stratus.Core;
using Stratus.Core.Models;
using Stratus.Core.Services;
using Stratus.Core.Tweaks;
using Xunit;

namespace Stratus.Tests;

/// <summary>
/// Apply and revert rules checked against a throwaway HKCU key: a repeat apply keeps the
/// first original, a failed multi-value apply rolls itself back, and a partial revert keeps
/// its backups. No administrator rights and no system key is touched.
/// </summary>
public class TweakSafetyTests : IDisposable
{
    private readonly TempFile _backupFile = new();
    private readonly TempFile _settingsFile = new();
    private readonly string _keyPath = "Software\\Stratus\\Tests\\" + Guid.NewGuid().ToString("N");

    private static RegistryService NewRegistry(IBackupStore store) => new(store);

    private List<Tweak> BuildCatalog(RegistryService registry, IBackupStore backup, PowerPlanService? power = null) => TweakCatalog.Build(
        registry, power ?? new PowerPlanService(), new SettingsStore(_settingsFile.Path), backup);

    private RegistryAction Value(string name, int tweaked, int? @default = 1) =>
        RegistryAction.SetDword(RegistryHive.CurrentUser, _keyPath, name, tweaked, @default);

    [Fact]
    public void Service_FailedConfigurationRestartsThePreviouslyRunningService()
    {
        var calls = new List<string>();
        var tweak = new ServiceTweak("test-service", "Test", "Test", "fake",
            ServiceStartupType.Disabled, ServiceStartupType.Manual, new BackupStore(_backupFile.Path),
            _ => ServiceStartMode.Automatic,
            (_, _) => { calls.Add("configure"); return (false, "denied"); },
            _ => { calls.Add("stop"); return true; },
            _ => { calls.Add("start"); return true; },
            status: _ => ServiceControllerStatus.Running);

        Assert.False(tweak.Apply().Success);
        Assert.Equal(new[] { "stop", "configure", "start" }, calls);
    }

    [Fact]
    public void Service_UnknownStartTypeDoesNotStopOrConfigure()
    {
        var changes = 0;
        var tweak = new ServiceTweak("test-service", "Test", "Test", "fake",
            ServiceStartupType.Disabled, ServiceStartupType.Manual, new BackupStore(_backupFile.Path),
            _ => null,
            (_, _) => { changes++; return (true, ""); },
            _ => { changes++; return true; },
            _ => { changes++; return true; },
            status: _ => ServiceControllerStatus.Running);

        Assert.False(tweak.Apply().Success);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Boost_FailedRevertRefreshKeepsOriginalsForRetry()
    {
        var registry = NewRegistry(new BackupStore(_backupFile.Path));
        var guid = Guid.NewGuid().ToString();
        var path = _keyPath + "\\" + guid + @"\54533251-82be-4824-96c1-47b60b740d00\be337238-0d82-4146-a960-4f3749d470c7";
        using (var root = Registry.CurrentUser.CreateSubKey(_keyPath))
            root.SetValue("ActivePowerScheme", guid);
        using (var key = Registry.CurrentUser.CreateSubKey(path))
        {
            key.SetValue("ACSettingIndex", 1);
            key.SetValue("DCSettingIndex", 3);
        }
        var exitCode = 0;
        var tweak = new PowerBoostTweak(registry, _ => new ProcessResult(exitCode, "", ""),
            RegistryHive.CurrentUser, _keyPath);
        Assert.True(tweak.Apply().Success);
        exitCode = 1;

        Assert.False(tweak.Revert().Success);
        Assert.True(registry.HasBackups("power-boost-mode"));

        exitCode = 0;
        Assert.True(tweak.Revert().Success);
        Assert.False(registry.HasBackups("power-boost-mode"));
        Assert.Equal(1, registry.ReadRequired(RegistryHive.CurrentUser, path, "ACSettingIndex").Value);
    }

    [Fact]
    public void Boost_FailedActivationRestoresAttemptValues()
    {
        var registry = NewRegistry(new BackupStore(_backupFile.Path));
        var guid = Guid.NewGuid().ToString();
        var path = _keyPath + "\\" + guid + @"\54533251-82be-4824-96c1-47b60b740d00\be337238-0d82-4146-a960-4f3749d470c7";
        using (var root = Registry.CurrentUser.CreateSubKey(_keyPath))
            root.SetValue("ActivePowerScheme", guid);
        using (var key = Registry.CurrentUser.CreateSubKey(path))
        {
            key.SetValue("ACSettingIndex", 1);
            key.SetValue("DCSettingIndex", 3);
        }
        var tweak = new PowerBoostTweak(registry, _ => new ProcessResult(1, "", "activation failed"),
            RegistryHive.CurrentUser, _keyPath);

        Assert.False(tweak.Apply().Success);

        Assert.Equal(1, registry.ReadRequired(RegistryHive.CurrentUser, path, "ACSettingIndex").Value);
        Assert.Equal(3, registry.ReadRequired(RegistryHive.CurrentUser, path, "DCSettingIndex").Value);
    }

    [Fact]
    public void Reapply_KeepsTheFirstOriginal()
    {
        var backup = new BackupStore(_backupFile.Path);
        var registry = NewRegistry(backup);
        using (var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true))
            key.SetValue("Value", 1, RegistryValueKind.DWord);

        var tweak = new RegistryTweak("test-apply-twice", "Test", "Test entry", TweakCategory.Power, registry, [Value("Value", 2)]);

        Assert.True(tweak.Apply().Success);
        backup.Append(new BackupEntry
        {
            TweakId = "test-apply-twice",
            ScopeType = RegistryService.ScopeType,
            ScopeId = RegistryService.ScopeIdOf(RegistryHive.CurrentUser, _keyPath, "Value"),
            Key = "Value",
            ValueKind = "4",
            ValueJson = "999",
            Existed = true
        });

        Assert.True(tweak.Apply().Success);

        var saved = backup.GetLatest("test-apply-twice", RegistryService.ScopeType,
            RegistryService.ScopeIdOf(RegistryHive.CurrentUser, _keyPath, "Value"), "Value");
        Assert.Equal("999", saved!.ValueJson);
        Assert.Equal(2, registry.Read(RegistryHive.CurrentUser, _keyPath, "Value").Value);
    }

    [Fact]
    public void FailedApply_RollsBackOnlyTheAttempt()
    {
        var backup = new BackupStore(_backupFile.Path);
        var registry = NewRegistry(backup);
        using (var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true))
            key.SetValue("Good", 1, RegistryValueKind.DWord);

        backup.Append(new BackupEntry
        {
            TweakId = "test-partial",
            ScopeType = RegistryService.ScopeType,
            ScopeId = RegistryService.ScopeIdOf(RegistryHive.CurrentUser, _keyPath, "Good"),
            Key = "Good",
            ValueKind = "4",
            ValueJson = "durable",
            Existed = true
        });

        // The second value has an unsupported kind, so it throws after the first was written.
        var tweak = new RegistryTweak("test-partial", "Test", "Test entry", TweakCategory.Power, registry,
            [
                Value("Good", 2),
                new RegistryAction
                {
                    Hive = RegistryHive.CurrentUser,
                    Path = _keyPath,
                    Name = "Broken",
                    Kind = (RegistryValueKind)42,
                    TweakedValue = 2
                }
            ]);

        var result = tweak.Apply();

        Assert.False(result.Success);
        Assert.Equal(1, registry.Read(RegistryHive.CurrentUser, _keyPath, "Good").Value);

        var saved = backup.GetLatest("test-partial", RegistryService.ScopeType,
            RegistryService.ScopeIdOf(RegistryHive.CurrentUser, _keyPath, "Good"), "Good");
        Assert.Equal("durable", saved!.ValueJson);
    }

    [Fact]
    public void FailedApply_WithNoPriorOriginal_LeavesNoBackupBehind()
    {
        var backup = new BackupStore(_backupFile.Path);
        var registry = NewRegistry(backup);
        using (var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true))
            key.SetValue("Good", 1, RegistryValueKind.DWord);

        var tweak = new RegistryTweak("test-partial-clean", "Test", "Test entry", TweakCategory.Power, registry,
            [
                Value("Good", 2),
                new RegistryAction
                {
                    Hive = RegistryHive.CurrentUser,
                    Path = _keyPath,
                    Name = "Broken",
                    Kind = (RegistryValueKind)42,
                    TweakedValue = 2
                }
            ]);

        Assert.False(tweak.Apply().Success);
        Assert.Equal(1, registry.Read(RegistryHive.CurrentUser, _keyPath, "Good").Value);
        Assert.False(backup.HasEntries("test-partial-clean"));
    }

    [Fact]
    public void PartialRevert_KeepsEveryBackup()
    {
        var backup = new BackupStore(_backupFile.Path);
        var registry = NewRegistry(backup);
        using (var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true))
            key.SetValue("Value", 1, RegistryValueKind.DWord);

        registry.WriteValue(RegistryHive.CurrentUser, _keyPath, "Value", RegistryValueKind.DWord, 2, "test-partial-revert");
        backup.Append(new BackupEntry
        {
            TweakId = "test-partial-revert",
            ScopeType = RegistryService.ScopeType,
            ScopeId = "not-a-scope-id",
            Key = "Broken",
            ValueKind = "4",
            ValueJson = "0",
            Existed = true
        });

        Assert.False(registry.RestoreAll("test-partial-revert"));
        Assert.Equal(1, registry.Read(RegistryHive.CurrentUser, _keyPath, "Value").Value);
        Assert.True(backup.HasEntries("test-partial-revert"));
    }

    [Fact]
    public void Revert_RestoresTheOriginal_NotTheDeclaredDefault()
    {
        var backup = new BackupStore(_backupFile.Path);
        var registry = NewRegistry(backup);
        using (var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true))
            key.SetValue("Value", 7, RegistryValueKind.DWord);

        var tweak = new RegistryTweak("test-revert-original", "Test", "Test entry", TweakCategory.Power, registry,
            [Value("Value", 2, @default: 1)]);

        Assert.True(tweak.Apply().Success);
        Assert.True(tweak.Revert().Success);

        Assert.Equal(7, registry.Read(RegistryHive.CurrentUser, _keyPath, "Value").Value);
        Assert.False(backup.HasEntries("test-revert-original"));
    }

    [Fact]
    public void Revert_WithoutABackup_FallsBackToTheDefault()
    {
        var backup = new BackupStore(_backupFile.Path);
        var registry = NewRegistry(backup);
        using (var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true))
            key.SetValue("Value", 2, RegistryValueKind.DWord);

        var tweak = new RegistryTweak("test-revert-default", "Test", "Test entry", TweakCategory.Power, registry,
            [Value("Value", 2, @default: 1)]);

        Assert.True(tweak.Revert().Success);
        Assert.Equal(1, registry.Read(RegistryHive.CurrentUser, _keyPath, "Value").Value);
    }

    [Fact]
    public void Attempt_RollsBackEveryValueItWrote()
    {
        // Same mechanism Nagle uses across interfaces: a failure part way through must not
        // leave the earlier values applied.
        var backup = new BackupStore(_backupFile.Path);
        var registry = NewRegistry(backup);
        using (var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true))
        {
            key.SetValue("A", 1, RegistryValueKind.DWord);
            key.SetValue("B", 2, RegistryValueKind.DWord);
        }

        var attempt = registry.BeginAttempt("test-attempt");
        attempt.Write(RegistryHive.CurrentUser, _keyPath, "A", RegistryValueKind.DWord, 9);
        attempt.Write(RegistryHive.CurrentUser, _keyPath, "B", RegistryValueKind.DWord, 8);

        Assert.True(attempt.Rollback());
        Assert.Equal(1, registry.Read(RegistryHive.CurrentUser, _keyPath, "A").Value);
        Assert.Equal(2, registry.Read(RegistryHive.CurrentUser, _keyPath, "B").Value);
        Assert.False(backup.HasEntries("test-attempt"));
    }

    [Fact]
    public void Attempt_Rollback_KeepsAnOriginalSavedBeforeTheAttempt()
    {
        var backup = new BackupStore(_backupFile.Path);
        var registry = NewRegistry(backup);
        using (var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true))
            key.SetValue("A", 1, RegistryValueKind.DWord);

        registry.SaveOriginal(new BackupEntry
        {
            TweakId = "earlier",
            ScopeType = RegistryService.ScopeType,
            ScopeId = RegistryService.ScopeIdOf(RegistryHive.CurrentUser, _keyPath, "A"),
            Key = "A",
            ValueKind = "4",
            ValueJson = "1",
            Existed = true
        });
        var before = backup.GetLatest("earlier", RegistryService.ScopeType,
            RegistryService.ScopeIdOf(RegistryHive.CurrentUser, _keyPath, "A"), "A");
        Assert.NotNull(before);

        var attempt = registry.BeginAttempt("test-attempt-keeps");
        attempt.Write(RegistryHive.CurrentUser, _keyPath, "A", RegistryValueKind.DWord, 9);
        Assert.True(attempt.Rollback());

        Assert.Equal(1, registry.Read(RegistryHive.CurrentUser, _keyPath, "A").Value);
        Assert.NotNull(backup.GetLatest("earlier", RegistryService.ScopeType,
            RegistryService.ScopeIdOf(RegistryHive.CurrentUser, _keyPath, "A"), "A"));
    }

    [Fact]
    public void UnreadableValue_StopsTheWriteInsteadOfSavingAnAbsentOriginal()
    {
        // A failed read used to look like "the value does not exist", so the attempt saved
        // Existed=false and a rollback would have deleted the real value.
        var backup = new BackupStore(_backupFile.Path);
        var registry = new RegistryService(backup, (_, _, _) =>
            new RegistryRead(false, RegistryValueKind.None, null) { ReadFailed = true });

        var attempt = registry.BeginAttempt("test-unreadable");

        Assert.Throws<RegistryReadException>(() =>
            attempt.Write(RegistryHive.CurrentUser, _keyPath, "Value", RegistryValueKind.DWord, 1));
        Assert.False(backup.HasEntries("test-unreadable"));
    }

    [Fact]
    public void UnreadableValue_StopsWriteValueAndDeleteValue()
    {
        var backup = new BackupStore(_backupFile.Path);
        var registry = new RegistryService(backup, (_, _, _) =>
            new RegistryRead(false, RegistryValueKind.None, null) { ReadFailed = true });

        Assert.Throws<RegistryReadException>(() =>
            registry.WriteValue(RegistryHive.CurrentUser, _keyPath, "Value", RegistryValueKind.DWord, 1, "test-unreadable"));
        Assert.Throws<RegistryReadException>(() =>
            registry.DeleteValue(RegistryHive.CurrentUser, _keyPath, "Value", "test-unreadable"));
        Assert.False(backup.HasEntries("test-unreadable"));
    }

    [Fact]
    public void UnreadableValue_IsReportedAsUnknownByProbe()
    {
        var backup = new BackupStore(_backupFile.Path);
        var registry = new RegistryService(backup, (_, _, _) =>
            new RegistryRead(false, RegistryValueKind.None, null) { ReadFailed = true });

        var read = registry.Read(RegistryHive.CurrentUser, _keyPath, "Value");

        Assert.True(read.ReadFailed);
        Assert.False(read.Exists);
    }

    [Fact]
    public void DisplayProbe_TreatsAnUnreadableValueAsUnknownNotApplied()
    {
        var backup = new BackupStore(_backupFile.Path);
        var registry = new RegistryService(backup, (_, _, _) =>
            new RegistryRead(false, RegistryValueKind.None, null) { ReadFailed = true });

        var tweak = new RegistryTweak("test-unreadable-probe", "Test", "Test entry", TweakCategory.Power, registry,
            [Value("Value", 0)]);

        Assert.Throws<RegistryReadException>(() => tweak.IsApplied);
    }

    [Fact]
    public void Catalog_RegistryTweaks_OwnEachValueOnce()
    {
        var backupFile = new TempFile();
        try
        {
            var backup = new BackupStore(backupFile.Path);
            var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var tweak in BuildCatalog(NewRegistry(backup), backup).OfType<RegistryTweak>())
            {
                foreach (var action in tweak.Actions)
                {
                    var key = $"{action.Hive}|{action.Path}|{action.Name}";
                    Assert.False(owners.TryGetValue(key, out var other),
                        $"{tweak.Id} and {other} both change {key}");
                    owners[key] = tweak.Id;
                }
            }
        }
        finally
        {
            backupFile.Dispose();
        }
    }

    [Fact]
    public void Catalog_RegistryTweaks_ExposeTheirActions()
    {
        var backup = new BackupStore(_backupFile.Path);
        var tweaks = BuildCatalog(NewRegistry(backup), backup).OfType<RegistryTweak>().ToList();

        Assert.NotEmpty(tweaks);
        Assert.All(tweaks, t => Assert.NotEmpty(t.Actions));
    }

    [Fact]
    public void ServiceRevert_KeepsASavedOriginalThatEqualsTheTarget()
    {
        // The original is the same start type the tweak sets, which used to look like
        // "no revert needed" and silently fell through to the factory fallback.
        var saved = new BackupEntry { ValueJson = "Disabled" };

        Assert.True(ServiceTweak.TryChooseRevertTarget(saved, ServiceStartupType.Manual, out var disabled));
        Assert.Equal(ServiceStartupType.Disabled, disabled);
        Assert.True(ServiceTweak.TryChooseRevertTarget(new BackupEntry { ValueJson = "Automatic" }, ServiceStartupType.Manual, out var automatic));
        Assert.Equal(ServiceStartupType.Automatic, automatic);
        Assert.True(ServiceTweak.TryChooseRevertTarget(null, ServiceStartupType.Manual, out var fallback));
        Assert.Equal(ServiceStartupType.Manual, fallback);
    }

    [Fact]
    public void DelegateTweak_ThrowingProbe_IsUnknownInTheScoreNotNotApplied()
    {
        var throwing = new DelegateTweak("probe-throws", "T", "T", TweakCategory.Power,
            () => throw new InvalidOperationException("read denied"),
            () => TweakResult.Ok, () => TweakResult.Ok);
        var known = new DelegateTweak("probe-known", "K", "K", TweakCategory.Power,
            () => true, () => TweakResult.Ok, () => TweakResult.Ok);

        var report = new ScoreService().Compute([throwing, known], _ => { });

        Assert.Equal(1, report.UnknownCount);
        Assert.Equal("Incomplete", report.StatusText);
        Assert.Equal(1, report.TotalCount);
    }

    [Fact]
    public void Boost_UnreadableValues_ProbeIsUnknown()
    {
        var registry = new RegistryService(new BackupStore(_backupFile.Path), (_, _, _) =>
            new RegistryRead(false, RegistryValueKind.None, null) { ReadFailed = true });
        var tweak = new PowerBoostTweak(registry, _ => new ProcessResult(0, "", ""), RegistryHive.CurrentUser, _keyPath);

        Assert.ThrowsAny<Exception>(() => tweak.IsApplied());
    }

    [Fact]
    public void Nagle_UnreadableValue_ProbeIsUnknown()
    {
        var registry = new RegistryService(new BackupStore(_backupFile.Path), (_, _, _) =>
            new RegistryRead(false, RegistryValueKind.None, null) { ReadFailed = true });
        var tweak = NagleTweak.Create(registry);

        Assert.ThrowsAny<Exception>(() => tweak.IsApplied);
    }

    [Fact]
    public void Hibernation_UnreadableValue_ProbeIsUnknown()
    {
        var backup = new BackupStore(_backupFile.Path);
        var registry = new RegistryService(backup, (_, _, _) =>
            new RegistryRead(false, RegistryValueKind.None, null) { ReadFailed = true });
        var tweak = BuildCatalog(registry, backup).Single(t => t.Id == "power-hibernation-off");

        Assert.ThrowsAny<Exception>(() => tweak.IsApplied);
    }

    [Fact]
    public void UltimatePlan_FailedProbe_IsUnknownInTheScore()
    {
        var backup = new BackupStore(_backupFile.Path);
        var registry = new RegistryService(backup);
        var power = new PowerPlanService(_ => throw new InvalidOperationException("powercfg failed"));
        var tweak = BuildCatalog(registry, backup, power).Single(t => t.Id == "power-ultimate");

        Assert.ThrowsAny<Exception>(() => tweak.IsApplied);

        var known = new DelegateTweak("probe-known2", "K", "K", TweakCategory.Power,
            () => true, () => TweakResult.Ok, () => TweakResult.Ok);
        var report = new ScoreService().Compute([tweak, known], _ => { });
        Assert.Equal(1, report.UnknownCount);
        Assert.Equal("Incomplete", report.StatusText);
    }

    [Fact]
    public void Hibernation_UnreadableValue_FailsApplyBeforeSavingOrRunningPowercfg()
    {
        var backup = new BackupStore(_backupFile.Path);
        var registry = new RegistryService(backup, (_, _, _) =>
            new RegistryRead(false, RegistryValueKind.None, null) { ReadFailed = true });
        var ran = new List<string>();

        var result = TweakCatalog.SetHibernation(false, registry, arguments =>
        {
            ran.Add(arguments);
            return new ProcessResult(0, "", "");
        });

        Assert.False(result.Success);
        Assert.Empty(ran);
        Assert.False(backup.HasEntries("power-hibernation-off"));
    }

    [Fact]
    public void Catalog_NagleIsOneEntry()
    {
        var backup = new BackupStore(_backupFile.Path);
        var catalog = BuildCatalog(NewRegistry(backup), backup);

        Assert.Single(catalog, t => t.Id == NagleTweak.TweakId);
    }

    [Fact]
    public async Task Engine_ConflictingOperations_DoNotInterleave()
    {
        var firstStarted = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var overlap = false;

        var slow = new DelegateTweak(
            "slow", "Slow", "Slow test entry", TweakCategory.Power,
            () => false,
            () =>
            {
                firstStarted.SetResult();
                release.Task.GetAwaiter().GetResult();
                return TweakResult.Ok;
            },
            () => TweakResult.Ok);

        var quick = new DelegateTweak(
            "quick", "Quick", "Quick test entry", TweakCategory.Power,
            () => false,
            () =>
            {
                if (!firstStarted.Task.IsCompleted)
                    overlap = true;
                return TweakResult.Ok;
            },
            () => TweakResult.Ok);

        var engine = new TweakEngine([slow, quick]);

        var running = engine.ApplyAsync(slow);
        await firstStarted.Task;
        var queued = engine.ApplyAsync(quick);

        release.SetResult();
        await Task.WhenAll(running, queued);

        Assert.False(overlap);
        Assert.True(running.Result.Success);
        Assert.True(queued.Result.Success);
    }

    [Fact]
    public async Task Engine_ThrowingTweak_ReportsFailureInsteadOfEscaping()
    {
        var broken = new ThrowingTweak();
        var engine = new TweakEngine([broken]);

        var result = await engine.ApplyAsync(broken);

        Assert.False(result.Success);
        Assert.Contains("boom", result.Error);
    }

    private sealed class ThrowingTweak : Tweak
    {
        public ThrowingTweak() : base("broken", "Broken", "Throws on apply", TweakCategory.Power) { }

        public override bool IsApplied => false;
        public override TweakResult Apply() => throw new InvalidOperationException("boom");
        public override TweakResult Revert() => TweakResult.Ok;
    }

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false); }
        catch { /* best effort */ }
        _backupFile.Dispose();
        _settingsFile.Dispose();
    }
}

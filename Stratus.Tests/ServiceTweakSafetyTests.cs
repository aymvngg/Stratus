using System.ServiceProcess;
using Stratus.Core.Models;
using Stratus.Core.Services;
using Stratus.Core.Tweaks;
using Xunit;

namespace Stratus.Tests;

/// <summary>
/// ServiceTweak rules against fakes: a revert with no saved original only undoes a service
/// still at the tweak's target, an unreadable state fails closed, the delayed-auto flag
/// survives in the saved original, boot and system start types are refused, and an absent
/// service probes as false while an unreadable one probes as unknown.
/// </summary>
public class ServiceTweakSafetyTests : IDisposable
{
    private readonly TempFile _backupFile = new();

    private ServiceTweak Tweak(
        ServiceStartupType target,
        ServiceStartupType fallback,
        Func<string, ServiceStartMode?> query,
        List<ServiceStartupType>? configured = null,
        Func<string, bool?>? serviceExists = null,
        Func<string, bool?>? delayedAuto = null,
        Func<string, ServiceControllerStatus?>? status = null) => new(
        "test-service", "Test", "Test", "fake",
        target, fallback, new BackupStore(_backupFile.Path),
        query,
        (_, type) => { configured?.Add(type); return (true, ""); },
        _ => true,
        _ => true,
        status: status,
        serviceExists: serviceExists,
        delayedAuto: delayedAuto);

    private BackupEntry Saved(string valueJson) => new()
    {
        TweakId = "test-service",
        ScopeType = ServiceTweak.ScopeType,
        ScopeId = "fake",
        Key = ServiceTweak.StartTypeKey,
        ValueJson = valueJson,
        Existed = true
    };

    // -- Revert with no saved original --

    [Fact]
    public void Revert_WithoutBackup_WhenServiceIsNotAtTarget_DoesNothing()
    {
        var configured = new List<ServiceStartupType>();
        var tweak = Tweak(ServiceStartupType.Disabled, ServiceStartupType.Automatic,
            _ => ServiceStartMode.Automatic, configured);

        Assert.True(tweak.Revert().Success);
        Assert.Empty(configured);
    }

    [Fact]
    public void Revert_WithoutBackup_WhenStartTypeIsUnknown_FailsWithoutConfiguring()
    {
        var configured = new List<ServiceStartupType>();
        var tweak = Tweak(ServiceStartupType.Disabled, ServiceStartupType.Manual,
            _ => null, configured);

        Assert.False(tweak.Revert().Success);
        Assert.Empty(configured);
    }

    [Fact]
    public void Revert_WithoutBackup_WhenServiceIsAtTarget_RestoresTheFallback()
    {
        var configured = new List<ServiceStartupType>();
        var tweak = Tweak(ServiceStartupType.Disabled, ServiceStartupType.Manual,
            _ => ServiceStartMode.Disabled, configured);

        Assert.True(tweak.Revert().Success);
        Assert.Equal([ServiceStartupType.Manual], configured);
    }

    // -- Saved original wins over the fallback, and the backup clears on success --

    [Fact]
    public void Revert_WithASavedOriginal_RestoresItAndClearsTheBackup()
    {
        var backup = new BackupStore(_backupFile.Path);
        backup.Append(Saved("Manual"));
        var configured = new List<ServiceStartupType>();
        var tweak = Tweak(ServiceStartupType.Disabled, ServiceStartupType.Automatic,
            _ => ServiceStartMode.Disabled, configured);

        Assert.True(tweak.Revert().Success);
        Assert.Equal([ServiceStartupType.Manual], configured);
        Assert.False(backup.HasEntries("test-service"));
    }

    // -- Delayed-auto flag in the saved original --

    [Fact]
    public void Apply_OfADelayedAutomaticService_SavesTheFlagInTheOriginal()
    {
        var backup = new BackupStore(_backupFile.Path);
        var configured = new List<ServiceStartupType>();
        var tweak = Tweak(ServiceStartupType.Manual, ServiceStartupType.Manual,
            _ => ServiceStartMode.Automatic, configured, delayedAuto: _ => true);

        Assert.True(tweak.Apply().Success);

        var saved = backup.GetLatest("test-service", ServiceTweak.ScopeType, "fake", ServiceTweak.StartTypeKey);
        Assert.NotNull(saved);
        Assert.Equal("AutomaticDelayed", saved!.ValueJson);
    }

    [Fact]
    public void Apply_OfAPlainAutomaticService_SavesPlainAutomatic()
    {
        var backup = new BackupStore(_backupFile.Path);
        var configured = new List<ServiceStartupType>();
        var tweak = Tweak(ServiceStartupType.Manual, ServiceStartupType.Manual,
            _ => ServiceStartMode.Automatic, configured, delayedAuto: _ => false);

        Assert.True(tweak.Apply().Success);

        var saved = backup.GetLatest("test-service", ServiceTweak.ScopeType, "fake", ServiceTweak.StartTypeKey);
        Assert.NotNull(saved);
        Assert.Equal("Automatic", saved!.ValueJson);
    }

    [Fact]
    public void Revert_OfASavedDelayedOriginal_RestoresDelayedAuto()
    {
        var backup = new BackupStore(_backupFile.Path);
        backup.Append(Saved("AutomaticDelayed"));
        var configured = new List<ServiceStartupType>();
        var tweak = Tweak(ServiceStartupType.Manual, ServiceStartupType.Manual,
            _ => ServiceStartMode.Manual, configured);

        Assert.True(tweak.Revert().Success);
        Assert.Equal([ServiceStartupType.AutomaticDelayed], configured);
        Assert.False(backup.HasEntries("test-service"));
    }

    [Fact]
    public void RevertTarget_ParsesTheDelayedOriginal()
    {
        Assert.True(ServiceTweak.TryChooseRevertTarget(Saved("AutomaticDelayed"),
            ServiceStartupType.Manual, out var target));
        Assert.Equal(ServiceStartupType.AutomaticDelayed, target);
    }

    [Fact]
    public void Apply_WithAnUnreadableDelayedFlag_FailsClosed()
    {
        var backup = new BackupStore(_backupFile.Path);
        var configured = new List<ServiceStartupType>();
        var tweak = Tweak(ServiceStartupType.Manual, ServiceStartupType.Manual,
            _ => ServiceStartMode.Automatic, configured, delayedAuto: _ => null);

        Assert.False(tweak.Apply().Success);
        Assert.Empty(configured);
        Assert.False(backup.HasEntries("test-service"));
    }

    // -- Boot and system start types are refused --

    [Theory]
    [InlineData(ServiceStartMode.Boot)]
    [InlineData(ServiceStartMode.System)]
    public void Apply_RefusesDriverStartTypes(ServiceStartMode mode)
    {
        var backup = new BackupStore(_backupFile.Path);
        var configured = new List<ServiceStartupType>();
        var tweak = Tweak(ServiceStartupType.Disabled, ServiceStartupType.Automatic,
            _ => mode, configured);

        Assert.False(tweak.Apply().Success);
        Assert.Empty(configured);
        Assert.False(backup.HasEntries("test-service"));
    }

    // -- Absent versus unreadable service --

    [Fact]
    public void Probe_WhenServiceIsAbsent_IsFalseWithoutThrowing()
    {
        var tweak = Tweak(ServiceStartupType.Disabled, ServiceStartupType.Automatic,
            _ => ServiceStartMode.Disabled, serviceExists: _ => false);

        Assert.False(tweak.IsApplicable);
        Assert.False(tweak.IsApplied);
    }

    [Fact]
    public void Probe_WhenExistenceIsUnknown_IsUnknown()
    {
        var tweak = Tweak(ServiceStartupType.Disabled, ServiceStartupType.Automatic,
            _ => ServiceStartMode.Disabled, serviceExists: _ => null);

        Assert.Throws<InvalidOperationException>(() => tweak.IsApplicable);
        Assert.Throws<InvalidOperationException>(() => tweak.IsApplied);
    }

    [Fact]
    public void Probe_WhenPresentButStartTypeIsUnreadable_IsUnknown()
    {
        var tweak = Tweak(ServiceStartupType.Disabled, ServiceStartupType.Automatic,
            _ => null, serviceExists: _ => true);

        Assert.Throws<InvalidOperationException>(() => tweak.IsApplied);
    }

    [Fact]
    public void Apply_WhenExistenceIsUnknown_FailsClosed()
    {
        var backup = new BackupStore(_backupFile.Path);
        var configured = new List<ServiceStartupType>();
        var tweak = Tweak(ServiceStartupType.Disabled, ServiceStartupType.Automatic,
            _ => ServiceStartMode.Disabled, configured, serviceExists: _ => null);

        Assert.False(tweak.Apply().Success);
        Assert.Empty(configured);
        Assert.False(backup.HasEntries("test-service"));
    }

    [Fact]
    public void Apply_WhenServiceIsAbsent_FailsWithoutChanges()
    {
        var backup = new BackupStore(_backupFile.Path);
        var configured = new List<ServiceStartupType>();
        var tweak = Tweak(ServiceStartupType.Disabled, ServiceStartupType.Automatic,
            _ => ServiceStartMode.Disabled, configured, serviceExists: _ => false);

        Assert.False(tweak.Apply().Success);
        Assert.Empty(configured);
        Assert.False(backup.HasEntries("test-service"));
    }

    [Fact]
    public void Score_TreatsAnUnknownServiceAsUnknownAndAnAbsentOneAsNormal()
    {
        var unknown = Tweak(ServiceStartupType.Disabled, ServiceStartupType.Automatic,
            _ => ServiceStartMode.Disabled, serviceExists: _ => null);
        var known = new DelegateTweak("probe-known", "K", "K", TweakCategory.Services,
            () => true, () => TweakResult.Ok, () => TweakResult.Ok);

        var report = new ScoreService().Compute([unknown, known], _ => { });

        Assert.Equal(1, report.UnknownCount);
        Assert.Equal("Incomplete", report.StatusText);
        Assert.Equal(1, report.TotalCount);

        var absent = Tweak(ServiceStartupType.Disabled, ServiceStartupType.Automatic,
            _ => ServiceStartMode.Disabled, serviceExists: _ => false);
        var absentReport = new ScoreService().Compute([absent], _ => { });
        Assert.Equal(0, absentReport.UnknownCount);
    }

    public void Dispose() => _backupFile.Dispose();
}

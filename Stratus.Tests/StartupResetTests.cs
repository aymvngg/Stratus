using Microsoft.Win32;
using Stratus.Core.Services;
using Xunit;

namespace Stratus.Tests;

/// <summary>
/// The startup reset counting rule through the narrow internal seam: every restored entry
/// counts once, a failed restore or thrown exception counts as failed and keeps going, and
/// the entry's saved state stays for a retry (the log carries the reason). The public
/// parameterless path only adds a scan plus the backup check on top.
/// </summary>
public class StartupResetTests : IDisposable
{
    private readonly TempFile _backup = new();

    public void Dispose() => _backup.Dispose();

    private StartupService CreateService() =>
        new(new RegistryService(new BackupStore(_backup.Path)));

    private static StartupItem Item(string name) => new(
        Id: "run:CurrentUser:Run:" + name,
        Name: name,
        Command: "cmd /c " + name,
        Scope: "Current user",
        Source: "Registry",
        Hive: RegistryHive.CurrentUser,
        ApprovedPath: @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
        ValueName: name,
        IsEnabled: true);

    [Fact]
    public void DetailedReset_CountsEachOutcomeAndKeepsGoingAfterFailures()
    {
        var service = CreateService();
        var items = new[] { Item("ok-entry"), Item("false-entry"), Item("throw-entry") };

        var (restored, failed) = service.ResetAllDetailed(items, id =>
        {
            if (id.EndsWith("ok-entry", StringComparison.Ordinal))
                return true;
            if (id.EndsWith("false-entry", StringComparison.Ordinal))
                return false;

            throw new InvalidOperationException("access denied");
        });

        Assert.Equal(1, restored);
        Assert.Equal(2, failed);
    }

    [Fact]
    public void DetailedReset_EveryRestored()
    {
        var service = CreateService();
        var items = new[] { Item("a"), Item("b") };

        var (restored, failed) = service.ResetAllDetailed(items, _ => true);

        Assert.Equal(2, restored);
        Assert.Equal(0, failed);
    }

    [Fact]
    public void DetailedReset_NothingChanged_IsZeroZero()
    {
        var service = CreateService();

        var (restored, failed) = service.ResetAllDetailed(
            Array.Empty<StartupItem>(), _ => throw new InvalidOperationException("never called"));

        Assert.Equal(0, restored);
        Assert.Equal(0, failed);
    }

    [Fact]
    public async Task PublicResetAllDetailed_EmptyBackupStore_FindsNothingToReset()
    {
        var service = CreateService();

        // The scan reads the live machine, the backup check reads the empty temp store:
        // nothing Stratus changed means nothing is attempted and nothing fails.
        var (restored, failed) = await Task.Run(() => service.ResetAllDetailed());

        Assert.Equal(0, restored);
        Assert.Equal(0, failed);
    }
}

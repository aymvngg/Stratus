using Stratus.Core.Models;
using Stratus.Core.Services;
using Xunit;

namespace Stratus.Tests;

public class ProfileServiceTests
{
    private static StratusProfile Sample() => new()
    {
        Version = StratusProfile.CurrentVersion,
        ExportedAtUtc = new DateTime(2026, 10, 27, 9, 30, 0, DateTimeKind.Utc),
        MachineName = "TEST-PC",
        Tweaks = ["game-dvr-off", "priv-telemetry"],
        StartupDisabled = ["startup-HKCU-Run-OneDrive"],
        TasksDisabled = ["\\Microsoft\\Windows\\Application Experience\\ProgramDataUpdater"],
        FeaturesEnabled = ["Microsoft-Windows-Subsystem-Linux"],
        Apps = ["Mozilla.Firefox"],
        CleanupTargets = ["temp", "d3ds-cache"],
        DnsProfile = "cloudflare",
        PowerPlan = "balanced",
        TcpSettings = new Dictionary<string, string> { ["rss"] = "enabled" }
    };

    [Fact]
    public void SerializeThenDeserialize_RoundTripsEverySection()
    {
        var original = Sample();

        var json = ProfileService.Serialize(original);
        var (loaded, error) = ProfileService.Deserialize(json);

        Assert.Null(error);
        Assert.NotNull(loaded);
        Assert.Equal(original.Version, loaded!.Version);
        Assert.Equal(original.MachineName, loaded.MachineName);
        Assert.Equal(original.ExportedAtUtc, loaded.ExportedAtUtc);
        Assert.Equal(original.Tweaks, loaded.Tweaks);
        Assert.Equal(original.StartupDisabled, loaded.StartupDisabled);
        Assert.Equal(original.TasksDisabled, loaded.TasksDisabled);
        Assert.Equal(original.FeaturesEnabled, loaded.FeaturesEnabled);
        Assert.Equal(original.Apps, loaded.Apps);
        Assert.Equal(original.CleanupTargets, loaded.CleanupTargets);
        Assert.Equal(original.DnsProfile, loaded.DnsProfile);
        Assert.Equal(original.PowerPlan, loaded.PowerPlan);
        Assert.Equal("enabled", loaded.TcpSettings["rss"]);
    }

    [Fact]
    public void ItemCount_AddsListsAndSingletonSelections()
    {
        // 2 tweaks + 1 startup + 1 task + 1 feature + 1 app + 2 cleanup + DNS + power + 1 TCP = 11.
        Assert.Equal(11, Sample().ItemCount);
    }

    [Fact]
    public void ItemCount_EmptyProfile_IsZero()
    {
        Assert.Equal(0, new StratusProfile().ItemCount);
    }

    [Fact]
    public void Deserialize_RejectsNewerProfileVersion()
    {
        var profile = Sample();
        profile.Version = StratusProfile.CurrentVersion + 1;

        var (loaded, error) = ProfileService.Deserialize(ProfileService.Serialize(profile));

        Assert.Null(loaded);
        Assert.NotNull(error);
        Assert.Contains("newer Stratus", error);
    }

    [Fact]
    public void Deserialize_RejectsInvalidJson()
    {
        var (loaded, error) = ProfileService.Deserialize("{ not a profile");

        Assert.Null(loaded);
        Assert.NotNull(error);
        Assert.Contains("not a valid Stratus profile", error);
    }

    [Fact]
    public void Deserialize_RejectsEmptyDocument()
    {
        var (loaded, error) = ProfileService.Deserialize("null");

        Assert.Null(loaded);
        Assert.Equal("The file does not contain a Stratus profile.", error);
    }

    [Fact]
    public void Serialize_WritesCamelCaseIndentedJson()
    {
        var json = ProfileService.Serialize(Sample());

        Assert.Contains("\"machineName\"", json);
        Assert.Contains("\"exportedAtUtc\"", json);
        Assert.Contains("\"cleanupTargets\"", json);
        Assert.Contains("\n  ", json);
    }

    [Fact]
    public void DefaultExportPath_IsTimestampedJsonUnderProfiles()
    {
        var path = ProfileService.DefaultExportPath();

        Assert.EndsWith(".json", path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("profiles", path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stratus-profile-", path, StringComparison.OrdinalIgnoreCase);
    }
}

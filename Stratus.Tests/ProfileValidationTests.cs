using Stratus.Core;
using Stratus.Core.Models;
using Stratus.Core.Services;
using Stratus.Core.Tweaks;
using Xunit;

namespace Stratus.Tests;

/// <summary>
/// Shape checks on a profile read from disk. A profile with an explicit JSON null for a
/// collection used to deserialize fine and then crash the CLI when it counted items, so
/// every collection is rejected by name before anything plans against it.
/// </summary>
public class ProfileValidationTests
{
    [Theory]
    [InlineData("tweaks")]
    [InlineData("startupDisabled")]
    [InlineData("tasksDisabled")]
    [InlineData("featuresEnabled")]
    [InlineData("apps")]
    [InlineData("cleanupTargets")]
    [InlineData("tcpSettings")]
    public void Deserialize_RejectsExplicitNullCollection(string property)
    {
        var json = "{\"version\":1,\"" + property + "\":null}";

        var (loaded, error) = ProfileService.Deserialize(json);

        Assert.Null(loaded);
        Assert.NotNull(error);
        Assert.Contains(property, error);
        Assert.Contains("null", error);
    }

    [Theory]
    [InlineData("{\"version\":1,\"tweaks\":[\"ok\",null]}")]
    [InlineData("{\"version\":1,\"tweaks\":[\"ok\",\"  \"]}")]
    [InlineData("{\"version\":1,\"tweaks\":[\"ok\",5]}")]
    [InlineData("{\"version\":1,\"tweaks\":\"ok\"}")]
    public void Deserialize_RejectsInvalidListElements(string json)
    {
        var (loaded, error) = ProfileService.Deserialize(json);

        Assert.Null(loaded);
        Assert.NotNull(error);
        Assert.StartsWith("That file is not a valid Stratus profile", error);
    }

    [Theory]
    [InlineData("{\"version\":1,\"tcpSettings\":{\"rss\":null}}")]
    [InlineData("{\"version\":1,\"tcpSettings\":{\"rss\":\"  \"}}")]
    [InlineData("{\"version\":1,\"tcpSettings\":{\" \":\"enabled\"}}")]
    [InlineData("{\"version\":1,\"tcpSettings\":[1,2]}")]
    public void Deserialize_RejectsInvalidDictionaryEntries(string json)
    {
        var (loaded, error) = ProfileService.Deserialize(json);

        Assert.Null(loaded);
        Assert.NotNull(error);
        Assert.Contains("tcpSettings", error);
    }

    [Theory]
    [InlineData("{\"version\":0,\"tweaks\":[]}")]
    [InlineData("{\"version\":-5,\"tweaks\":[]}")]
    public void Deserialize_RejectsMissingOrNonPositiveVersion(string json)
    {
        var (loaded, error) = ProfileService.Deserialize(json);

        Assert.Null(loaded);
        Assert.NotNull(error);
        Assert.Contains("version", error);
    }

    [Theory]
    [InlineData("{\"version\":\"one\",\"tweaks\":[]}")]
    [InlineData("{\"version\":null,\"tweaks\":[]}")]
    [InlineData("{\"version\":1.5,\"tweaks\":[]}")]
    public void Deserialize_RejectsNonIntegerVersion(string json)
    {
        var (loaded, error) = ProfileService.Deserialize(json);

        Assert.Null(loaded);
        Assert.NotNull(error);
    }

    [Fact]
    public void Deserialize_RejectsNewerVersionBeforeTouchingCollections()
    {
        var (loaded, error) = ProfileService.Deserialize("{\"version\":99,\"tweaks\":null}");

        Assert.Null(loaded);
        Assert.Contains("newer Stratus", error);
    }

    [Fact]
    public void Deserialize_AcceptsMissingVersionAsCurrent()
    {
        var (loaded, error) = ProfileService.Deserialize("{\"tweaks\":[\"game-dvr-off\"]}");

        Assert.Null(error);
        Assert.NotNull(loaded);
        Assert.Equal(StratusProfile.CurrentVersion, loaded!.Version);
        Assert.Equal(new[] { "game-dvr-off" }, loaded.Tweaks);
    }

    [Fact]
    public void Deserialize_AcceptsUnknownPropertiesFromTheSameVersion()
    {
        var (loaded, error) = ProfileService.Deserialize(
            "{\"version\":1,\"tweaks\":[],\"futureField\":\"ignored\"}");

        Assert.Null(error);
        Assert.NotNull(loaded);
    }

    [Fact]
    public void Deserialize_BlankMachineName_IsReplaced()
    {
        var (loaded, error) = ProfileService.Deserialize("{\"version\":1,\"machineName\":\"  \"}");

        Assert.Null(error);
        Assert.Equal("unknown", loaded!.MachineName);
    }

    [Fact]
    public void Deserialize_ValidMinimalProfile_Loads()
    {
        var (loaded, error) = ProfileService.Deserialize("{\"version\":1,\"tweaks\":[]}");

        Assert.Null(error);
        Assert.NotNull(loaded);
        Assert.Equal(0, loaded!.ItemCount);
    }

    [Fact]
    public void Validate_NullProfile_Fails()
    {
        var check = ProfileService.Validate(null);

        Assert.False(check.Valid);
        Assert.NotNull(check.Error);
    }

    [Fact]
    public void Validate_ProgrammaticNullCollection_FailsWithName()
    {
        var profile = new StratusProfile { Tweaks = null! };

        var check = ProfileService.Validate(profile);

        Assert.False(check.Valid);
        Assert.Contains("tweaks", check.Error);
    }

    [Fact]
    public void Validate_ProgrammaticBlankElement_Fails()
    {
        var profile = new StratusProfile { Apps = ["Mozilla.Firefox", ""] };

        var check = ProfileService.Validate(profile);

        Assert.False(check.Valid);
        Assert.Contains("apps", check.Error);
    }

    [Fact]
    public void ItemCount_ToleratesNullCollections()
    {
        // The model is public and can be built in code with nulls; counting must not throw.
        var profile = new StratusProfile
        {
            Tweaks = null!,
            StartupDisabled = null!,
            TasksDisabled = null!,
            FeaturesEnabled = null!,
            Apps = null!,
            CleanupTargets = null!,
            TcpSettings = null!,
            DnsProfile = "cloudflare",
            PowerPlan = "balanced",
        };

        Assert.Equal(2, profile.ItemCount);
    }

    [Fact]
    public void Deserialize_ErrorMessagesAreAscii()
    {
        var (_, error) = ProfileService.Deserialize("{\"version\":1,\"tweaks\":null}");

        Assert.All(error!, c => Assert.True(c < 128, $"Non-ASCII character U+{(int)c:X4}"));
    }

    /// <summary>
    /// Capture reads the machine, so these tests only build the service graph and give it a
    /// winget read that fails. The winget read runs first, before any registry or COM scan,
    /// so nothing on the machine is touched.
    /// </summary>
    private sealed class ProfileHarness : IDisposable
    {
        private readonly TempFile _backup = new();
        private readonly TempFile _settings = new();
        private readonly TempFile _tasks = new();
        public SettingsStore Settings => new(_settings.Path);

        public ProfileService Create(Func<string, ProcessResult> export)
        {
            var backup = new BackupStore(_backup.Path);
            return new ProfileService(
                new TweakEngine(Array.Empty<Tweak>()),
                new StartupService(new RegistryService(backup)),
                new ScheduledTaskService(_tasks.Path),
                new OptionalFeatureService(),
                new WingetService(export),
                new DnsService(),
                new PowerPlanService(),
                new SettingsStore(_settings.Path));
        }

        public void Dispose()
        {
            _backup.Dispose();
            _settings.Dispose();
            _tasks.Dispose();
        }
    }

    [Fact]
    public async Task Capture_FailedWingetRead_ThrowsInsteadOfSavingIncompleteProfile()
    {
        using var harness = new ProfileHarness();
        var service = harness.Create(_ => new ProcessResult(1, "", "winget is unavailable"));

        var ex = await Assert.ThrowsAsync<ProfileValidationException>(() => service.CaptureAsync());

        Assert.Contains("profile was not saved", ex.Message);
        Assert.Contains("winget is unavailable", ex.Message);
    }

    [Fact]
    public async Task Preview_ProfileWantsAppsButWingetReadFails_ThrowsInsteadOfReinstalling()
    {
        using var harness = new ProfileHarness();
        var service = harness.Create(_ => new ProcessResult(1, "", "winget is unavailable"));
        var profile = new StratusProfile { Apps = ["Mozilla.Firefox"] };

        var ex = await Assert.ThrowsAsync<ProfileValidationException>(() => service.PreviewAsync(profile));

        Assert.Contains("installed app list could not be read", ex.Message);
    }

    [Fact]
    public async Task Preview_ProfileWantsNoApps_DoesNotReadWinget()
    {
        using var harness = new ProfileHarness();
        var reads = 0;
        var service = harness.Create(_ =>
        {
            reads++;
            return new ProcessResult(1, "", "should not be called");
        });
        var profile = new StratusProfile();

        var actions = await service.PreviewAsync(profile);

        Assert.Equal(0, reads);
        Assert.DoesNotContain(actions, action => action.WillChange);
    }

    [Fact]
    public async Task Preview_UnknownAppsDoNotRequireWinget()
    {
        using var harness = new ProfileHarness();
        var service = harness.Create(_ => throw new InvalidOperationException("Unexpected winget read"));

        var actions = await service.PreviewAsync(new StratusProfile { Apps = ["Unknown.App"] });

        Assert.Contains(actions, action => action.Category == "App" && !action.WillChange);
    }

    [Fact]
    public void ItemCount_IgnoresBlankOptionalSettings()
    {
        Assert.Equal(0, new StratusProfile { DnsProfile = " ", PowerPlan = "" }.ItemCount);
    }

    [Fact]
    public async Task Apply_CleanupSelectionAddsWithoutRemovingExistingTargets()
    {
        using var harness = new ProfileHarness();
        harness.Settings.Set("cleanup.selection", "existing");
        var service = harness.Create(_ => throw new InvalidOperationException("Unexpected winget read"));

        await service.ApplyAsync(new StratusProfile { CleanupTargets = ["added"] });

        Assert.Equal("existing,added", harness.Settings.Get("cleanup.selection"));
    }

    [Fact]
    public async Task Preview_ProfileWithNullCollection_ThrowsBeforePlanning()
    {
        using var harness = new ProfileHarness();
        var service = harness.Create(_ => new ProcessResult(0, "", ""));
        var profile = new StratusProfile { Tweaks = null! };

        var ex = await Assert.ThrowsAsync<ProfileValidationException>(() => service.PreviewAsync(profile));

        Assert.Contains("tweaks", ex.Message);
    }

    [Fact]
    public async Task Apply_ProfileWithBlankEntry_ThrowsBeforeChangingAnything()
    {
        using var harness = new ProfileHarness();
        var service = harness.Create(_ => new ProcessResult(0, "", ""));
        var profile = new StratusProfile { FeaturesEnabled = ["Microsoft-Windows-Subsystem-Linux", " "] };

        var ex = await Assert.ThrowsAsync<ProfileValidationException>(() => service.ApplyAsync(profile));

        Assert.Contains("featuresEnabled", ex.Message);
    }

    [Fact]
    public async Task Apply_NewerProfileVersion_ThrowsWithUpdateMessage()
    {
        using var harness = new ProfileHarness();
        var service = harness.Create(_ => new ProcessResult(0, "", ""));
        var profile = new StratusProfile { Version = StratusProfile.CurrentVersion + 1 };

        var ex = await Assert.ThrowsAsync<ProfileValidationException>(() => service.ApplyAsync(profile));

        Assert.Contains("newer Stratus", ex.Message);
    }
}

using Stratus.Core.Services;
using Xunit;

namespace Stratus.Tests;

public class PowerPlanTests
{
    [Fact]
    public void ListPlans_FailedReadDoesNotLookLikeAnEmptyMachine()
    {
        var service = new PowerPlanService(_ => new ProcessResult(1, "", "Read denied"));

        Assert.Throws<InvalidOperationException>(() => service.ListPlans());
    }

    [Fact]
    public void GetActivePlan_FailedReadsDoNotLookLikeNoActivePlan()
    {
        var service = new PowerPlanService(_ => new ProcessResult(1, "", "Read denied"));

        Assert.Throws<InvalidOperationException>(() => service.GetActivePlan());
    }

    [Fact]
    public void SetActive_UnknownNameDoesNotThrow()
    {
        var commands = new List<string>();
        var service = new PowerPlanService(arguments =>
        {
            commands.Add(arguments);
            return new ProcessResult(0, "", "");
        });

        Assert.False(service.SetActive("stratus-nonexistent-plan-3f690720"));
        Assert.Equal(new[] { "/list" }, commands);
    }

    [Fact]
    public void SetActive_ResolvesLocalizedNameAndActivatesOnce()
    {
        var commands = new List<string>();
        var service = new PowerPlanService(arguments =>
        {
            commands.Add(arguments);
            return new ProcessResult(0,
                $"Mode: {PowerPlanService.BalancedGuid} (Utilisation normale) *", "");
        });

        Assert.True(service.SetActive("utilisation normale"));
        Assert.Equal(new[] { "/list", "/setactive " + PowerPlanService.BalancedGuid }, commands);
    }

    [Fact]
    public void SetActive_GuidDoesNotNeedNameLookup()
    {
        var commands = new List<string>();
        var service = new PowerPlanService(arguments =>
        {
            commands.Add(arguments);
            return new ProcessResult(0, "", "");
        });

        Assert.True(service.SetActive(PowerPlanService.BalancedGuid));
        Assert.Equal(new[] { "/setactive " + PowerPlanService.BalancedGuid }, commands);
    }

    [Fact]
    public void UltimateIsActive_UsesGuidOnLocalizedWindows()
    {
        using var settingsFile = new TempFile();
        var service = new PowerPlanService(_ => new ProcessResult(0,
            $"Mode: {PowerPlanService.UltimateTemplateGuid} (Performances optimales)", ""));

        Assert.True(service.UltimateIsActive(new SettingsStore(settingsFile.Path)));
    }

    [Fact]
    public void UltimateIsActive_EmptySettingsIsFalseWhenActivePlanIsNotUltimate()
    {
        using var settingsFile = new TempFile();
        var service = new PowerPlanService(_ => new ProcessResult(0,
            $"Mode: {PowerPlanService.BalancedGuid} (Utilisation normale)", ""));

        Assert.False(service.UltimateIsActive(new SettingsStore(settingsFile.Path)));
    }

    [Fact]
    public void SetActive_TrimsPlanName()
    {
        var commands = new List<string>();
        var service = new PowerPlanService(arguments =>
        {
            commands.Add(arguments);
            return new ProcessResult(0,
                $"Mode: {PowerPlanService.BalancedGuid} (Utilisation normale) *", "");
        });

        Assert.True(service.SetActive("  utilisation normale  "));
        Assert.Equal(new[] { "/list", "/setactive " + PowerPlanService.BalancedGuid }, commands);
    }

    [Fact]
    public void ActivateUltimate_ReusesTheStoredDuplicateInsteadOfCreatingAnother()
    {
        using var settingsFile = new TempFile();
        var storedGuid = Guid.Parse("c0ffee00-0000-0000-0000-000000000001");
        new SettingsStore(settingsFile.Path).Set("UltimatePlanGuid", storedGuid.ToString());

        var commands = new List<string>();
        var service = new PowerPlanService(arguments =>
        {
            commands.Add(arguments);
            return arguments == "/list"
                ? new ProcessResult(0,
                    $"Mode: {PowerPlanService.BalancedGuid} (Utilisation normale)\nMode: {storedGuid:D} (Performances optimales)", "")
                : new ProcessResult(0, "", "");
        });

        var plan = service.ActivateUltimate(new SettingsStore(settingsFile.Path));

        Assert.NotNull(plan);
        Assert.Equal(storedGuid, plan.Guid);
        Assert.Equal(new[] { "/list", "/setactive " + storedGuid.ToString("D") }, commands);
    }

    [Fact]
    public void ActivateUltimate_EmptySettingsDuplicatesAndStoresTheNewGuid()
    {
        using var settingsFile = new TempFile();
        var newGuid = Guid.Parse("d0e0a100-0000-0000-0000-000000000002");
        var commands = new List<string>();
        var service = new PowerPlanService(arguments =>
        {
            commands.Add(arguments);
            return arguments == "/list"
                ? new ProcessResult(0, $"Mode: {PowerPlanService.BalancedGuid} (Utilisation normale) *", "")
                : arguments.StartsWith("-duplicatescheme")
                    ? new ProcessResult(0, $"New plan GUID: {newGuid:D} (Ultimate Performance)", "")
                    : new ProcessResult(0, "", "");
        });

        var settings = new SettingsStore(settingsFile.Path);
        var plan = service.ActivateUltimate(settings);

        Assert.NotNull(plan);
        Assert.Equal(newGuid, plan.Guid);
        Assert.Equal(newGuid.ToString(), settings.Get("UltimatePlanGuid"));
        Assert.Equal(new[]
        {
            "/list",
            "-duplicatescheme " + PowerPlanService.UltimateTemplateGuid,
            "/setactive " + newGuid.ToString("D"),
        }, commands);
    }
}

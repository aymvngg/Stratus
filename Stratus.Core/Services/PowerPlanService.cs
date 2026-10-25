using System.Text.RegularExpressions;

namespace Stratus.Core.Services;

public sealed record PowerPlan(Guid Guid, string Name, bool IsActive);

/// <summary>Wraps powercfg.exe - works on localized Windows because parsing is GUID-based.</summary>
public sealed class PowerPlanService
{
    public const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string HighPerformanceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string UltimateTemplateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    private static readonly Regex PlanLine = new(
        @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\(([^)]*)\)\s*(\*)?\s*$",
        RegexOptions.Compiled);

    public List<PowerPlan> ListPlans()
    {
        var result = ProcessRunner.Run("powercfg.exe", "/list");
        var plans = new List<PowerPlan>();

        foreach (var line in (result.StdOut + "\n" + result.StdErr).Split('\n'))
        {
            var match = PlanLine.Match(line.Trim());
            if (match.Success && Guid.TryParse(match.Groups[1].Value, out var guid))
            {
                plans.Add(new PowerPlan(guid, match.Groups[2].Value.Trim(), match.Groups[3].Success));
            }
        }

        return plans;
    }

    public PowerPlan? GetActivePlan()
    {
        var result = ProcessRunner.Run("powercfg.exe", "/getactivescheme");
        var match = PlanLine.Match(result.StdOut.Trim());
        return match.Success && Guid.TryParse(match.Groups[1].Value, out var guid)
            ? new PowerPlan(guid, match.Groups[2].Value.Trim(), true)
            : ListPlans().FirstOrDefault(p => p.IsActive);
    }

    public bool SetActive(Guid guid) =>
        ProcessRunner.Run("powercfg.exe", $"/setactive {guid:D}").Success;

    public bool SetActive(string guidString) =>
        SetActive(Guid.Parse(guidString));

    /// <summary>Activates Ultimate Performance, reusing the copy we created before (avoids duplicates).</summary>
    public PowerPlan? ActivateUltimate(SettingsStore settings)
    {
        var stored = settings.Get("UltimatePlanGuid");
        var plans = ListPlans();

        if (stored is not null && Guid.TryParse(stored, out var existing) && plans.Any(p => p.Guid == existing))
        {
            if (SetActive(existing))
                return plans.First(p => p.Guid == existing);
        }

        // Fall back to any plan whose GUID equals the known template (some SKUs ship it).
        var template = plans.FirstOrDefault(p => p.Guid == Guid.Parse(UltimateTemplateGuid));
        if (template is not null && SetActive(template.Guid))
        {
            settings.Set("UltimatePlanGuid", template.Guid.ToString());
            return template;
        }

        var dup = ProcessRunner.Run("powercfg.exe", $"-duplicatescheme {UltimateTemplateGuid}");
        var m = Regex.Match(dup.StdOut + dup.StdErr,
            @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
        if (!m.Success || !Guid.TryParse(m.Value, out var newGuid))
            return null;

        settings.Set("UltimatePlanGuid", newGuid.ToString());
        return SetActive(newGuid) ? new PowerPlan(newGuid, "Ultimate Performance", true) : null;
    }

    public bool UltimateIsActive(SettingsStore settings)
    {
        var active = GetActivePlan();
        if (active is null)
            return false;

        if (active.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase))
            return true;

        var stored = settings.Get("UltimatePlanGuid");
        return stored is not null && Guid.TryParse(stored, out var g) && active.Guid == g;
    }

    public bool SetBalanced() => SetActive(BalancedGuid);
    public bool SetHighPerformance() => SetActive(HighPerformanceGuid);
}

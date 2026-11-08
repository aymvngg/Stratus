using System.Text.RegularExpressions;

namespace Stratus.Core.Services;

public sealed record PowerPlan(Guid Guid, string Name, bool IsActive);

/// <summary>Wraps powercfg.exe - works on localized Windows because parsing is GUID-based.</summary>
public sealed class PowerPlanService
{
    public const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string UltimateTemplateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    private static readonly Regex PlanLine = new(
        @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\(([^)]*)\)\s*(\*)?\s*$",
        RegexOptions.Compiled);

    private readonly Func<string, ProcessResult> _run;

    public PowerPlanService() : this(arguments => ProcessRunner.Run("powercfg.exe", arguments)) { }

    internal PowerPlanService(Func<string, ProcessResult> run) => _run = run;

    public List<PowerPlan> ListPlans()
    {
        var result = _run("/list");
        if (!result.Success)
            throw new InvalidOperationException("Could not read power plans: " + result.StdErr.Trim());
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
        var result = _run("/getactivescheme");
        if (!result.Success)
            throw new InvalidOperationException("Could not read the active power plan: " + result.StdErr.Trim());
        var match = PlanLine.Match(result.StdOut.Trim());
        return match.Success && Guid.TryParse(match.Groups[1].Value, out var guid)
            ? new PowerPlan(guid, match.Groups[2].Value.Trim(), true)
            : ListPlans().FirstOrDefault(p => p.IsActive);
    }

    public bool SetActive(Guid guid)
    {
        var ok = _run($"/setactive {guid:D}").Success;
        if (ok)
            Log.Info("Power plan activated: " + guid);
        else
            Log.Warn("Power plan activation failed: " + guid);
        return ok;
    }

    public bool SetActive(string guidOrName)
    {
        var name = guidOrName.Trim();
        if (Guid.TryParse(name, out var guid))
            return SetActive(guid);

        var plan = ListPlans().FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        return plan is not null && SetActive(plan.Guid);
    }

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

        var dup = _run($"-duplicatescheme {UltimateTemplateGuid}");
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

        if (active.Guid == Guid.Parse(UltimateTemplateGuid))
            return true;

        var stored = settings.Get("UltimatePlanGuid");
        return stored is not null && Guid.TryParse(stored, out var g) && active.Guid == g;
    }

    public bool SetBalanced() => SetActive(BalancedGuid);
}

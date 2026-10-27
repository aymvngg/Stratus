using System.Text.Json;
using Stratus.Core.Models;

namespace Stratus.Core.Services;

/// <summary>One row of the import preview: what a profile asks for and what would happen here.</summary>
public sealed record ProfileAction(string Category, string Name, string Action, bool WillChange);

/// <summary>Outcome of applying a profile. Every failed item is listed, never swallowed.</summary>
public sealed record ProfileApplyReport(int Applied, int Skipped, int Failed, IReadOnlyList<string> Messages)
{
    public string Summary => Failed == 0
        ? $"Applied {Applied} items, skipped {Skipped} that already matched."
        : $"Applied {Applied} items, skipped {Skipped}, {Failed} failed. See the list below.";
}

/// <summary>
/// Captures the setup Stratus built on a machine into a portable JSON profile and applies
/// such a profile on another machine.
///
/// Applying is additive by design: it applies missing tweaks, disables the listed startup
/// entries and tasks, enables the listed features, installs the listed apps and switches the
/// saved DNS, power and TCP settings. It never reverts, re-enables or uninstalls anything, so
/// importing a profile cannot undo work that was done on the target machine.
/// </summary>
public sealed class ProfileService
{
    public const string CleanupSelectionKey = "cleanup.selection";

    private readonly TweakEngine _engine;
    private readonly StartupService _startup;
    private readonly ScheduledTaskService _tasks;
    private readonly OptionalFeatureService _features;
    private readonly WingetService _winget;
    private readonly DnsService _dns;
    private readonly PowerPlanService _power;
    private readonly SettingsStore _settings;

    public ProfileService(
        TweakEngine engine,
        StartupService startup,
        ScheduledTaskService tasks,
        OptionalFeatureService features,
        WingetService winget,
        DnsService dns,
        PowerPlanService power,
        SettingsStore settings)
    {
        _engine = engine;
        _startup = startup;
        _tasks = tasks;
        _features = features;
        _winget = winget;
        _dns = dns;
        _power = power;
        _settings = settings;
    }

    /// <summary>Reads the live state of everything Stratus manages into one profile.</summary>
    public async Task<StratusProfile> CaptureAsync()
    {
        var profile = new StratusProfile();

        foreach (var tweak in _engine.Tweaks)
        {
            if (SafeApplied(tweak))
                profile.Tweaks.Add(tweak.Id);
        }

        profile.StartupDisabled.AddRange(_startup.Scan()
            .Where(item => !item.IsEnabled)
            .Select(StartupService.TweakId));

        profile.TasksDisabled.AddRange(_tasks.Scan()
            .Where(task => !task.IsEnabled)
            .Select(TaskKey));

        profile.FeaturesEnabled.AddRange(_features.Scan()
            .Where(feature => feature.Kind == "Feature" && feature.IsInstalled)
            .Select(feature => feature.Name));

        var installed = await _winget.GetInstalledIdsAsync();
        profile.Apps.AddRange(AppCatalog.Apps
            .Where(app => installed.Contains(app.Id))
            .Select(app => app.Id));

        profile.CleanupTargets.AddRange(ReadCleanupSelection());

        profile.DnsProfile = _dns.GetActiveProfile()?.Id;

        var plan = _power.GetActivePlan();
        if (plan is not null)
            profile.PowerPlan = plan.Guid.ToString();

        foreach (var item in TcpSettingsService.Scan())
        {
            if (!string.Equals(item.CurrentValue, item.DefaultValue, StringComparison.OrdinalIgnoreCase))
                profile.TcpSettings[item.Id] = item.CurrentValue;
        }

        return profile;
    }

    /// <summary>Describes what applying the profile would do on this machine, without changing anything.</summary>
    public async Task<List<ProfileAction>> PreviewAsync(StratusProfile profile)
    {
        var plan = await BuildPlanAsync(profile);
        return plan.Select(step => new ProfileAction(step.Category, step.Name, step.Action, step.WillChange)).ToList();
    }

    /// <summary>Applies the profile and reports every item that was applied, skipped or failed.</summary>
    public async Task<ProfileApplyReport> ApplyAsync(StratusProfile profile, IProgress<string>? progress = null)
    {
        var plan = await BuildPlanAsync(profile);
        var messages = new List<string>();
        var applied = 0;
        var skipped = 0;
        var failed = 0;

        using (_engine.BeginBulk())
        {
            foreach (var step in plan)
            {
                if (!step.WillChange || step.Execute is null)
                {
                    skipped++;
                    continue;
                }

                progress?.Report(step.Category + ": " + step.Name);
                try
                {
                    var (ok, error) = await step.Execute();
                    if (ok)
                    {
                        applied++;
                        messages.Add(step.Category + ": " + step.Name + " - " + step.Action);
                    }
                    else
                    {
                        failed++;
                        var text = step.Category + ": " + step.Name + " failed - " + (error ?? "no error text");
                        messages.Add(text);
                        Log.Warn(text);
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    var text = step.Category + ": " + step.Name + " failed - " + ex.Message;
                    messages.Add(text);
                    Log.Warn(text);
                }
            }
        }

        Log.Info($"Profile applied: {applied} applied, {skipped} skipped, {failed} failed.");
        return new ProfileApplyReport(applied, skipped, failed, messages);
    }

    private sealed record Planned(
        string Category,
        string Name,
        string Action,
        bool WillChange,
        Func<Task<(bool Ok, string? Error)>>? Execute);

    private async Task<List<Planned>> BuildPlanAsync(StratusProfile profile)
    {
        var plan = new List<Planned>();

        foreach (var id in Distinct(profile.Tweaks))
        {
            var tweak = _engine.Find(id);
            if (tweak is null)
            {
                plan.Add(new Planned("Tweak", id, "Unknown to this version, skipped", false, null));
                continue;
            }

            if (SafeApplied(tweak))
            {
                plan.Add(new Planned("Tweak", tweak.Name, "Already applied", false, null));
                continue;
            }

            if (!SafeApplicable(tweak))
            {
                plan.Add(new Planned("Tweak", tweak.Name, "Not available on this machine, skipped", false, null));
                continue;
            }

            var target = tweak;
            plan.Add(new Planned("Tweak", tweak.Name, "Apply", true, async () =>
            {
                var result = await _engine.ApplyAsync(target);
                return (result.Success, result.Error);
            }));
        }

        var startup = _startup.Scan()
            .GroupBy(StartupService.TweakId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var id in Distinct(profile.StartupDisabled))
        {
            if (!startup.TryGetValue(id, out var item))
            {
                plan.Add(new Planned("Startup", id, "Not present on this machine, skipped", false, null));
                continue;
            }

            if (!item.IsEnabled)
            {
                plan.Add(new Planned("Startup", item.Name, "Already disabled", false, null));
                continue;
            }

            var target = item;
            plan.Add(new Planned("Startup", item.Name, "Disable", true,
                () => Task.FromResult(FromResult(_startup.SetEnabled(target, false)))));
        }

        var tasks = _tasks.Scan()
            .GroupBy(TaskKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var path in Distinct(profile.TasksDisabled))
        {
            if (!tasks.TryGetValue(path, out var item))
            {
                plan.Add(new Planned("Task", path, "Not present on this machine, skipped", false, null));
                continue;
            }

            if (!item.IsEnabled)
            {
                plan.Add(new Planned("Task", item.Name, "Already disabled", false, null));
                continue;
            }

            var target = item;
            plan.Add(new Planned("Task", item.Name, "Disable", true,
                () => Task.FromResult(FromResult(_tasks.SetEnabled(target, false)))));
        }

        var features = _features.Scan()
            .GroupBy(feature => feature.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var name in Distinct(profile.FeaturesEnabled))
        {
            if (!features.TryGetValue(name, out var item))
            {
                plan.Add(new Planned("Feature", name, "Not offered by Stratus, skipped", false, null));
                continue;
            }

            if (item.IsInstalled)
            {
                plan.Add(new Planned("Feature", item.DisplayName, "Already enabled", false, null));
                continue;
            }

            var target = item;
            plan.Add(new Planned("Feature", item.DisplayName, "Enable", true,
                () => Task.FromResult(FromResult(_features.Enable(target)))));
        }

        var installed = await _winget.GetInstalledIdsAsync();
        foreach (var id in Distinct(profile.Apps))
        {
            var app = AppCatalog.Apps.FirstOrDefault(entry =>
                string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));
            if (app is null)
            {
                plan.Add(new Planned("App", id, "Not in the app catalog, skipped", false, null));
                continue;
            }

            if (installed.Contains(id))
            {
                plan.Add(new Planned("App", app.Name, "Already installed", false, null));
                continue;
            }

            var target = app;
            plan.Add(new Planned("App", app.Name, "Install with winget", true, async () =>
            {
                var result = await _winget.InstallAsync(target);
                return (result.Success, result.Success ? null : result.Message);
            }));
        }

        if (!string.IsNullOrWhiteSpace(profile.DnsProfile))
        {
            var target = DnsService.Profiles.FirstOrDefault(entry =>
                string.Equals(entry.Id, profile.DnsProfile, StringComparison.OrdinalIgnoreCase));
            var active = _dns.GetActiveProfile();
            if (target is null)
            {
                plan.Add(new Planned("DNS", profile.DnsProfile, "Unknown DNS profile, skipped", false, null));
            }
            else if (active is not null && string.Equals(active.Id, target.Id, StringComparison.OrdinalIgnoreCase))
            {
                plan.Add(new Planned("DNS", target.Label, "Already active", false, null));
            }
            else
            {
                var chosen = target;
                plan.Add(new Planned("DNS", chosen.Label, "Switch DNS", true, () =>
                {
                    var (ok, message) = _dns.Apply(chosen);
                    return Task.FromResult<(bool Ok, string? Error)>((ok, ok ? null : message));
                }));
            }
        }

        if (!string.IsNullOrWhiteSpace(profile.PowerPlan))
        {
            var saved = profile.PowerPlan;
            var active = _power.GetActivePlan();
            var matches = active is not null
                && (string.Equals(active.Guid.ToString(), saved, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(active.Name, saved, StringComparison.OrdinalIgnoreCase));
            if (matches)
                plan.Add(new Planned("Power", active!.Name, "Already active", false, null));
            else
                plan.Add(new Planned("Power", saved, "Switch power plan", true,
                    () => Task.FromResult(ApplyPowerPlan(saved))));
        }

        var tcp = TcpSettingsService.Scan()
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var (id, value) in profile.TcpSettings)
        {
            if (!tcp.TryGetValue(id, out var item))
            {
                plan.Add(new Planned("TCP", id, "Unknown setting, skipped", false, null));
                continue;
            }

            if (string.Equals(item.CurrentValue, value, StringComparison.OrdinalIgnoreCase))
            {
                plan.Add(new Planned("TCP", item.Name, "Already set", false, null));
                continue;
            }

            var target = item;
            plan.Add(new Planned("TCP", item.Name, "Set to " + value, true,
                () => Task.FromResult(FromResult(TcpSettingsService.Apply(target, value)))));
        }

        var wanted = string.Join(",", Distinct(profile.CleanupTargets));
        var current = string.Join(",", ReadCleanupSelection());
        if (string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase))
        {
            plan.Add(new Planned("Cleanup", "Cleanup selection", "Already matches", false, null));
        }
        else
        {
            plan.Add(new Planned("Cleanup", "Cleanup selection",
                "Restore the ticked targets (nothing is deleted)", true, () =>
                {
                    _settings.Set(CleanupSelectionKey, wanted);
                    return Task.FromResult<(bool Ok, string? Error)>((true, null));
                }));
        }

        return plan;
    }

    private (bool Ok, string? Error) ApplyPowerPlan(string value)
    {
        if (Guid.TryParse(value, out var guid) && _power.SetActive(guid))
            return (true, null);

        if (_power.SetActive(value))
            return (true, null);

        return (false, "That power plan does not exist on this machine.");
    }

    private static IEnumerable<string> Distinct(IEnumerable<string> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string TaskKey(ScheduledTaskItem task) => task.Folder + "\\" + task.Name;

    private static (bool Ok, string? Error) FromResult(TweakResult result) => (result.Success, result.Error);

    private static (bool Ok, string? Error) FromResult((bool Success, string Message) result) =>
        (result.Success, result.Success ? null : result.Message);

    private List<string> ReadCleanupSelection()
    {
        var raw = _settings.Get(CleanupSelectionKey);
        if (string.IsNullOrWhiteSpace(raw))
            return new List<string>();

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static bool SafeApplied(Tweak tweak)
    {
        try
        {
            return tweak.IsApplied;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the state of " + tweak.Id + ": " + ex.Message);
            return false;
        }
    }

    private static bool SafeApplicable(Tweak tweak)
    {
        try
        {
            return tweak.IsApplicable;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not check whether " + tweak.Id + " applies here: " + ex.Message);
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize(StratusProfile profile) => JsonSerializer.Serialize(profile, JsonOptions);

    public static (StratusProfile? Profile, string? Error) Deserialize(string json)
    {
        try
        {
            var profile = JsonSerializer.Deserialize<StratusProfile>(json, JsonOptions);
            if (profile is null)
                return (null, "The file does not contain a Stratus profile.");

            if (profile.Version > StratusProfile.CurrentVersion)
                return (null, "This profile was written by a newer Stratus (version "
                    + profile.Version + "). Update Stratus and try again.");

            return (profile, null);
        }
        catch (JsonException ex)
        {
            return (null, "That file is not a valid Stratus profile: " + ex.Message);
        }
    }

    public static string DefaultExportPath() => Path.Combine(
        AppPaths.ProfilesDir,
        "stratus-profile-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
}

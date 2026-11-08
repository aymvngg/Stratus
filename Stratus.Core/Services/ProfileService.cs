using System.Text.Json;
using Stratus.Core.Models;

namespace Stratus.Core.Services;

/// <summary>One row of the import preview: what a profile asks for and what would happen here.</summary>
public sealed record ProfileAction(string Category, string Name, string Action, bool WillChange);

/// <summary>Result of checking a profile's shape before it is previewed or applied.</summary>
public sealed record ProfileValidation(bool Valid, string? Error)
{
    public static ProfileValidation Ok { get; } = new(true, null);

    public static ProfileValidation Fail(string error) => new(false, error);
}

/// <summary>A profile that cannot be captured, previewed or applied. The message is ready for the UI.</summary>
public sealed class ProfileValidationException : Exception
{
    public ProfileValidationException(string message) : base(message) { }
}

/// <summary>Outcome of applying a profile. Every failed item is listed, never swallowed.</summary>
public sealed record ProfileApplyReport(int Applied, int Skipped, int Failed, IReadOnlyList<string> Messages)
{
    public string Summary => Failed == 0
        ? $"Applied {Applied} items, skipped {Skipped}."
        : $"Applied {Applied} items, skipped {Skipped}, {Failed} failed.";
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

        // A failed export looks like "no apps installed" and would write a profile that
        // silently drops every app the user picked, so fail the capture instead.
        HashSet<string> installed;
        try
        {
            installed = await _winget.GetInstalledIdsAsync();
        }
        catch (WingetReadException ex)
        {
            Log.Warn("Profile capture stopped: " + ex.Message);
            throw new ProfileValidationException(
                "The installed app list could not be read, so the profile was not saved. "
                + "It would be missing every app Stratus can install. " + ex.Message);
        }

        foreach (var tweak in _engine.Tweaks)
        {
            if (tweak.IsApplicable && tweak.IsApplied)
                profile.Tweaks.Add(tweak.Id);
        }

        profile.StartupDisabled.AddRange(_startup.Scan(requireComplete: true)
            .Where(item => !item.IsEnabled)
            .Select(StartupService.TweakId));

        profile.TasksDisabled.AddRange(_tasks.Scan(requireComplete: true)
            .Where(task => !task.IsEnabled)
            .Select(TaskKey));

        profile.FeaturesEnabled.AddRange(_features.Scan(requireComplete: true)
            .Where(feature => feature.Kind == "Feature" && feature.IsInstalled)
            .Select(feature => feature.Name));

        profile.Apps.AddRange(AppCatalog.Apps
            .Where(app => installed.Contains(app.Id))
            .Select(app => app.Id));

        profile.CleanupTargets.AddRange(ReadCleanupSelection());

        var dns = _dns.GetActiveProfile();
        profile.DnsProfile = dns?.Id == "custom" ? null : dns?.Id;

        var plan = _power.GetActivePlan();
        if (plan is not null)
            profile.PowerPlan = plan.Guid.ToString();

        foreach (var item in TcpSettingsService.Scan())
        {
            if (!string.Equals(item.CurrentValue, item.DefaultValue, StringComparison.OrdinalIgnoreCase))
                profile.TcpSettings[item.Id] = item.CurrentValue;
        }

        var check = Validate(profile);
        if (!check.Valid)
            throw new ProfileValidationException(check.Error ?? "The captured profile is not valid.");

        return profile;
    }

    /// <summary>Describes what applying the profile would do on this machine, without changing anything.</summary>
    public async Task<List<ProfileAction>> PreviewAsync(StratusProfile profile)
    {
        RequireValid(profile);
        var plan = await BuildPlanAsync(profile);
        return plan.Select(step => new ProfileAction(step.Category, step.Name, step.Action, step.WillChange)).ToList();
    }

    /// <summary>Applies the profile and reports every item that was applied, skipped or failed.</summary>
    public async Task<ProfileApplyReport> ApplyAsync(StratusProfile profile, IProgress<string>? progress = null)
    {
        RequireValid(profile);
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
            var tweak = _engine.Tweaks.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
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

        var startup = (profile.StartupDisabled.Count == 0 ? [] : _startup.Scan(requireComplete: true))
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

        var tasks = (profile.TasksDisabled.Count == 0 ? [] : _tasks.Scan(requireComplete: true))
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

        var features = (profile.FeaturesEnabled.Count == 0 ? [] : _features.Scan(requireComplete: true))
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

        // The winget export is slow (up to two minutes); skip it when the profile
        // wants no apps at all.
        var wantedApps = Distinct(profile.Apps).ToList();
        HashSet<string> installedApps;
        try
        {
            installedApps = !wantedApps.Any(id => AppCatalog.Apps.Any(app => string.Equals(app.Id, id, StringComparison.OrdinalIgnoreCase)))
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : await _winget.GetInstalledIdsAsync();
        }
        catch (WingetReadException ex)
        {
            // Without the installed set every app would look absent and be reinstalled.
            Log.Warn("Profile plan stopped: " + ex.Message);
            throw new ProfileValidationException(
                "The installed app list could not be read, so the profile was not used. "
                + "Every app would look missing and be installed again. " + ex.Message);
        }
        foreach (var id in wantedApps)
        {
            var app = AppCatalog.Apps.FirstOrDefault(entry =>
                string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));
            if (app is null)
            {
                plan.Add(new Planned("App", id, "Not in the app catalog, skipped", false, null));
                continue;
            }

            if (installedApps.Contains(id))
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

        var tcp = (profile.TcpSettings.Count == 0 ? [] : TcpSettingsService.Scan())
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var (id, value) in profile.TcpSettings)
        {
            if (!tcp.TryGetValue(id, out var item))
            {
                plan.Add(new Planned("TCP", id, "Unknown setting, skipped", false, null));
                continue;
            }

            if (!item.Values.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                plan.Add(new Planned("TCP", item.Name, "Unsupported value, skipped", false, null));
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

        var current = ReadCleanupSelection();
        var combined = current.Concat(Distinct(profile.CleanupTargets))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (combined.Count == current.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            plan.Add(new Planned("Cleanup", "Cleanup selection", "Already matches", false, null));
        }
        else
        {
            plan.Add(new Planned("Cleanup", "Cleanup selection",
                "Add the ticked targets (nothing is deleted)", true, () =>
                {
                    var saved = _settings.TrySet(CleanupSelectionKey, string.Join(",", combined));
                    return Task.FromResult<(bool Ok, string? Error)>((saved,
                        saved ? null : "The cleanup selection could not be saved."));
                }));
        }

        return plan;
    }

    private (bool Ok, string? Error) ApplyPowerPlan(string value) =>
        _power.SetActive(value)
            ? (true, null)
            : (false, "That power plan does not exist on this machine.");

    private static IEnumerable<string> Distinct(IEnumerable<string>? values) => (values ?? [])
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase);

    private static void RequireValid(StratusProfile? profile)
    {
        var check = Validate(profile);
        if (!check.Valid)
            throw new ProfileValidationException(check.Error ?? "That profile is not valid.");
    }

    private static string TaskKey(ScheduledTaskItem task) => task.Folder + "\\" + task.Name;

    private static (bool Ok, string? Error) FromResult(TweakResult result) => (result.Success, result.Error);

    private static (bool Ok, string? Error) FromResult((bool Success, string Message) result) =>
        (result.Success, result.Success ? null : result.Message);

    private List<string> ReadCleanupSelection()
    {
        // One parser for the persisted format, shared with the Cleanup page. A missing
        // setting and an empty string both mean "nothing ticked" here: capture writes an
        // empty list either way, and an import only ever adds targets.
        var parsed = Coordination.CleanupSelection.Parse(_settings.Get(CleanupSelectionKey));
        return parsed?.ToList() ?? [];
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

    /// <summary>
    /// Checks a profile's shape and version before anything reads its contents. A profile
    /// read with an explicit JSON null for a list would otherwise count or plan against null.
    /// </summary>
    public static ProfileValidation Validate(StratusProfile? profile)
    {
        if (profile is null)
            return ProfileValidation.Fail("The file does not contain a Stratus profile.");

        if (profile.Version < 1)
            return ProfileValidation.Fail(Invalid("the version is missing or not a positive number"));

        if (profile.Version > StratusProfile.CurrentVersion)
            return ProfileValidation.Fail("This profile was written by a newer Stratus (version "
                + profile.Version + "). Update Stratus and try again.");

        var listError =
            CheckList(profile.Tweaks, "tweaks")
            ?? CheckList(profile.StartupDisabled, "startupDisabled")
            ?? CheckList(profile.TasksDisabled, "tasksDisabled")
            ?? CheckList(profile.FeaturesEnabled, "featuresEnabled")
            ?? CheckList(profile.Apps, "apps")
            ?? CheckList(profile.CleanupTargets, "cleanupTargets");
        if (listError is not null)
            return ProfileValidation.Fail(listError);

        if (profile.TcpSettings is null)
            return ProfileValidation.Fail(Invalid("tcpSettings is null"));

        foreach (var (key, value) in profile.TcpSettings)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                return ProfileValidation.Fail(Invalid("tcpSettings contains a blank key or value"));
        }

        return ProfileValidation.Ok;
    }

    private static string? CheckList(List<string>? values, string name)
    {
        if (values is null)
            return Invalid(name + " is null");

        if (values.Any(string.IsNullOrWhiteSpace))
            return Invalid(name + " contains a blank entry");

        return null;
    }

    private static string Invalid(string detail) =>
        "That file is not a valid Stratus profile: " + detail + ".";

    public static (StratusProfile? Profile, string? Error) Deserialize(string json)
    {
        try
        {
            var profile = JsonSerializer.Deserialize<StratusProfile>(json, JsonOptions);
            if (profile is null)
                return (null, "The file does not contain a Stratus profile.");

            var check = Validate(profile);
            if (!check.Valid)
                return (null, check.Error);

            // A null machine name is harmless but prints as an empty name in the preview.
            profile.MachineName = string.IsNullOrWhiteSpace(profile.MachineName)
                ? "unknown"
                : profile.MachineName.Trim();

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

namespace Stratus.Core.Models;

/// <summary>
/// A portable snapshot of the setup Stratus built on a machine: applied tweaks, disabled
/// startup entries and scheduled tasks, enabled Windows features, installed catalog apps,
/// and the chosen DNS, power and TCP settings.
///
/// Importing a profile only adds or applies. It never reverts a tweak, re-enables an entry,
/// uninstalls an app or removes a feature, so loading someone else's profile cannot undo work.
/// </summary>
public sealed class StratusProfile
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;
    public string MachineName { get; set; } = Environment.MachineName;

    /// <summary>Ids of the tweaks that were applied when the profile was exported.</summary>
    public List<string> Tweaks { get; set; } = new();

    /// <summary>Startup entry ids (see StartupService.TweakId) that were disabled.</summary>
    public List<string> StartupDisabled { get; set; } = new();

    /// <summary>Task paths (folder + name) that were disabled.</summary>
    public List<string> TasksDisabled { get; set; } = new();

    /// <summary>Windows feature names that were enabled.</summary>
    public List<string> FeaturesEnabled { get; set; } = new();

    /// <summary>Winget ids of catalog apps that were installed.</summary>
    public List<string> Apps { get; set; } = new();

    /// <summary>Cleanup target ids that were ticked in the Cleanup page.</summary>
    public List<string> CleanupTargets { get; set; } = new();

    /// <summary>DnsService profile id, or null when DNS was left alone.</summary>
    public string? DnsProfile { get; set; }

    /// <summary>Power plan GUID, or the plan name when the GUID no longer exists.</summary>
    public string? PowerPlan { get; set; }

    /// <summary>TcpSettingsService id to the value that was set.</summary>
    public Dictionary<string, string> TcpSettings { get; set; } = new();

    public int ItemCount => Tweaks.Count + StartupDisabled.Count + TasksDisabled.Count
        + FeaturesEnabled.Count + Apps.Count + CleanupTargets.Count + TcpSettings.Count
        + (DnsProfile is null ? 0 : 1) + (PowerPlan is null ? 0 : 1);
}

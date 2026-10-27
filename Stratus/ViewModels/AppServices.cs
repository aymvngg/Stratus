using Microsoft.UI.Dispatching;
using Stratus.Core;
using Stratus.Core.Services;
using Stratus.Core.Tweaks;

namespace Stratus.ViewModels;

/// <summary>Composition root - one instance of every service, shared by all pages/VMs.</summary>
public static class AppServices
{
    public static readonly BackupStore Backup = new(AppPaths.BackupFile);
    public static readonly HistoryStore History = new(AppPaths.HistoryFile);
    public static readonly ScoreHistoryStore ScoreHistory = new(AppPaths.ScoreHistoryFile);
    public static readonly RegistryService Registry = new(Backup);
    public static readonly SettingsStore Settings = new(AppPaths.SettingsFile);
    public static readonly PowerPlanService Power = new();

    public static readonly TweakEngine Engine = new(TweakCatalog.Build(Registry, Power, Settings, Backup), History);

    public static readonly CleanupService Cleanup = new();
    public static readonly DebloatService Debloat = new();
    public static readonly OptionalFeatureService OptionalFeatures = new();
    public static readonly DnsService Dns = new();
    public static readonly StartupService Startup = new(Registry);
    public static readonly ScheduledTaskService ScheduledTasks = new(AppPaths.TasksFile);
    public static readonly WingetService Winget = new();

    /// <summary>
    /// One engine snapshot for every listener: StateChanged, bulk operations and the Tools page
    /// plan switch all funnel here, so each tweak's registry state is read once per change.
    /// </summary>
    public static async Task RefreshFromEngineAsync()
    {
        var (report, plan) = await Task.Run(() => (Engine.Rescore(), Power.GetActivePlan()));
        OnUi(() =>
        {
            Vm.Dashboard.ApplySnapshot(report, plan?.Name);
            Vm.Tweaks.ApplyStates(report.States);
            Vm.History.RequestRefresh();
        });
    }

    /// <summary>Set on the UI thread at launch; lets background events marshal back.</summary>
    public static DispatcherQueue? Dispatcher { get; set; }

    public static void OnUi(Action action)
    {
        if (Dispatcher is { } d)
            d.TryEnqueue(() => action());
        else
            action();
    }
}

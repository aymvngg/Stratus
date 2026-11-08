using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Stratus.Core;
using Stratus.Core.Coordination;
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

    // After Winget: field initializers run in declaration order and Profiles needs every service above.
    public static readonly ProfileService Profiles = new(Engine, Startup, ScheduledTasks, OptionalFeatures, Winget, Dns, Power, Settings);

    /// <summary>
    /// One UI writer owns tweak changes at a time. Bulk runs on one page and a row toggle on
    /// another would otherwise interleave writes and each overwrite the other's snapshot.
    /// </summary>
    public static readonly OperationGate TweakWrites = new();

    /// <summary>
    /// Orders engine snapshots. A change advances the revision; long reads capture the
    /// current value and discard their result when a change landed meanwhile, so a stale
    /// scan cannot overwrite a state a user just changed.
    /// </summary>
    public static readonly ChangeRevision StateRevision = new();

    /// <summary>
    /// One engine snapshot for every listener: StateChanged, bulk operations and the Tools page
    /// plan switch all funnel here, so each tweak's registry state is read once per change.
    /// A snapshot that lost the race with a newer change is dropped instead of rendered.
    /// </summary>
    public static async Task RefreshFromEngineAsync()
    {
        var revision = StateRevision.Advance();
        try
        {
            var (report, plan) = await Task.Run(() => (Engine.Rescore(), Power.GetActivePlan()));
            if (!StateRevision.IsCurrent(revision))
                return;

            OnUi(() =>
            {
                if (!StateRevision.IsCurrent(revision))
                    return;

                Vm.Dashboard.ApplySnapshot(report, plan?.Name);
                Vm.Tweaks.ApplyStates(report.States);
                Vm.History.RequestRefresh();
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Refreshing the engine state failed: " + ex.Message);
            OnUi(() => Vm.Dashboard.ShowStatus(
                $"Could not refresh the system state: {ex.Message}", InfoBarSeverity.Warning));
        }
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

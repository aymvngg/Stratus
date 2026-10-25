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
    public static readonly RegistryService Registry = new(Backup);
    public static readonly SettingsStore Settings = new(AppPaths.SettingsFile);
    public static readonly PowerPlanService Power = new();

    public static readonly TweakEngine Engine = new(TweakCatalog.Build(Registry, Power, Settings, Backup), History);

    public static readonly CleanupService Cleanup = new();
    public static readonly DebloatService Debloat = new();
    public static readonly DnsService Dns = new();

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

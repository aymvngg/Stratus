using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Stratus.Core.Models;
using Stratus.Core.Services;

namespace Stratus.ViewModels;

public sealed class ToolsViewModel : ObservableObject
{
    public ObservableCollection<PowerPlan> Plans { get; } = new();
    public ObservableCollection<string> Adapters { get; } = new();

    private PowerPlan? _selectedPlan;
    private DnsProfile? _selectedDns;
    private DnsProfile? _currentDns;
    private string _activePlanText = "…";
    private bool _isBusy;
    private string _busyText = "";
    private string _statusMessage = "";
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    private bool _statusOpen;

    public PowerPlan? SelectedPlan
    {
        get => _selectedPlan;
        set => Set(ref _selectedPlan, value);
    }

    public DnsProfile? SelectedDns
    {
        get => _selectedDns;
        set => Set(ref _selectedDns, value);
    }

    public DnsProfile? CurrentDns
    {
        get => _currentDns;
        private set => Set(ref _currentDns, value);
    }

    public IReadOnlyList<DnsProfile> DnsProfiles => DnsService.Profiles;

    public string ActivePlanText { get => _activePlanText; private set => Set(ref _activePlanText, value); }
    public string AdaptersText => Adapters.Count == 0 ? "no connected physical adapters found" : string.Join(", ", Adapters);

    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public string BusyText { get => _busyText; private set => Set(ref _busyText, value); }

    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public InfoBarSeverity StatusSeverity { get => _statusSeverity; private set => Set(ref _statusSeverity, value); }
    public bool StatusOpen { get => _statusOpen; set => Set(ref _statusOpen, value); }

    public void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
        StatusOpen = true;
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        BusyText = "Reading system state…";
        try
        {
            var plans = await Task.Run(() => AppServices.Power.ListPlans());
            var adapters = await Task.Run(() => AppServices.Dns.GetConnectedAdapters());

            Plans.Clear();
            foreach (var plan in plans)
                Plans.Add(plan);

            Adapters.Clear();
            foreach (var adapter in adapters)
                Adapters.Add(adapter);

            var active = plans.FirstOrDefault(p => p.IsActive);
            SelectedPlan = active ?? plans.FirstOrDefault();
            ActivePlanText = active is null ? "Unknown" : $"{active.Name} ({active.Guid:B})";
            Raise(nameof(AdaptersText));
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    public async Task<string> ApplyPlanAsync()
    {
        if (SelectedPlan is null)
            return "Pick a plan first.";

        IsBusy = true;
        BusyText = "Switching power plan…";
        try
        {
            var ok = await Task.Run(() => AppServices.Power.SetActive(SelectedPlan.Guid));
            if (!ok)
                return "powercfg failed to switch the plan.";

            ActivePlanText = $"{SelectedPlan.Name} ({SelectedPlan.Guid:B})";
            // The power-ultimate tweak's state depends on the active plan.
            Vm.Dashboard.RequestScoreRefresh();
            Vm.Tweaks.RequestStateRefresh();
            return $"Active plan: {SelectedPlan.Name}.";
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    public async Task<string> ApplyDnsAsync()
    {
        if (SelectedDns is null)
            return "Pick a DNS provider first.";

        IsBusy = true;
        BusyText = "Applying DNS…";
        try
        {
            var (ok, message) = await Task.Run(() => AppServices.Dns.Apply(SelectedDns));
            if (ok)
                CurrentDns = SelectedDns;
            return message;
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    public async Task<string> CreateRestorePointAsync()
    {
        IsBusy = true;
        BusyText = "Creating restore point (can take a minute)…";
        try
        {
            var (ok, message) = await Task.Run(() => RestorePointService.Create());
            return ok ? "Restore point created." : $"Restore point failed: {message}";
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    /// <summary>Reverts every applied tweak back to its default (Windows) state.</summary>
    public async Task<string> RevertAllAsync()
    {
        IsBusy = true;
        BusyText = "Reverting tweaks…";
        try
        {
            var toRevert = AppServices.Engine.Tweaks
                .Where(t => Safe(() => t.IsApplicable, false) && Safe(() => t.IsApplied, false))
                .ToList();

            var reverted = 0;
            var failed = new List<string>();
            foreach (var tweak in toRevert)
            {
                var result = await AppServices.Engine.RevertAsync(tweak);
                if (result.Success)
                    reverted++;
                else
                    failed.Add($"{tweak.Id}: {result.Error}");
            }

            Vm.Dashboard.RequestScoreRefresh();
            Vm.Tweaks.RequestStateRefresh();

            return failed.Count == 0
                ? $"Reverted {Format.Count(reverted, "tweak")} to default settings."
                : $"Reverted {reverted}, {failed.Count} failed - {string.Join("; ", failed.Take(3))}";
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }
}

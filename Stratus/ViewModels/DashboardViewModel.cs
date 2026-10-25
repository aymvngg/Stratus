using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Stratus.Core.Models;
using Stratus.Core.Services;

namespace Stratus.ViewModels;

public sealed record CategoryRow(string Title, string Detail, int Score, Brush Brush);

public sealed class DashboardViewModel : ObservableObject
{
    private ScoreReport? _report;
    private SystemSummary? _summary;
    private string _powerPlan = "…";
    private bool _isBusy;
    private string _busyText = "";
    private string _statusMessage = "";
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    private bool _statusOpen;
    private int _refreshRequests;
    private int _pending;

    public ScoreReport? Report
    {
        get => _report;
        private set
        {
            if (Set(ref _report, value))
                Raise(nameof(Score), nameof(StatusText), nameof(AppliedText), nameof(ScoreBrush),
                      nameof(AppliedCountText), nameof(ScoreCaption));
        }
    }

    public int Score => Report?.Score ?? 0;
    public string StatusText => Report?.StatusText ?? "—";
    public string AppliedText => Report is null ? "Scanning…" : $"{Report.AppliedCount} of {Report.TotalCount} applicable tweaks applied";
    public string AppliedCountText => Report is null ? "…" : $"{Report.AppliedCount} / {Report.TotalCount}";
    public string ScoreCaption => Report is null ? "Scanning the system…" : Report.StatusText;

    /// <summary>Ring + accent colour that tracks health: green → blue → amber → red.</summary>
    public Brush ScoreBrush => RiskStyles.HealthBrush(Score);

    public string Cpu => _summary?.Cpu ?? "…";
    public string Gpu => _summary?.Gpu ?? "…";
    public string Ram => _summary?.Ram ?? "…";
    public string Os => _summary is null ? "…" : $"{_summary.Os} ({_summary.OsBuild})";
    public string ElevatedText => _summary is null ? "…" : _summary.IsElevated ? "Administrator" : "Standard (limited)";
    public string PowerPlan => _powerPlan;

    public int PendingRecommended => _pending;
    public string PendingText => _pending == 0
        ? "All recommended tweaks applied"
        : $"{_pending} recommended tweak{(_pending == 1 ? "" : "s")} pending";

    public ObservableCollection<CategoryRow> Categories { get; } = new();

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

    public void RequestScoreRefresh() => _ = DebouncedScoreAsync();

    private async Task DebouncedScoreAsync()
    {
        var token = Interlocked.Increment(ref _refreshRequests);
        await Task.Delay(400);
        if (Volatile.Read(ref _refreshRequests) != token)
            return;
        await RefreshScoreAsync();
    }

    public async Task RefreshScoreAsync()
    {
        var (report, pending) = await Task.Run(() => (AppServices.Engine.Rescore(), PendingRecommendedCount()));
        _pending = pending;
        ApplyReport(report);
        Raise(nameof(PendingRecommended), nameof(PendingText));
    }

    public async Task RescanAsync()
    {
        IsBusy = true;
        BusyText = "Reading system state…";
        try
        {
            var reportTask = Task.Run(() => AppServices.Engine.Rescore());
            var summaryTask = Task.Run(() => new SystemSummary(
                SystemInfoService.GetCpu(),
                SystemInfoService.GetGpu(),
                SystemInfoService.GetRam(),
                SystemInfoService.GetOsDisplayName(),
                SystemInfoService.GetOsBuild(),
                SystemInfoService.IsElevated()));
            var planTask = Task.Run(() => AppServices.Power.GetActivePlan());
            var pendingTask = Task.Run(PendingRecommendedCount);

            ApplyReport(await reportTask);
            _pending = await pendingTask;
            _summary = await summaryTask;
            _powerPlan = (await planTask)?.Name ?? "Unknown";
            Raise(nameof(Cpu), nameof(Gpu), nameof(Ram), nameof(Os), nameof(ElevatedText), nameof(PowerPlan),
                  nameof(PendingRecommended), nameof(PendingText));
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    private void ApplyReport(ScoreReport report)
    {
        Report = report;
        Categories.Clear();
        foreach (var c in report.Categories.Where(c => c.TotalCount > 0))
        {
            Categories.Add(new CategoryRow(
                c.Category.ToString(),
                $"{c.AppliedCount}/{c.TotalCount} applied",
                c.Score,
                RiskStyles.HealthBrush(c.Score)));
        }
    }

    /// <summary>Number of pending tweaks "Apply recommended" would touch (Safe + Recommended risk).</summary>
    public int PendingRecommendedCount()
    {
        var count = 0;
        foreach (var t in AppServices.Engine.Tweaks)
        {
            if (t.Risk > RiskLevel.Recommended)
                continue;
            try
            {
                if (t.IsApplicable && !t.IsApplied)
                    count++;
            }
            catch { /* unreadable state — skip */ }
        }
        return count;
    }

    public async Task<string> ApplyRecommendedAsync(bool createRestorePoint)
    {
        IsBusy = true;
        try
        {
            if (createRestorePoint)
            {
                BusyText = "Creating restore point (can take a minute)…";
                var (ok, message) = await Task.Run(() => RestorePointService.Create());
                if (!ok)
                    return $"Restore point failed: {message} — nothing was changed.";
            }

            BusyText = "Applying tweaks…";
            var results = await AppServices.Engine.ApplyBulkAsync(RiskLevel.Recommended);
            var failed = results.Where(kv => !kv.Value.Success).ToList();
            var applied = results.Count - failed.Count;

            await RefreshScoreAsync();

            return failed.Count == 0
                ? $"Applied {Format.Count(applied, "tweak")}. Score: {Score}/100."
                : $"Applied {Format.Count(applied, "tweak")}, {failed.Count} failed — {string.Join("; ", failed.Take(3).Select(f => $"{f.Key}: {f.Value.Error}"))}";
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
}

public static class RiskStyles
{
    public static (SolidColorBrush Foreground, SolidColorBrush Background) For(RiskLevel risk) => risk switch
    {
        RiskLevel.Safe => (Brush(0xFF4CC38A), Brush(0x334CC38A)),
        RiskLevel.Recommended => (Brush(0xFF60CDFF), Brush(0x3360CDFF)),
        _ => (Brush(0xFFFFB900), Brush(0x33FFB900)),
    };

    /// <summary>Score → colour ramp used by the ring, bars and category rows.</summary>
    public static SolidColorBrush HealthBrush(int score) => score switch
    {
        >= 85 => Brush(0xFF4CC38A),
        >= 60 => Brush(0xFF60CDFF),
        >= 35 => Brush(0xFFFFB900),
        _ => Brush(0xFFF1707B),
    };

    private static SolidColorBrush Brush(uint argb) => new(Microsoft.UI.ColorHelper.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
}

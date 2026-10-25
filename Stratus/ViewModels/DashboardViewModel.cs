using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Stratus.Core.Models;
using Stratus.Core.Services;
using Stratus.Core.Tweaks;

namespace Stratus.ViewModels;

public sealed record CategoryRow(string Title, string Detail, int Score, Brush Brush);

/// <summary>One preset card on the Dashboard. Recomputed on every refresh, detection-driven like everything else.</summary>
public sealed record PresetRow(Preset Preset, string Name, string Description, string Glyph, int Applied, int Total)
{
    public bool Complete => Total > 0 && Applied == Total;
    public bool CanApply => Total > 0 && !Complete;
    public string ProgressText => Total == 0 ? "Nothing applicable on this machine"
        : Complete ? "Fully applied"
        : $"{Applied} of {Total} applied";
    public int Percent => Total == 0 ? 0 : (int)Math.Round(Applied * 100.0 / Total);
}

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
    private IReadOnlyList<ScoreSnapshot> _scoreHistory = Array.Empty<ScoreSnapshot>();

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
    public string StatusText => Report?.StatusText ?? "-";
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

    /// <summary>Snapshots behind the Dashboard graph, oldest first.</summary>
    public IReadOnlyList<ScoreSnapshot> ScoreHistory
    {
        get => _scoreHistory;
        private set
        {
            if (Set(ref _scoreHistory, value))
                Raise(nameof(HasHistory), nameof(GraphSubtitle));
        }
    }

    public bool HasHistory => _scoreHistory.Count >= 2;

    public string GraphSubtitle
    {
        get
        {
            if (_scoreHistory.Count == 0)
                return "A point is recorded every time the score changes.";
            var first = _scoreHistory[0].TimestampUtc.ToLocalTime();
            var last = _scoreHistory[^1].TimestampUtc.ToLocalTime();
            var delta = _scoreHistory[^1].Score - _scoreHistory[0].Score;
            var deltaText = delta == 0
                ? "no change since the first snapshot"
                : $"{delta:+0;-0} since the first snapshot";
            var range = first.ToString("MMM d") == last.ToString("MMM d")
                ? first.ToString("MMM d")
                : $"{first:MMM d} - {last:MMM d}";
            return $"{_scoreHistory.Count} point{(_scoreHistory.Count == 1 ? "" : "s")} · {range} · {deltaText}";
        }
    }

    public ObservableCollection<CategoryRow> Categories { get; } = new();
    public ObservableCollection<PresetRow> Presets { get; } = new();

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
        var (report, pending, presets) = await Task.Run(() => (AppServices.Engine.Rescore(), PendingRecommendedCount(), BuildPresetRows()));
        _pending = pending;
        ApplyReport(report);
        ScoreHistory = await Task.Run(() => RecordAndReadHistory(report.Score));
        SyncPresets(presets);
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
            var presetTask = Task.Run(BuildPresetRows);

            var report = await reportTask;
            ApplyReport(report);
            ScoreHistory = await Task.Run(() => RecordAndReadHistory(report.Score));
            _pending = await pendingTask;
            _summary = await summaryTask;
            _powerPlan = (await planTask)?.Name ?? "Unknown";
            SyncPresets(await presetTask);
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

    /// <summary>Persists a point when the score moved, then returns the full graph data. Never throws.</summary>
    private IReadOnlyList<ScoreSnapshot> RecordAndReadHistory(int score)
    {
        try { AppServices.ScoreHistory.AppendIfChanged(score); } catch { /* the graph is cosmetic */ }
        try { return AppServices.ScoreHistory.All(); }
        catch { return Array.Empty<ScoreSnapshot>(); }
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
            catch { /* unreadable state - skip */ }
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
                    return $"Restore point failed: {message} - nothing was changed.";
            }

            BusyText = "Applying tweaks…";
            var results = await AppServices.Engine.ApplyBulkAsync(RiskLevel.Recommended);
            var failed = results.Where(kv => !kv.Value.Success).ToList();
            var applied = results.Count - failed.Count;

            await RefreshScoreAsync();

            return failed.Count == 0
                ? $"Applied {Format.Count(applied, "tweak")}. Score: {Score}/100."
                : $"Applied {Format.Count(applied, "tweak")}, {failed.Count} failed - {string.Join("; ", failed.Take(3).Select(f => $"{f.Key}: {f.Value.Error}"))}";
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    private void SyncPresets(List<PresetRow> rows)
    {
        Presets.Clear();
        foreach (var row in rows)
            Presets.Add(row);
    }

    /// <summary>Per-preset progress read from live system state (unknown and n/a tweaks excluded).</summary>
    private List<PresetRow> BuildPresetRows()
    {
        var rows = new List<PresetRow>();
        foreach (var preset in PresetCatalog.All)
        {
            var total = 0;
            var applied = 0;
            foreach (var id in preset.TweakIds)
            {
                var t = AppServices.Engine.Find(id);
                if (t is null || !t.IsApplicable)
                    continue;
                total++;
                bool a;
                try { a = t.IsApplied; } catch { a = false; }
                if (a)
                    applied++;
            }
            rows.Add(new PresetRow(preset, preset.Name, preset.Description, preset.Glyph, applied, total));
        }
        return rows;
    }

    /// <summary>How many tweaks "Apply" on this preset card would actually change.</summary>
    public int PresetPendingCount(Preset preset)
    {
        var count = 0;
        foreach (var id in preset.TweakIds)
        {
            var t = AppServices.Engine.Find(id);
            if (t is null || !t.IsApplicable)
                continue;
            try
            {
                if (!t.IsApplied)
                    count++;
            }
            catch { /* unreadable state - skip */ }
        }
        return count;
    }

    public async Task<string> ApplyPresetAsync(Preset preset, bool createRestorePoint)
    {
        IsBusy = true;
        try
        {
            if (createRestorePoint)
            {
                BusyText = "Creating restore point (can take a minute)…";
                var (ok, message) = await Task.Run(() => RestorePointService.Create());
                if (!ok)
                    return $"Restore point failed: {message} - nothing was changed.";
            }

            BusyText = $"Applying '{preset.Name}'…";
            var results = await AppServices.Engine.ApplyIdsAsync(preset.TweakIds);
            var failed = results.Where(kv => !kv.Value.Success).ToList();
            var applied = results.Count - failed.Count;

            await RefreshScoreAsync();

            return failed.Count == 0
                ? $"{preset.Name}: applied {Format.Count(applied, "tweak")}. Score: {Score}/100."
                : $"{preset.Name}: applied {Format.Count(applied, "tweak")}, {failed.Count} failed - {string.Join("; ", failed.Take(3).Select(f => $"{f.Key}: {f.Value.Error}"))}";
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

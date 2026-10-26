using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Stratus.Core.Models;
using Stratus.Core.Services;
using Stratus.Core.Tweaks;
using Stratus.Services;

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

    public string ApplyRecommendedButtonText => $"Apply recommended ({_pending})";
    public bool CanApplyRecommended => _pending > 0 && !IsBusy;

    private bool _rebootPending;
    public bool RebootPending
    {
        get => _rebootPending;
        private set { if (Set(ref _rebootPending, value)) Raise(nameof(RebootText)); }
    }

    public string RebootText => RebootPending ? "Restart required" : "No restart pending";

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

    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) Raise(nameof(CanApplyRecommended)); } }
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
        try
        {
            var report = await Task.Run(() => AppServices.Engine.Rescore());
            ApplySnapshot(report);
        }
        catch (Exception ex)
        {
            Log.Warn("Dashboard score refresh failed: " + ex.Message);
            ShowStatus($"Could not refresh the score: {ex.Message}", InfoBarSeverity.Warning);
        }
    }

    /// <summary>
    /// Applies one engine snapshot on the UI thread: score, category rows, pending count,
    /// presets and (optionally) the active plan name. No registry reads happen here.
    /// </summary>
    public void ApplySnapshot(ScoreReport report, string? activePlan = null)
    {
        _pending = report.States.Count(s =>
            s.IsApplicable && !s.IsApplied && s.Tweak.Risk <= RiskLevel.Recommended);
        ApplyReport(report);
        RebootPending = SystemInfoService.RebootPending();
        if (activePlan is not null)
        {
            _powerPlan = activePlan;
            Raise(nameof(PowerPlan));
        }
        SyncPresets(BuildPresetRows(report.States));
        Raise(nameof(PendingRecommended), nameof(PendingText),
              nameof(ApplyRecommendedButtonText), nameof(CanApplyRecommended));

        // Every snapshot is a graph point, including applies made from other pages (this method
        // is the single funnel for engine refreshes). Fire and forget: the store has its own lock.
        _ = RecordHistoryAsync(report.Score);
    }

    private async Task RecordHistoryAsync(int score)
    {
        try
        {
            ScoreHistory = await Task.Run(() => RecordAndReadHistory(score));
        }
        catch (Exception ex)
        {
            Log.Warn("Score history update failed: " + ex.Message);
        }
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

            var report = await reportTask;
            _summary = await summaryTask;
            _powerPlan = (await planTask)?.Name ?? "Unknown";
            ApplySnapshot(report);
            Raise(nameof(Cpu), nameof(Gpu), nameof(Ram), nameof(Os), nameof(ElevatedText), nameof(PowerPlan));
        }
        catch (Exception ex)
        {
            Log.Warn("Dashboard rescan failed: " + ex.Message);
            ShowStatus($"Rescan failed: {ex.Message}", InfoBarSeverity.Warning);
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
                CategoryStyle.For(c.Category)));
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
    public int PendingRecommendedCount() => Report?.States.Count(s =>
        s.IsApplicable && !s.IsApplied && s.Tweak.Risk <= RiskLevel.Recommended) ?? 0;

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
        catch (Exception ex)
        {
            Log.Error("Apply recommended failed", ex);
            return $"Apply failed: {ex.Message}";
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

    /// <summary>Applicable tweak ids mapped to their applied state, from one engine snapshot.</summary>
    private static Dictionary<string, bool> AppliedMap(IReadOnlyList<TweakState> states)
    {
        var map = new Dictionary<string, bool>(states.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var s in states)
        {
            if (s.IsApplicable)
                map[s.Tweak.Id] = s.IsApplied;
        }
        return map;
    }

    /// <summary>Per-preset progress derived from a snapshot (unknown and n/a tweaks excluded).</summary>
    private static List<PresetRow> BuildPresetRows(IReadOnlyList<TweakState> states)
    {
        var appliedById = AppliedMap(states);
        var rows = new List<PresetRow>();
        foreach (var preset in PresetCatalog.All)
        {
            var total = 0;
            var applied = 0;
            foreach (var id in preset.TweakIds)
            {
                if (!appliedById.TryGetValue(id, out var isApplied))
                    continue;
                total++;
                if (isApplied)
                    applied++;
            }
            rows.Add(new PresetRow(preset, preset.Name, preset.Description, preset.Glyph, applied, total));
        }
        return rows;
    }

    /// <summary>How many tweaks "Apply" on this preset card would actually change, from the latest snapshot.</summary>
    public int PresetPendingCount(Preset preset)
    {
        if (Report is null)
            return 0;
        var appliedById = AppliedMap(Report.States);
        var count = 0;
        foreach (var id in preset.TweakIds)
        {
            if (appliedById.TryGetValue(id, out var isApplied) && !isApplied)
                count++;
        }
        return count;
    }

    /// <summary>Names of the tweaks a preset apply would change, for the review dialog.</summary>
    public List<string> PendingPresetNames(Preset preset) =>
        Report is null
            ? []
            : Report.States
                .Where(s => s.IsApplicable && !s.IsApplied &&
                            preset.TweakIds.Contains(s.Tweak.Id, StringComparer.OrdinalIgnoreCase))
                .Select(s => s.Tweak.Name)
                .ToList();

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
        catch (Exception ex)
        {
            Log.Error($"Apply preset '{preset.Name}' failed", ex);
            return $"{preset.Name}: apply failed - {ex.Message}";
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
        catch (Exception ex)
        {
            Log.Warn("Restore point failed: " + ex.Message);
            return $"Restore point failed: {ex.Message}";
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

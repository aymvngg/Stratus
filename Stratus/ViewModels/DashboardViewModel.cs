using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Stratus.Core.Coordination;
using Stratus.Core.Models;
using Stratus.Core.Services;
using Stratus.Core.Tweaks;
using Stratus.Services;

namespace Stratus.ViewModels;

public sealed record CategoryRow(string Title, string Detail, int Score, Brush Brush);

/// <summary>One preset card on the Dashboard. Recomputed on every refresh, detection-driven like everything else.
/// UnknownCount covers preset members whose state could not be read; they are never counted as applicable.</summary>
public sealed record PresetRow(Preset Preset, string Name, string Description, string Glyph, int Applied, int Total, int UnknownCount = 0)
{
    public bool Complete => Total > 0 && UnknownCount == 0 && Applied == Total;
    public bool CanApply => Total > 0 && !Complete;
    public bool AllUnreadable => Total == 0 && UnknownCount > 0;
    public string ProgressText => AllUnreadable ? "Every member state could not be read"
        : Total == 0 ? "Nothing applicable on this machine"
        : UnknownCount > 0 ? $"{Applied} of {Total} applied, {UnknownCount} unreadable"
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
    private int _pending;
    private IReadOnlyList<ScoreSnapshot> _scoreHistory = Array.Empty<ScoreSnapshot>();

    public ScoreReport? Report
    {
        get => _report;
        private set
        {
            if (Set(ref _report, value))
                Raise(nameof(Score), nameof(ScoreValueText), nameof(HasScore), nameof(StatusText), nameof(StatusBrush),
                      nameof(AppliedText), nameof(ScoreBrush), nameof(AppliedCountText), nameof(ScoreCaption),
                      nameof(UnknownNoticeText), nameof(HasUnknownNotice));
        }
    }

    public int Score => Report?.Score ?? 0;

    /// <summary>False when no applicable tweak state could be read: there is no honest score to show.</summary>
    public bool HasScore => Report is not null && Report.TotalCount > 0;

    /// <summary>The ring value, or "--" when every read failed.</summary>
    public string ScoreValueText => Report is null ? "..." : HasScore ? Score.ToString() : "--";

    public string StatusText => Report?.StatusText ?? "-";

    public string AppliedText => Report switch
    {
        null => "Scanning...",
        { TotalCount: 0 } => "No tweak state could be read.",
        var report => $"{report.AppliedCount} of {report.TotalCount} applicable tweaks applied",
    };

    public string AppliedCountText => Report switch
    {
        null => "...",
        { TotalCount: 0 } => "--",
        var report => $"{report.AppliedCount} / {report.TotalCount}",
    };

    public string ScoreCaption => Report is null ? "Scanning the system..." : Report.StatusText;

    /// <summary>Some rows could not be read while others could: the score covers only the known ones.</summary>
    public bool HasUnknownNotice => Report is { UnknownCount: > 0, TotalCount: > 0 };

    public string UnknownNoticeText
    {
        get
        {
            if (Report is not { UnknownCount: > 0 } report)
                return "";

            return report.TotalCount == 0
                ? "No tweak state could be read."
                : $"{report.UnknownCount} tweak{(report.UnknownCount == 1 ? "" : "s")} could not be read and are left out of the score.";
        }
    }

    /// <summary>Ring + accent colour that tracks health: green -> blue -> amber -> red, grey when unknown.</summary>
    public Brush ScoreBrush => HasScore ? RiskStyles.HealthBrush(Score) : RiskStyles.NeutralBrush;

    /// <summary>Status line colour: grey for unavailable, amber for a partial read, health otherwise.</summary>
    public Brush StatusBrush => !HasScore
        ? RiskStyles.NeutralBrush
        : HasUnknownNotice ? RiskStyles.WarningBrush : RiskStyles.HealthBrush(Score);

    public string Cpu => _summary?.Cpu ?? "…";
    public string Gpu => _summary?.Gpu ?? "…";
    public string Ram => _summary?.Ram ?? "…";
    public string Os => _summary is null ? "…" : $"{_summary.Os} ({_summary.OsBuild})";
    public string ElevatedText => _summary is null ? "…" : _summary.IsElevated ? "Administrator" : "Standard (limited)";
    public string PowerPlan => _powerPlan;

    public int PendingRecommended => _pending;
    public string PendingText
    {
        get
        {
            var unknown = Report?.UnknownCount ?? 0;
            if (_pending == 0 && unknown > 0)
                return $"No pending recommended tweaks in the states that could be read ({unknown} unreadable)";

            return _pending == 0
                ? "All recommended tweaks applied"
                : $"{_pending} recommended tweak{(_pending == 1 ? "" : "s")} pending";
        }
    }

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

    public async Task RefreshScoreAsync()
    {
        var revision = AppServices.StateRevision.Current;
        try
        {
            var report = await Task.Run(() => AppServices.Engine.Rescore());
            if (!AppServices.StateRevision.IsCurrent(revision))
                return;

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
            s.IsApplicable && s.ReadError is null && !s.IsApplied && s.Tweak.Risk <= RiskLevel.Recommended);
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

        // Only a complete read is a graph point: a partial score covers just the rows that
        // could be read, and plotting it would read as a real measurement.
        if (HasScore && !HasUnknownNotice)
        {
            _ = RecordHistoryAsync(report.Score);
        }
    }

    private async Task RecordHistoryAsync(int score)
    {
        try
        {
            ScoreHistory = await Task.Run(() =>
            {
                AppServices.ScoreHistory.AppendIfChanged(score);
                return AppServices.ScoreHistory.All();
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Score history update failed: " + ex.Message);
        }
    }

    public async Task RescanAsync()
    {
        if (IsBusy)
        {
            ShowStatus("A scan is already running.", InfoBarSeverity.Informational);
            return;
        }

        IsBusy = true;
        BusyText = "Reading system state…";
        var revision = AppServices.StateRevision.Current;
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
            var summary = await summaryTask;
            var plan = await planTask;
            if (!AppServices.StateRevision.IsCurrent(revision))
                return;

            _summary = summary;
            _powerPlan = plan?.Name ?? "Unknown";
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

    /// <summary>Number of pending tweaks "Apply recommended" would touch (Safe + Recommended risk).
    /// Unknown reads are not counted: there is no evidence they are off.</summary>
    public int PendingRecommendedCount() => Report?.States.Count(s =>
        s.IsApplicable && s.ReadError is null && !s.IsApplied && s.Tweak.Risk <= RiskLevel.Recommended) ?? 0;

    /// <summary>Ids "Apply recommended" would touch, from the rendered snapshot.
    /// Unknown reads are excluded so a failed read is never turned into an apply.</summary>
    private List<string> PendingRecommendedIds() => Report?.States
        .Where(s => s.IsApplicable && s.ReadError is null && !s.IsApplied && s.Tweak.Risk <= RiskLevel.Recommended)
        .Select(s => s.Tweak.Id)
        .ToList() ?? new List<string>();

    public async Task<OperationOutcome> ApplyRecommendedAsync(bool createRestorePoint)
    {
        if (IsBusy)
            return OperationOutcome.Warning("Another operation is still running.");

        var pending = PendingRecommendedIds();
        if (pending.Count == 0)
            return OperationOutcome.Info("Nothing to do: every recommended tweak that could be read is already applied.");

        if (!AppServices.TweakWrites.TryEnter())
            return OperationOutcome.Warning("The tweak list is being changed on another page.");

        IsBusy = true;
        // Any scan that started before this change must not land after it.
        AppServices.StateRevision.Advance();
        try
        {
            if (createRestorePoint)
            {
                BusyText = "Creating restore point (can take a minute)…";
                var (ok, message) = await Task.Run(() => RestorePointService.Create(AppServices.Registry));
                if (!ok)
                    return OperationOutcome.Error($"Restore point failed: {message} - nothing was changed.");
            }

            BusyText = "Applying tweaks…";
            var results = await AppServices.Engine.ApplyIdsAsync(pending);
            var failed = results.Where(kv => !kv.Value.Success).ToList();
            var applied = results.Count - failed.Count;

            await RefreshScoreAsync();

            var scoreText = HasScore ? $" Score: {Score}/100." : " The score is unavailable because no state could be read.";
            if (results.Count == 0)
                return OperationOutcome.Warning(
                    "Nothing was applied. Every pending tweak was already applied or is no longer applicable on this machine." + scoreText);
            if (failed.Count == 0)
                return OperationOutcome.Success($"Applied {Format.Count(applied, "tweak")}.{scoreText}");
            if (applied == 0)
                return OperationOutcome.Error(
                    $"None of the {Format.Count(failed.Count, "tweak")} could be applied - {string.Join("; ", failed.Take(3).Select(f => $"{f.Key}: {f.Value.Error}"))}{scoreText}");

            return OperationOutcome.Warning(
                $"Applied {Format.Count(applied, "tweak")}, {failed.Count} failed - {string.Join("; ", failed.Take(3).Select(f => $"{f.Key}: {f.Value.Error}"))}{scoreText}");
        }
        catch (Exception ex)
        {
            Log.Error("Apply recommended failed", ex);
            return OperationOutcome.Error($"Apply failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
            AppServices.TweakWrites.Exit();
        }
    }

    private void SyncPresets(List<PresetRow> rows)
    {
        Presets.Clear();
        foreach (var row in rows)
            Presets.Add(row);
    }

    /// <summary>Applicable tweak ids mapped to their applied state, from one engine snapshot.
    /// A tweak whose state could not be read is omitted, so presets never count it as pending.</summary>
    private static Dictionary<string, bool> AppliedMap(IReadOnlyList<TweakState> states)
    {
        var map = new Dictionary<string, bool>(states.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var s in states)
        {
            if (s.IsApplicable && s.ReadError is null)
                map[s.Tweak.Id] = s.IsApplied;
        }
        return map;
    }

    /// <summary>Per-preset progress derived from a snapshot (unknown and n/a tweaks excluded).</summary>
    private static List<PresetRow> BuildPresetRows(IReadOnlyList<TweakState> states)
    {
        var appliedById = AppliedMap(states);
        var unknownById = states
            .Where(s => s.IsApplicable && s.ReadError is not null)
            .Select(s => s.Tweak.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new List<PresetRow>();
        foreach (var preset in PresetCatalog.All)
        {
            var total = 0;
            var applied = 0;
            var unknown = 0;
            foreach (var id in preset.TweakIds)
            {
                if (unknownById.Contains(id))
                {
                    unknown++;
                    continue;
                }

                if (!appliedById.TryGetValue(id, out var isApplied))
                    continue;
                total++;
                if (isApplied)
                    applied++;
            }
            rows.Add(new PresetRow(preset, preset.Name, preset.Description, preset.Glyph, applied, total, unknown));
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

    /// <summary>Names of the tweaks a preset apply would change, for the review dialog.
    /// Unknown reads are excluded for the same reason as the pending count.</summary>
    public List<string> PendingPresetNames(Preset preset) =>
        Report is null
            ? []
            : Report.States
                .Where(s => s.IsApplicable && s.ReadError is null && !s.IsApplied &&
                            preset.TweakIds.Contains(s.Tweak.Id, StringComparer.OrdinalIgnoreCase))
                .Select(s => s.Tweak.Name)
                .ToList();

    public async Task<OperationOutcome> ApplyPresetAsync(Preset preset, bool createRestorePoint)
    {
        if (IsBusy)
            return OperationOutcome.Warning("Another operation is still running.");

        // Apply only members whose state could be read: a failed read is not evidence of
        // "not applied", so it must not be turned into an apply by a preset button.
        var targets = Report?.States
            .Where(s => s.IsApplicable && s.ReadError is null && !s.IsApplied &&
                        preset.TweakIds.Contains(s.Tweak.Id, StringComparer.OrdinalIgnoreCase))
            .Select(s => s.Tweak.Id)
            .ToList() ?? new List<string>();

        if (targets.Count == 0)
            return OperationOutcome.Info($"The {preset.Name} preset is already fully applied, or its remaining states could not be read.");

        if (!AppServices.TweakWrites.TryEnter())
            return OperationOutcome.Warning("The tweak list is being changed on another page.");

        IsBusy = true;
        AppServices.StateRevision.Advance();
        try
        {
            if (createRestorePoint)
            {
                BusyText = "Creating restore point (can take a minute)…";
                var (ok, message) = await Task.Run(() => RestorePointService.Create(AppServices.Registry));
                if (!ok)
                    return OperationOutcome.Error($"Restore point failed: {message} - nothing was changed.");
            }

            BusyText = $"Applying '{preset.Name}'…";
            var results = await AppServices.Engine.ApplyIdsAsync(targets);
            var failed = results.Where(kv => !kv.Value.Success).ToList();
            var applied = results.Count - failed.Count;

            await RefreshScoreAsync();

            var scoreText = HasScore ? $" Score: {Score}/100." : " The score is unavailable because no state could be read.";
            if (results.Count == 0)
                return OperationOutcome.Warning(
                    $"{preset.Name}: nothing was applied. Every pending tweak was already applied or is no longer applicable on this machine." + scoreText);
            if (failed.Count == 0)
                return OperationOutcome.Success($"{preset.Name}: applied {Format.Count(applied, "tweak")}.{scoreText}");
            if (applied == 0)
                return OperationOutcome.Error(
                    $"None of the {Format.Count(failed.Count, "tweak")} could be applied - {string.Join("; ", failed.Take(3).Select(f => $"{f.Key}: {f.Value.Error}"))}{scoreText}");

            return OperationOutcome.Warning(
                $"{preset.Name}: applied {Format.Count(applied, "tweak")}, {failed.Count} failed - {string.Join("; ", failed.Take(3).Select(f => $"{f.Key}: {f.Value.Error}"))}{scoreText}");
        }
        catch (Exception ex)
        {
            Log.Error($"Apply preset '{preset.Name}' failed", ex);
            return OperationOutcome.Error($"{preset.Name}: apply failed - {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
            AppServices.TweakWrites.Exit();
        }
    }

    public async Task<OperationOutcome> CreateRestorePointAsync()
    {
        if (IsBusy)
            return OperationOutcome.Warning("Another operation is still running.");

        IsBusy = true;
        BusyText = "Creating restore point (can take a minute)…";
        try
        {
            var (ok, message) = await Task.Run(() => RestorePointService.Create(AppServices.Registry));
            return ok
                ? OperationOutcome.Success("Restore point created.")
                : OperationOutcome.Error($"Restore point failed: {message}");
        }
        catch (Exception ex)
        {
            Log.Warn("Restore point failed: " + ex.Message);
            return OperationOutcome.Error($"Restore point failed: {ex.Message}");
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

    /// <summary>Neutral grey for "unknown": no state could be read.</summary>
    public static SolidColorBrush NeutralBrush => Brush(0xFF8A8A8A);

    /// <summary>Amber for a partial read (some rows unknown, the rest measured).</summary>
    public static SolidColorBrush WarningBrush => Brush(0xFFFFB900);

    private static SolidColorBrush Brush(uint argb) => new(Microsoft.UI.ColorHelper.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
}

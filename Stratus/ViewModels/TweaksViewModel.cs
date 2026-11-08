using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Stratus.Core.Coordination;
using Stratus.Core.Models;
using Stratus.Core.Services;
using Stratus.Services;

namespace Stratus.ViewModels;

public sealed class TweakItemViewModel : ObservableObject
{
    public Tweak T { get; }

    public string Id => T.Id;
    public string Name => T.Name;
    public string Description => T.Description;
    public string CategoryText => T.Category.ToString();
    public string RiskText => T.Risk.ToString();
    public bool RequiresReboot => T.RequiresReboot;

    /// <summary>Reference page for this tweak (registry keys, defaults, reasoning) in the Stratus repo.
    /// docs/reference.md is generated from the catalog, so every tweak has a heading here.</summary>
    public string DocsUrl => $"https://github.com/aymvngg/Stratus/blob/main/docs/reference.md#{T.Id}";

    public Brush RiskForeground { get; }
    public Brush RiskBackground { get; }
    public Brush Accent { get; }

    private readonly TweaksViewModel _owner;
    private bool _isApplied;
    private bool _notApplicable;
    private bool _isBusy;
    private bool _isUnknown;
    private string? _readError;

    public bool IsApplied { get => _isApplied; private set => Set(ref _isApplied, value); }
    public bool NotApplicable { get => _notApplicable; private set { if (Set(ref _notApplicable, value)) Raise(nameof(ToggleEnabled)); } }
    public bool IsBusy { get => _isBusy; set { if (Set(ref _isBusy, value)) Raise(nameof(ToggleEnabled)); } }

    /// <summary>The last read of this row failed. Its rendered value is the previous known one,
    /// not a claim that the tweak is off, so the switch is disabled until a read succeeds.</summary>
    public bool IsUnknown { get => _isUnknown; private set { if (Set(ref _isUnknown, value)) Raise(nameof(ToggleEnabled)); } }

    public string ReadErrorText => _readError ?? "";

    public bool ToggleEnabled => !IsBusy && !NotApplicable && !IsUnknown && !_owner.IsBulkApplying;

    public TweakItemViewModel(Tweak tweak, TweaksViewModel owner)
    {
        T = tweak;
        _owner = owner;
        (RiskForeground, RiskBackground) = RiskStyles.For(tweak.Risk);
        Accent = CategoryStyle.For(tweak.Category);
    }

    public void RefreshToggleEnabled() => Raise(nameof(ToggleEnabled));

    /// <summary>Renders a state read from the system. It never starts an apply: the page
    /// ignores a toggle event whose value already matches what is rendered here.
    /// A failed read keeps the previous rendered state and marks the row unknown.</summary>
    public void SetState(bool notApplicable, bool isApplied, string? readError = null)
    {
        _readError = readError;
        Raise(nameof(ReadErrorText));
        IsUnknown = readError is not null;

        if (readError is not null)
            return;

        NotApplicable = notApplicable;
        IsApplied = isApplied;
    }
}

/// <summary>One expandable category section on the Tweaks page.</summary>
public sealed class TweakGroup : ObservableObject
{
    private readonly TweaksViewModel _owner;

    public TweakCategory Category { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public List<TweakItemViewModel> Items { get; }
    public Brush Accent { get; }

    private int _applied;
    private int _pendingSafe;

    /// <summary>Initial expansion only: groups with pending safe tweaks open on first filter.</summary>
    public bool HasPendingItems { get; }

    public string AppliedText => $"{_applied} of {Items.Count} applied";
    public string PendingSafeText => $"Apply safe ({_pendingSafe})";
    public bool CanApplySafe => _pendingSafe > 0 && !_owner.IsBulkApplying;

    public TweakGroup(TweakCategory category, List<TweakItemViewModel> items, TweaksViewModel owner)
    {
        _owner = owner;
        Category = category;
        Title = category.ToString();
        Items = items;
        Accent = CategoryStyle.For(category);
        Subtitle = category switch
        {
            TweakCategory.Gaming => "Frame pacing, input latency and background capture.",
            TweakCategory.Visuals => "Shell animation and rendering overhead.",
            TweakCategory.Network => "Latency, throttling and background traffic.",
            TweakCategory.Services => "Background services you almost certainly don't need.",
            TweakCategory.Privacy => "Telemetry, suggestions and data collection.",
            TweakCategory.Power => "CPU/GPU scheduling and power plans.",
            _ => "",
        };

        Refresh();
        HasPendingItems = _pendingSafe > 0;
    }

    /// <summary>Recomputes counters after item states changed. Does not touch HasPendingItems so expand state survives.
    /// Unknown rows are counted as neither applied nor pending.</summary>
    public void Refresh()
    {
        _applied = Items.Count(i => i.IsApplied && !i.IsUnknown);
        _pendingSafe = PendingSafeIds().Count;
        Raise(nameof(AppliedText), nameof(CanApplySafe), nameof(PendingSafeText));
    }

    /// <summary>Ids of safe tweaks that would change. Rows whose state could not be read are
    /// excluded: an unreadable row is never turned into an apply.</summary>
    public List<string> PendingSafeIds() => Items
        .Where(i => !i.IsUnknown && !i.NotApplicable && !i.IsApplied && i.T.Risk <= RiskLevel.Recommended)
        .Select(i => i.Id)
        .ToList();
}

public sealed class TweaksViewModel : ObservableObject
{
    public List<TweakItemViewModel> All { get; }

    public ObservableCollection<TweakGroup> Groups { get; } = new();

    private string _searchText = "";
    private TweakCategory? _category;
    private RiskLevel? _risk;
    private string _statusMessage = "";
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    private bool _statusOpen;
    private bool _isBulkApplying;

    public TweaksViewModel()
    {
        All = AppServices.Engine.Tweaks.Select(t => new TweakItemViewModel(t, this)).ToList();
    }

    public string SearchText
    {
        get => _searchText;
        set { _searchText = value; ApplyFilter(); }
    }

    public TweakCategory? CategoryFilter
    {
        get => _category;
        set { _category = value; ApplyFilter(); }
    }

    public RiskLevel? RiskFilter
    {
        get => _risk;
        set { _risk = value; ApplyFilter(); }
    }

    /// <summary>While a bulk apply runs, row toggles are disabled: one writer owns the catalog.</summary>
    public bool IsBulkApplying
    {
        get => _isBulkApplying;
        private set
        {
            if (!Set(ref _isBulkApplying, value))
                return;

            foreach (var item in All)
                item.RefreshToggleEnabled();
            foreach (var group in Groups)
                group.Refresh();
        }
    }

    public string CountText
    {
        get
        {
            var shown = Groups.Sum(g => g.Items.Count);
            return $"{shown} of {All.Count} tweaks shown";
        }
    }

    public string AppliedCountText => $"{All.Count(i => i.IsApplied && !i.IsUnknown)} of {All.Count} applied";

    public string SummaryText
    {
        get
        {
            var applied = All.Count(i => i.IsApplied && !i.IsUnknown);
            var pending = All.Count(i => !i.IsUnknown && !i.IsApplied && !i.NotApplicable && i.T.Risk <= RiskLevel.Recommended);
            var unknown = All.Count(i => i.IsUnknown);
            var text = $"{applied} of {All.Count} applied - {pending} recommended pending";
            return unknown == 0 ? text : $"{text} - {unknown} could not be read";
        }
    }

    public bool NoResults => Groups.Count == 0;

    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public InfoBarSeverity StatusSeverity { get => _statusSeverity; private set => Set(ref _statusSeverity, value); }
    public bool StatusOpen { get => _statusOpen; set => Set(ref _statusOpen, value); }

    public void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
        StatusOpen = true;
    }

    public void ApplyFilter()
    {
        var query = _searchText.Trim();
        IEnumerable<TweakItemViewModel> source = All;

        if (_category is { } cat)
            source = source.Where(i => i.T.Category == cat);
        if (_risk is { } risk)
            source = source.Where(i => i.T.Risk == risk);
        if (query.Length > 0)
            source = source.Where(i =>
                i.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                i.Description.Contains(query, StringComparison.OrdinalIgnoreCase));

        Groups.Clear();
        foreach (var group in source
                     .OrderBy(i => i.T.Category)
                     .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                     .GroupBy(i => i.T.Category)
                     .OrderBy(g => g.Key))
        {
            Groups.Add(new TweakGroup(group.Key, group.ToList(), this));
        }

        Raise(nameof(CountText), nameof(NoResults), nameof(AppliedCountText), nameof(SummaryText));
    }

    public async Task RefreshStatesAsync()
    {
        var revision = AppServices.StateRevision.Current;
        try
        {
            var states = await Task.Run(() => AppServices.Engine.Rescore().States);
            if (!AppServices.StateRevision.IsCurrent(revision))
                return;

            ApplyStates(states);
        }
        catch (Exception ex)
        {
            Log.Warn("Reading the tweak states failed: " + ex.Message);
            ShowStatus($"Could not read the tweak states: {ex.Message}", InfoBarSeverity.Warning);
        }
    }

    /// <summary>Pushes an engine snapshot (registry state read once) into the rows. UI thread.
    /// A row whose read failed keeps its previous value and is marked unknown instead of off.</summary>
    public void ApplyStates(IReadOnlyList<TweakState> states)
    {
        foreach (var s in states)
        {
            var item = All.FirstOrDefault(i => i.T == s.Tweak);
            item?.SetState(!s.IsApplicable, s.IsApplied, s.ReadError);
        }

        NotifyItemChanged();
    }

    /// <summary>Re-computes the summary chips and group counters after a toggle changed an item's state.</summary>
    public void NotifyItemChanged()
    {
        foreach (var g in Groups)
            g.Refresh();
        Raise(nameof(AppliedCountText), nameof(SummaryText));
    }

    /// <summary>Applies every pending Safe/Recommended tweak in one category. Mirrors ApplyRecommendedAsync.</summary>
    public async Task<OperationOutcome> ApplyCategoryAsync(TweakGroup group, bool createRestorePoint)
    {
        // Shared with the confirmation dialog so the count the user approved is the count applied.
        // Rows whose state could not be read are never applied by a bulk action: there is no
        // evidence they are off, so the user has not chosen them.
        var pending = group.PendingSafeIds();

        if (pending.Count == 0)
        {
            var unknown = group.Items.Count(i => i.IsUnknown && !i.NotApplicable && i.T.Risk <= RiskLevel.Recommended);
            return unknown == 0
                ? OperationOutcome.Info("Nothing to do: every safe tweak in this category is already applied.")
                : OperationOutcome.Warning(
                    $"Nothing to apply: every safe tweak is already applied, except {unknown} whose state could not be read.");
        }

        if (!AppServices.TweakWrites.TryEnter())
            return OperationOutcome.Warning("Another operation is already running.");

        IsBulkApplying = true;
        AppServices.StateRevision.Advance();
        try
        {
            if (createRestorePoint)
            {
                var (ok, error) = await Task.Run(() => RestorePointService.Create(AppServices.Registry));
                if (!ok)
                    return OperationOutcome.Error($"Restore point failed: {error} - nothing was changed.");
            }

            var results = await AppServices.Engine.ApplyIdsAsync(pending);
            await RefreshStatesAsync();

            // results names only the tweaks the engine actually attempted: ids that turned
            // out to be already applied or not applicable since the dialog are skipped.
            var failed = results.Where(kv => !kv.Value.Success).ToList();
            var attempted = results.Count;
            var applied = attempted - failed.Count;
            if (attempted == 0)
                return OperationOutcome.Warning(
                    $"{group.Title}: nothing was applied. Every pending tweak was already applied or is no longer applicable on this machine.");
            if (failed.Count == 0)
                return OperationOutcome.Success($"{group.Title}: applied {Format.Count(applied, "tweak")}.");
            if (applied == 0)
                return OperationOutcome.Error(
                    $"{group.Title}: none of the {Format.Count(attempted, "tweak")} could be applied. Failed: {string.Join(", ", failed.Select(f => f.Key))}.");

            var summary = $"{group.Title}: applied {Format.Count(applied, "tweak")}, {Format.Count(failed.Count, "tweak")} failed. Failed: {string.Join(", ", failed.Select(f => f.Key))}.";
            if (failed.First().Value.Error is { Length: > 0 } first)
                summary += $" First error: {first}";
            return OperationOutcome.Warning(summary);
        }
        catch (Exception ex)
        {
            Log.Error($"Applying the '{group.Title}' category failed", ex);
            return OperationOutcome.Error($"{group.Title}: apply failed - {ex.Message}");
        }
        finally
        {
            IsBulkApplying = false;
            AppServices.TweakWrites.Exit();
        }
    }

    /// <summary>
    /// Applies or reverts one tweak on explicit user intent. The caller has already decided
    /// that the toggle differs from the system state, so a stale render cannot trigger this.
    /// </summary>
    public async Task<OperationOutcome> SetAppliedAsync(TweakItemViewModel item, bool apply)
    {
        if (!AppServices.TweakWrites.TryEnter())
            return OperationOutcome.Warning("Another operation is already running.");

        item.IsBusy = true;
        // Invalidate any scan that started before this change, so its result cannot land after it.
        AppServices.StateRevision.Advance();
        try
        {
            var result = apply
                ? await AppServices.Engine.ApplyAsync(item.T)
                : await AppServices.Engine.RevertAsync(item.T);

            // A successful change renders the state it produced, so a follow-up read failure
            // cannot make a finished change look unknown. Only a failed change re-reads.
            if (result.Success)
            {
                item.SetState(item.NotApplicable, apply);
            }
            else
            {
                var read = await Task.Run(() => SafeReadState(item.T));
                // A failed re-read never becomes "not applied": keep the rendered value and let
                // SetState mark the row unknown so the switch is disabled.
                item.SetState(
                    item.NotApplicable,
                    read.Error is null ? read.Applied : item.IsApplied,
                    read.Error);

                // A failed change has no StateChanged announce, so nothing invalidates the
                // scans that read mid-change. Advance here (never in finally: a success must
                // still let its announce refresh land).
                AppServices.StateRevision.Advance();
            }

            NotifyItemChanged();

            if (!result.Success)
                return OperationOutcome.Error($"{item.Name}: {result.Error}");

            if (apply && item.RequiresReboot)
                return OperationOutcome.Success($"{item.Name} applied - restart Windows to take full effect.");

            if (apply)
                return OperationOutcome.Success($"{item.Name} applied.");

            return OperationOutcome.Success($"{item.Name} reverted.");
        }
        catch (Exception ex)
        {
            Log.Error($"Changing '{item.Name}' failed", ex);
            return OperationOutcome.Error($"{item.Name}: {ex.Message}");
        }
        finally
        {
            item.IsBusy = false;
            AppServices.TweakWrites.Exit();
        }
    }

    private static (bool Applied, string? Error) SafeReadState(Tweak tweak)
    {
        try
        {
            return (tweak.IsApplied, null);
        }
        catch (Exception ex)
        {
            // Unknown, not off: the caller keeps the row's previous value and marks it unknown.
            Log.Warn($"Could not re-read {tweak.Id} after a failed change: {ex.Message}");
            return (false, "Could not read this tweak's state: " + ex.Message);
        }
    }
}

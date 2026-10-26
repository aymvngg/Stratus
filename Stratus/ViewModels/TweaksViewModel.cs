using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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

    /// <summary>Reference page for this tweak (registry keys, defaults, reasoning) in the Stratus repo.</summary>
    public string DocsUrl => $"https://github.com/aymvngg/Stratus/blob/main/docs/tweaks.md#{T.Id}";

    public Brush RiskForeground { get; }
    public Brush RiskBackground { get; }
    public Brush Accent { get; }

    private bool _isApplied;
    private bool _notApplicable;
    private bool _isBusy;

    public bool IsApplied { get => _isApplied; private set => Set(ref _isApplied, value); }
    public bool NotApplicable { get => _notApplicable; private set { if (Set(ref _notApplicable, value)) Raise(nameof(ToggleEnabled)); } }
    public bool IsBusy { get => _isBusy; set { if (Set(ref _isBusy, value)) Raise(nameof(ToggleEnabled)); } }
    public bool ToggleEnabled => !IsBusy && !NotApplicable;

    public TweakItemViewModel(Tweak tweak)
    {
        T = tweak;
        (RiskForeground, RiskBackground) = RiskStyles.For(tweak.Risk);
        Accent = CategoryStyle.For(tweak.Category);
    }

    public void SetState(bool notApplicable, bool isApplied)
    {
        NotApplicable = notApplicable;
        IsApplied = isApplied;
    }
}

/// <summary>One expandable category section on the Tweaks page.</summary>
public sealed class TweakGroup : ObservableObject
{
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
    public bool CanApplySafe => _pendingSafe > 0;

    public TweakGroup(TweakCategory category, List<TweakItemViewModel> items)
    {
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

    /// <summary>Recomputes counters after item states changed. Does not touch HasPendingItems so expand state survives.</summary>
    public void Refresh()
    {
        _applied = Items.Count(i => i.IsApplied);
        _pendingSafe = Items.Count(i => !i.NotApplicable && !i.IsApplied && i.T.Risk <= RiskLevel.Recommended);
        Raise(nameof(AppliedText), nameof(CanApplySafe), nameof(PendingSafeText));
    }
}

public sealed class TweaksViewModel : ObservableObject
{
    public List<TweakItemViewModel> All { get; } =
        AppServices.Engine.Tweaks.Select(t => new TweakItemViewModel(t)).ToList();

    public ObservableCollection<TweakGroup> Groups { get; } = new();

    private string _searchText = "";
    private TweakCategory? _category;
    private RiskLevel? _risk;
    private string _statusMessage = "";
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    private bool _statusOpen;

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

    public string CountText
    {
        get
        {
            var shown = Groups.Sum(g => g.Items.Count);
            return $"{shown} of {All.Count} tweaks shown";
        }
    }

    public string AppliedCountText => $"{All.Count(i => i.IsApplied)} of {All.Count} applied";

    public string SummaryText
    {
        get
        {
            var applied = All.Count(i => i.IsApplied);
            var pending = All.Count(i => !i.IsApplied && !i.NotApplicable && i.T.Risk <= RiskLevel.Recommended);
            return $"{applied} of {All.Count} applied · {pending} recommended pending";
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
            Groups.Add(new TweakGroup(group.Key, group.ToList()));
        }

        Raise(nameof(CountText), nameof(NoResults), nameof(AppliedCountText), nameof(SummaryText));
    }

    public void RequestStateRefresh() => _ = RefreshStatesAsync();

    public async Task RefreshStatesAsync()
    {
        try
        {
            var states = await Task.Run(() => AppServices.Engine.Rescore().States);
            ApplyStates(states);
        }
        catch (Exception ex)
        {
            Log.Warn("Reading the tweak states failed: " + ex.Message);
        }
    }

    /// <summary>Pushes an engine snapshot (registry state read once) into the rows. UI thread.</summary>
    public void ApplyStates(IReadOnlyList<TweakState> states)
    {
        foreach (var s in states)
        {
            var item = All.FirstOrDefault(i => i.T == s.Tweak);
            item?.SetState(!s.IsApplicable, s.IsApplied);
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
    public async Task<string> ApplyCategoryAsync(TweakGroup group, bool createRestorePoint)
    {
        var pending = group.Items
            .Where(i => !i.NotApplicable && !i.IsApplied && i.T.Risk <= RiskLevel.Recommended)
            .Select(i => i.Id)
            .ToList();

        if (pending.Count == 0)
            return "Nothing to do: every safe tweak in this category is already applied.";

        if (createRestorePoint)
        {
            var (ok, error) = await Task.Run(RestorePointService.Create);
            if (!ok)
                return $"Restore point failed: {error} - nothing was changed.";
        }

        try
        {
            var results = await AppServices.Engine.ApplyIdsAsync(pending);
            await RefreshStatesAsync();

            var failed = results.Where(kv => !kv.Value.Success).ToList();
            if (failed.Count == 0)
                return $"{group.Title}: applied {pending.Count} tweak{(pending.Count == 1 ? "" : "s")}.";

            var summary = $"{group.Title}: applied {pending.Count - failed.Count} of {pending.Count}. Failed: {string.Join(", ", failed.Select(f => f.Key))}.";
            if (failed.First().Value.Error is { Length: > 0 } first)
                summary += $" First error: {first}";
            return summary;
        }
        catch (Exception ex)
        {
            Log.Error($"Applying the '{group.Title}' category failed", ex);
            return $"{group.Title}: apply failed - {ex.Message}";
        }
    }
}

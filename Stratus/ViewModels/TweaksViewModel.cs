using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Stratus.Core.Models;

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

    public Brush RiskForeground { get; }
    public Brush RiskBackground { get; }

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
    }

    public void SetState(bool notApplicable, bool isApplied)
    {
        NotApplicable = notApplicable;
        IsApplied = isApplied;
    }
}

/// <summary>One category section on the Tweaks page.</summary>
public sealed class TweakGroup
{
    public string Title { get; }
    public string Header { get; }
    public string Subtitle { get; }
    public List<TweakItemViewModel> Items { get; }

    public TweakGroup(string title, List<TweakItemViewModel> items)
    {
        Title = title;
        Items = items;
        Header = $"{title} · {items.Count} tweak{(items.Count == 1 ? "" : "s")}";
        Subtitle = title switch
        {
            "Gaming" => "Frame pacing, input latency and background capture.",
            "Visuals" => "Shell animation and rendering overhead.",
            "Network" => "Latency, throttling and background traffic.",
            "Services" => "Background services you almost certainly don't need.",
            "Privacy" => "Telemetry, suggestions and data collection.",
            "Power" => "CPU/GPU scheduling and power plans.",
            _ => "",
        };
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
            Groups.Add(new TweakGroup(group.Key.ToString(), group.ToList()));
        }

        Raise(nameof(CountText), nameof(NoResults), nameof(AppliedCountText), nameof(SummaryText));
    }

    public void RequestStateRefresh() => _ = RefreshStatesAsync();

    public async Task RefreshStatesAsync()
    {
        var snapshot = await Task.Run(() => All.Select(i =>
        {
            bool na = true, applied = false;
            try { na = !i.T.IsApplicable; } catch { }
            try { applied = i.T.IsApplied; } catch { }
            return (Item: i, Na: na, Applied: applied);
        }).ToList());

        foreach (var s in snapshot)
            s.Item.SetState(s.Na, s.Applied);

        NotifyItemChanged();
    }

    /// <summary>Re-computes the summary chips after a toggle changed an item's state.</summary>
    public void NotifyItemChanged() =>
        Raise(nameof(AppliedCountText), nameof(SummaryText));
}

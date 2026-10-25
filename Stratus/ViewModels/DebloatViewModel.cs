using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Stratus.Core.Services;

namespace Stratus.ViewModels;

public sealed class UwpRow : ObservableObject
{
    public UwpAppItem Item { get; }

    public string Name => Item.Name;
    public string Publisher => Item.Publisher;

    private bool _isSelected;
    private string? _result;
    private bool _isBusy;

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public string? Result { get => _result; private set => Set(ref _result, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    public UwpRow(UwpAppItem item) => Item = item;

    public async Task<(bool Ok, string Message)> RemoveAsync()
    {
        IsBusy = true;
        try
        {
            var (ok, message) = await AppServices.Debloat.RemoveAsync(Item);
            Result = ok ? "Removed" : message;
            return (ok, message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed class DebloatViewModel : ObservableObject
{
    public ObservableCollection<UwpRow> Recommended { get; } = new();
    public ObservableCollection<UwpRow> OtherMicrosoft { get; } = new();
    public ObservableCollection<UwpRow> ThirdParty { get; } = new();
    public ObservableCollection<Win32AppItem> Win32 { get; } = new();

    // Full scan results; the observable collections above show the filtered view.
    private List<UwpRow> _allRecommended = new();
    private List<UwpRow> _allOtherMicrosoft = new();
    private List<UwpRow> _allThirdParty = new();
    private List<Win32AppItem> _allWin32 = new();

    private string _searchText = "";
    private bool _hasScanned;
    private bool _isBusy;
    private string _busyText = "";
    private string _statusMessage = "";
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    private bool _statusOpen;

    public string SearchText
    {
        get => _searchText;
        set { _searchText = value; Refilter(); }
    }

    public bool HasScanned { get => _hasScanned; private set { if (Set(ref _hasScanned, value)) Raise(nameof(NotScannedHint)); } }
    public bool NotScannedHint => !HasScanned;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public string BusyText { get => _busyText; private set => Set(ref _busyText, value); }

    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public InfoBarSeverity StatusSeverity { get => _statusSeverity; private set => Set(ref _statusSeverity, value); }
    public bool StatusOpen { get => _statusOpen; set => Set(ref _statusOpen, value); }

    public string RecommendedCount => $"Recommended ({Recommended.Count})";
    public string OtherMicrosoftCount => $"Other Microsoft apps ({OtherMicrosoft.Count})";
    public string ThirdPartyCount => $"Third-party ({ThirdParty.Count})";
    public string Win32Count => $"Desktop programs ({Win32.Count})";

    public bool RecommendedEmpty => HasScanned && Recommended.Count == 0;
    public bool OtherMicrosoftEmpty => HasScanned && OtherMicrosoft.Count == 0;
    public bool ThirdPartyEmpty => HasScanned && ThirdParty.Count == 0;
    public bool Win32Empty => HasScanned && Win32.Count == 0;

    public string SelectedSummary => TotalSelected == 0
        ? "Nothing selected"
        : $"{TotalSelected} app{(TotalSelected == 1 ? "" : "s")} selected";

    public int TotalSelected => Recommended.Concat(OtherMicrosoft).Concat(ThirdParty).Count(r => r.IsSelected);
    public string RemoveButtonText => TotalSelected == 0 ? "Remove selected" : $"Remove selected ({TotalSelected})";

    public event Action? SelectionChanged;

    public DebloatViewModel()
    {
        foreach (var group in new[] { Recommended, OtherMicrosoft, ThirdParty })
            group.CollectionChanged += (_, _) => RaiseCounts();
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UwpRow.IsSelected))
            Raise(nameof(TotalSelected), nameof(RemoveButtonText), nameof(SelectedSummary));
        SelectionChanged?.Invoke();
    }

    private void RaiseCounts() =>
        Raise(nameof(RecommendedCount), nameof(OtherMicrosoftCount), nameof(ThirdPartyCount), nameof(Win32Count),
              nameof(RecommendedEmpty), nameof(OtherMicrosoftEmpty), nameof(ThirdPartyEmpty), nameof(Win32Empty),
              nameof(SelectedSummary));

    public void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
        StatusOpen = true;
    }

    public async Task ScanAsync()
    {
        IsBusy = true;
        BusyText = "Scanning installed apps…";
        try
        {
            var uwpTask = Task.Run(() => AppServices.Debloat.ScanUwp());
            var win32Task = Task.Run(() => AppServices.Debloat.ScanWin32());
            var uwp = await uwpTask;
            var win32 = await win32Task;

            _allRecommended = Rows(uwp.Where(i => i.Group == DebloatGroup.Recommended));
            _allOtherMicrosoft = Rows(uwp.Where(i => i.Group == DebloatGroup.OtherMicrosoft));
            _allThirdParty = Rows(uwp.Where(i => i.Group == DebloatGroup.ThirdParty));
            _allWin32 = win32;

            HasScanned = true;
            Refilter();
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    private static List<UwpRow> Rows(IEnumerable<UwpAppItem> items)
    {
        var list = new List<UwpRow>();
        foreach (var item in items)
            list.Add(new UwpRow(item));
        return list;
    }

    /// <summary>Rebuilds the visible collections from the backing lists using the current search text.</summary>
    private void Refilter()
    {
        // Keep row subscriptions alive even when a row is filtered out.
        foreach (var row in _allRecommended.Concat(_allOtherMicrosoft).Concat(_allThirdParty))
            row.PropertyChanged -= OnRowChanged;

        var query = _searchText.Trim();
        FillVisible(Recommended, Filter(_allRecommended, query));
        FillVisible(OtherMicrosoft, Filter(_allOtherMicrosoft, query));
        FillVisible(ThirdParty, Filter(_allThirdParty, query));

        Win32.Clear();
        foreach (var app in string.IsNullOrEmpty(query)
                     ? _allWin32
                     : _allWin32.Where(a => Match(a.DisplayName, a.Publisher, query)))
            Win32.Add(app);

        RaiseCounts();
        Raise(nameof(TotalSelected), nameof(RemoveButtonText));

        static IEnumerable<UwpRow> Filter(List<UwpRow> source, string q) =>
            string.IsNullOrEmpty(q) ? source : source.Where(r => Match(r.Name, r.Publisher, q));

        static bool Match(string name, string publisher, string q) =>
            name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            publisher.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void FillVisible(ObservableCollection<UwpRow> target, IEnumerable<UwpRow> rows)
    {
        target.Clear();
        foreach (var row in rows)
        {
            row.PropertyChanged += OnRowChanged;
            target.Add(row);
        }
    }

    public void SetAllSelected(ObservableCollection<UwpRow> group, bool selected)
    {
        foreach (var row in group)
            row.IsSelected = selected;
    }

    public async Task<string> RemoveSelectedAsync()
    {
        var rows = Recommended.Concat(OtherMicrosoft).Concat(ThirdParty).Where(r => r.IsSelected).ToList();
        if (rows.Count == 0)
            return "Nothing selected.";

        IsBusy = true;
        var ok = 0;
        var failed = new List<string>();
        try
        {
            for (var i = 0; i < rows.Count; i++)
            {
                BusyText = $"Removing {i + 1} of {rows.Count}…";
                var (success, message) = await rows[i].RemoveAsync();
                if (success) ok++;
                else failed.Add($"{rows[i].Name}: {message}");
            }

            await ScanAsync();
            return failed.Count == 0
                ? $"Removed {Format.Count(ok, "app")}."
                : $"Removed {Format.Count(ok, "app")}, {failed.Count} failed - {string.Join("; ", failed.Take(3))}";
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }
}

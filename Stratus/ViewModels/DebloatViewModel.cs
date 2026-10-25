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

    private bool _hasScanned;
    private bool _isBusy;
    private string _busyText = "";
    private string _statusMessage = "";
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    private bool _statusOpen;

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

            Fill(Recommended, uwp.Where(i => i.Group == DebloatGroup.Recommended));
            Fill(OtherMicrosoft, uwp.Where(i => i.Group == DebloatGroup.OtherMicrosoft));
            Fill(ThirdParty, uwp.Where(i => i.Group == DebloatGroup.ThirdParty));

            Win32.Clear();
            foreach (var app in win32)
                Win32.Add(app);

            HasScanned = true;
            RaiseCounts();
            Raise(nameof(TotalSelected), nameof(RemoveButtonText));
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    private void Fill(ObservableCollection<UwpRow> target, IEnumerable<UwpAppItem> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            var row = new UwpRow(item);
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
                : $"Removed {Format.Count(ok, "app")}, {failed.Count} failed — {string.Join("; ", failed.Take(3))}";
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }
}

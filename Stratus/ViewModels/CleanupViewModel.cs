using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Stratus.Core.Services;

namespace Stratus.ViewModels;

public sealed class CleanupRow : ObservableObject
{
    public CleanupTarget Target { get; }

    public string Name => Target.Name;
    public string PathText => Target.Path;
    public bool Exists => Target.Exists;

    public string SizeText => Target.Exists
        ? (Target.FileCount > 0 ? $"{Format.Bytes(Target.SizeBytes)} · {Target.FileCount:N0} files" : "empty")
        : "not present";

    private bool _isChecked;

    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }

    /// <summary>Full strength when the target holds data, dimmed when empty or missing. Lets the color come from the active theme.</summary>
    public double SizeOpacity => Target.Exists && Target.SizeBytes > 0 ? 1.0 : 0.55;

    internal Action? Changed;

    public CleanupRow(CleanupTarget target)
    {
        Target = target;
        _isChecked = target.DefaultChecked;
    }

    public void RefreshVisual()
    {
        Raise(nameof(SizeOpacity), nameof(SizeText));
    }

    protected override void OnPropertyChanged(string propertyName)
    {
        if (propertyName == nameof(IsChecked))
            Changed?.Invoke();
    }
}

public sealed class CleanupViewModel : ObservableObject
{
    public ObservableCollection<CleanupRow> Rows { get; } = new();

    private bool _hasScanned;
    private bool _isBusy;
    private string _busyText = "";
    private string _statusMessage = "";
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    private bool _statusOpen;
    private string _selectedText = "-";

    public bool HasScanned { get => _hasScanned; private set { if (Set(ref _hasScanned, value)) Raise(nameof(NotScannedHint)); } }
    public bool NotScannedHint => !HasScanned;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public string BusyText { get => _busyText; private set => Set(ref _busyText, value); }
    public string SelectedText { get => _selectedText; private set => Set(ref _selectedText, value); }

    private long _sessionFreed;
    public string SessionText => _sessionFreed > 0 ? $"Session total: {Format.Bytes(_sessionFreed)} freed" : "";
    public bool HasSession => _sessionFreed > 0;

    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public InfoBarSeverity StatusSeverity { get => _statusSeverity; private set => Set(ref _statusSeverity, value); }
    public bool StatusOpen { get => _statusOpen; set => Set(ref _statusOpen, value); }

    public string CleanButtonText
    {
        get
        {
            var count = Rows.Count(r => r.IsChecked);
            return count == 0 ? "Clean selected" : $"Clean selected ({count})";
        }
    }

    public string AllText => HasScanned
        ? $"{Format.Bytes(Rows.Sum(r => r.Target.SizeBytes))} found across {Rows.Count(r => r.Target.Exists && r.Target.SizeBytes > 0)} locations"
        : "Scan to measure every location";

    public void SetAllChecked(bool value)
    {
        foreach (var row in Rows)
            row.IsChecked = value;
    }

    /// <summary>Checks only the targets Stratus marks as safe defaults, so a scan does not clean surprises.</summary>
    public void SelectRecommended()
    {
        foreach (var row in Rows)
            row.IsChecked = row.Target.DefaultChecked;
    }

    public void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
        StatusOpen = true;
    }

    public async Task ScanAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        BusyText = "Measuring folders…";
        try
        {
            await ScanCoreAsync();
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    /// <summary>Measures every target and rebuilds the rows. Failures are reported in the status bar. Never throws.</summary>
    private async Task ScanCoreAsync()
    {
        try
        {
            var targets = await Task.Run(() => AppServices.Cleanup.Scan());

            // Preserve user check state across rescans, unless a saved selection exists.
            var previous = Rows.ToDictionary(r => r.Target.Id, r => r.IsChecked);
            var savedIds = ReadSavedSelection();

            Rows.Clear();
            foreach (var target in targets)
            {
                var row = new CleanupRow(target);
                if (savedIds != null)
                    row.IsChecked = savedIds.Contains(target.Id);
                else if (previous.TryGetValue(target.Id, out var wasChecked))
                    row.IsChecked = wasChecked;
                row.Changed = UpdateSelectedText;
                row.RefreshVisual();
                Rows.Add(row);
            }

            HasScanned = true;
            UpdateSelectedText();
            Raise(nameof(CleanButtonText), nameof(AllText));
        }
        catch (Exception ex)
        {
            Log.Warn("Cleanup scan failed: " + ex.Message);
            ShowStatus($"Could not measure the cleanup targets: {ex.Message}", InfoBarSeverity.Warning);
        }
    }

    private void UpdateSelectedText()
    {
        var selected = Rows.Where(r => r.IsChecked).ToList();
        SelectedText = selected.Count == 0
            ? "-"
            : $"{Format.Bytes(selected.Sum(r => r.Target.SizeBytes))} selected";
        Raise(nameof(CleanButtonText));
        PersistSelection();
    }

    private void PersistSelection()
    {
        try
        {
            var ids = string.Join(",", Rows.Where(r => r.IsChecked).Select(r => r.Target.Id));
            AppServices.Settings.Set(ProfileService.CleanupSelectionKey, ids);
        }
        catch (Exception ex)
        {
            Log.Warn("Persisting the cleanup selection failed: " + ex.Message);
        }
    }

    private HashSet<string>? ReadSavedSelection()
    {
        try
        {
            var saved = AppServices.Settings.Get(ProfileService.CleanupSelectionKey);
            if (string.IsNullOrWhiteSpace(saved))
                return null;
            return new HashSet<string>(
                saved.Split(',').Select(id => id.Trim()).Where(id => id.Length > 0),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn("Reading the saved cleanup selection failed: " + ex.Message);
            return null;
        }
    }

    public async Task<string> CleanAsync(bool includeRecycleBin)
    {
        if (IsBusy)
            return "Another operation is still running.";

        var selected = Rows.Where(r => r.IsChecked && r.Target.Exists).Select(r => r.Target).ToList();
        if (selected.Count == 0 && !includeRecycleBin)
            return "Nothing selected.";

        IsBusy = true;
        BusyText = includeRecycleBin ? "Cleaning (incl. Recycle Bin)…" : "Cleaning…";
        try
        {
            var result = await Task.Run(() => AppServices.Cleanup.Clean(selected, includeRecycleBin));
            _sessionFreed += result.FreedBytes;
            Raise(nameof(SessionText), nameof(HasSession));
            await ScanCoreAsync();
            return $"Freed {Format.Bytes(result.FreedBytes)} - {result.DeletedFiles:N0} files deleted, {result.FailedFiles:N0} in use and skipped.";
        }
        catch (Exception ex)
        {
            Log.Error("Cleanup failed", ex);
            return $"Cleaning failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }
}

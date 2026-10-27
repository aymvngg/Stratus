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

    public void MarkFailed(string message) => Result = message;

    public async Task<(bool Ok, string Message)> RemoveAsync()
    {
        IsBusy = true;
        try
        {
            var (ok, message) = await AppServices.Debloat.RemoveAsync(Item);
            Result = ok ? "Removed" : message;
            return (ok, message);
        }
        catch (Exception ex)
        {
            Log.Error($"Removing '{Item.Name}' failed", ex);
            Result = ex.Message;
            return (false, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed class Win32Row : ObservableObject
{
    public Win32Row(Win32AppItem item) => Item = item;

    public Win32AppItem Item { get; }
    public string DisplayName => Item.DisplayName;
    public string Publisher => string.IsNullOrWhiteSpace(Item.Publisher) ? "Unknown publisher" : Item.Publisher;
    public string SizeText => Item.SizeBytes > 0 ? Format.Bytes(Item.SizeBytes) : "Unknown size";
    public string ScopeText => Item.Scope;
    public bool CanUninstallSilently => !string.IsNullOrWhiteSpace(Item.SilentCommand);

    private string? _result;
    public string? Result { get => _result; private set => Set(ref _result, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
                Raise(nameof(CanInteract));
        }
    }

    public bool CanInteract => !IsBusy;

    public async Task<(bool Ok, string Message)> UninstallAsync(bool silent)
    {
        if (!silent)
        {
            var started = AppServices.Debloat.LaunchUninstaller(Item);
            Result = started ? "Uninstaller opened" : "Could not start the uninstaller";
            return (started, Result);
        }

        IsBusy = true;
        try
        {
            var (ok, message) = await AppServices.Debloat.UninstallSilentlyAsync(Item);
            Result = ok ? "Uninstalled" : message;
            return (ok, message);
        }
        catch (Exception ex)
        {
            Log.Error($"Silent uninstall of '{Item.DisplayName}' failed", ex);
            Result = ex.Message;
            return (false, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void MarkResult(string message) => Result = message;
}

public sealed class FeatureRow : ObservableObject
{
    public FeatureRow(OptionalFeatureItem item)
    {
        Item = item;
        _isInstalled = item.IsInstalled;
    }

    public OptionalFeatureItem Item { get; }
    public string Name => Item.DisplayName;
    public string KindText => Item.Kind;

    private bool _isInstalled;
    public bool IsInstalled
    {
        get => _isInstalled;
        private set
        {
            if (Set(ref _isInstalled, value))
                Raise(nameof(StateText), nameof(CanRemove), nameof(CanEnable));
        }
    }

    public string StateText => IsInstalled ? "Installed" : "Not installed";
    public bool CanRemove => IsInstalled && !IsBusy;
    public bool CanEnable => !IsInstalled && !IsBusy;

    private string? _result;
    public string? Result { get => _result; private set => Set(ref _result, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
                Raise(nameof(CanInteract), nameof(CanRemove), nameof(CanEnable));
        }
    }

    public bool CanInteract => !IsBusy;

    public void MarkInstalled() => IsInstalled = true;

    public async Task<(bool Ok, string Message)> RemoveAsync()
    {
        IsBusy = true;
        try
        {
            var (ok, message) = await Task.Run(() => AppServices.OptionalFeatures.Remove(Item));
            Result = ok ? "Removed" : message;
            return (ok, message);
        }
        catch (Exception ex)
        {
            Log.Error($"Removing optional feature '{Item.Name}' failed", ex);
            Result = ex.Message;
            return (false, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<(bool Ok, string Message)> EnableAsync()
    {
        IsBusy = true;
        try
        {
            var (ok, message) = await Task.Run(() => AppServices.OptionalFeatures.Enable(Item));
            Result = ok ? "Enabled" : message;
            if (ok)
                MarkInstalled();
            return (ok, message);
        }
        catch (Exception ex)
        {
            Log.Error($"Enabling optional feature '{Item.Name}' failed", ex);
            Result = ex.Message;
            return (false, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed class ProvisionedRow : ObservableObject
{
    public ProvisionedRow(ProvisionedAppItem item) => Item = item;

    public ProvisionedAppItem Item { get; }
    public string Name => Item.Name;
    public string DisplayName => Item.DisplayName;

    private string? _result;
    public string? Result { get => _result; private set => Set(ref _result, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
                Raise(nameof(CanInteract));
        }
    }

    public bool CanInteract => !IsBusy;

    public async Task<(bool Ok, string Message)> RemoveAsync()
    {
        IsBusy = true;
        try
        {
            var (ok, message) = await AppServices.Debloat.DeprovisionAsync(Item);
            Result = ok ? "Removed" : message;
            return (ok, message);
        }
        catch (Exception ex)
        {
            Log.Error($"Deprovisioning '{Item.Name}' failed", ex);
            Result = ex.Message;
            return (false, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed class DebloatViewModel : ObservableObject
{
    private const string AllScopes = "All programs";

    public ObservableCollection<UwpRow> Recommended { get; } = new();
    public ObservableCollection<UwpRow> OtherMicrosoft { get; } = new();
    public ObservableCollection<UwpRow> ThirdParty { get; } = new();
    public ObservableCollection<Win32Row> Win32 { get; } = new();
    public ObservableCollection<FeatureRow> Features { get; } = new();
    public ObservableCollection<ProvisionedRow> Provisioned { get; } = new();

    public ObservableCollection<string> Scopes { get; } = new() { AllScopes, "All users", "Current user" };

    // Full scan results; the observable collections above show the filtered view.
    private List<UwpRow> _allRecommended = new();
    private List<UwpRow> _allOtherMicrosoft = new();
    private List<UwpRow> _allThirdParty = new();
    private List<Win32Row> _allWin32 = new();
    private List<ProvisionedRow> _allProvisioned = new();

    private string _searchText = "";
    private bool _hasScanned;
    private bool _isBusy;
    private bool _scanning;
    private string _busyText = "";
    private string _statusMessage = "";
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    private bool _statusOpen;

    public string SearchText
    {
        get => _searchText;
        set { _searchText = value; Refilter(); }
    }

    private string _scopeFilter = AllScopes;
    public string ScopeFilter
    {
        get => _scopeFilter;
        set { _scopeFilter = value; Refilter(); }
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
    public string FeaturesCount => $"Windows features ({Features.Count})";
    public string ProvisionedCount => $"Provisioned for new users ({Provisioned.Count})";

    public bool RecommendedEmpty => HasScanned && Recommended.Count == 0;
    public bool OtherMicrosoftEmpty => HasScanned && OtherMicrosoft.Count == 0;
    public bool ThirdPartyEmpty => HasScanned && ThirdParty.Count == 0;
    public bool Win32Empty => HasScanned && Win32.Count == 0;
    public bool FeaturesEmpty => Features.Count == 0;
    public bool ProvisionedEmpty => Provisioned.Count == 0;

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
              nameof(FeaturesCount), nameof(ProvisionedCount),
              nameof(RecommendedEmpty), nameof(OtherMicrosoftEmpty), nameof(ThirdPartyEmpty), nameof(Win32Empty),
              nameof(FeaturesEmpty), nameof(ProvisionedEmpty),
              nameof(SelectedSummary));

    public void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
        StatusOpen = true;
    }

    public async Task ScanAsync()
    {
        if (_scanning)
            return;

        _scanning = true;
        IsBusy = true;
        BusyText = "Scanning installed apps…";
        try
        {
            await ScanCoreAsync();
        }
        finally
        {
            _scanning = false;
            IsBusy = false;
            BusyText = "";
        }
    }

    /// <summary>Reads both app lists and rebuilds the visible groups. Never throws.</summary>
    private async Task ScanCoreAsync()
    {
        try
        {
            var uwpTask = Task.Run(() => AppServices.Debloat.ScanUwp());
            var win32Task = Task.Run(() => AppServices.Debloat.ScanWin32());
            var provisionedTask = Task.Run(() => AppServices.Debloat.ScanProvisioned());
            var uwp = await uwpTask;
            var win32 = await win32Task;
            var provisioned = await provisionedTask;

            _allRecommended = Rows(uwp.Where(i => i.Group == DebloatGroup.Recommended));
            _allOtherMicrosoft = Rows(uwp.Where(i => i.Group == DebloatGroup.OtherMicrosoft));
            _allThirdParty = Rows(uwp.Where(i => i.Group == DebloatGroup.ThirdParty));
            _allWin32 = win32.Select(a => new Win32Row(a)).ToList();
            _allProvisioned = provisioned.Select(p => new ProvisionedRow(p)).ToList();

            HasScanned = true;
            Refilter();
        }
        catch (Exception ex)
        {
            // Called from async void handlers: an escape here would kill the process.
            Log.Warn("App scan failed: " + ex.Message);
            ShowStatus($"Scan failed: {ex.Message}", InfoBarSeverity.Error);
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
        foreach (var row in _allWin32)
        {
            if (_scopeFilter != AllScopes &&
                !string.Equals(row.ScopeText, _scopeFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(query) && !Match(row.DisplayName, row.Publisher, query))
                continue;

            Win32.Add(row);
        }

        Provisioned.Clear();
        foreach (var row in _allProvisioned)
        {
            if (!string.IsNullOrEmpty(query) && !Match(row.DisplayName, row.Name, query))
                continue;

            Provisioned.Add(row);
        }

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
        if (IsBusy)
            return "Another operation is still running.";

        var rows = Recommended.Concat(OtherMicrosoft).Concat(ThirdParty).Where(r => r.IsSelected).ToList();
        if (rows.Count == 0)
            return "Nothing selected.";

        IsBusy = true;
        var ok = 0;
        var failed = new List<(string FullName, string Name, string Message)>();
        try
        {
            for (var i = 0; i < rows.Count; i++)
            {
                BusyText = $"Removing {i + 1} of {rows.Count}: {rows[i].Name}…";
                var (success, message) = await rows[i].RemoveAsync();
                if (success) ok++;
                else failed.Add((rows[i].Item.FullName, rows[i].Name, message));
            }

            await ScanCoreAsync();

            // The rescan rebuilds every row, so put the error text back on the apps that failed.
            foreach (var row in _allRecommended.Concat(_allOtherMicrosoft).Concat(_allThirdParty))
            {
                foreach (var (fullName, _, message) in failed)
                {
                    if (string.Equals(row.Item.FullName, fullName, StringComparison.OrdinalIgnoreCase))
                    {
                        row.MarkFailed(message);
                        break;
                    }
                }
            }

            return failed.Count == 0
                ? $"Removed {Format.Count(ok, "app")}."
                : $"Removed {Format.Count(ok, "app")}, {failed.Count} failed - " +
                  string.Join("; ", failed.Take(3).Select(f => $"{f.Name}: {f.Message}"));
        }
        catch (Exception ex)
        {
            Log.Error("Removing the selected apps failed", ex);
            return $"Removal failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    /// <summary>
    /// Reads the curated Windows feature list. Kept out of the main scan because the DISM
    /// queries take several seconds and most visits only need the app lists.
    /// </summary>
    public async Task ScanFeaturesAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        BusyText = "Reading Windows features…";
        try
        {
            var items = await Task.Run(() => AppServices.OptionalFeatures.Scan());

            Features.Clear();
            foreach (var item in items)
                Features.Add(new FeatureRow(item));

            RaiseCounts();
            ShowStatus(
                Features.Count == 0
                    ? "No optional features were found."
                    : $"{Features.Count} optional features found. Installed ones can be removed, missing ones can be enabled.",
                InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            Log.Warn("Optional feature scan failed: " + ex.Message);
            ShowStatus($"Could not read the Windows features: {ex.Message}", InfoBarSeverity.Warning);
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    public async Task<string> RemoveFeatureAsync(FeatureRow row)
    {
        if (IsBusy)
            return "Another operation is still running.";

        IsBusy = true;
        try
        {
            var (ok, message) = await row.RemoveAsync();
            if (!ok)
                return $"Could not remove {row.Name}: {message}";

            Features.Remove(row);
            RaiseCounts();
            return $"{row.Name} removed. A restart may be needed to finish.";
        }
        catch (Exception ex)
        {
            Log.Error($"Removing the optional feature '{row.Name}' failed", ex);
            return $"Could not remove {row.Name}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<string> EnableFeatureAsync(FeatureRow row)
    {
        if (IsBusy)
            return "Another operation is still running.";

        IsBusy = true;
        try
        {
            var (ok, message) = await row.EnableAsync();
            if (!ok)
                return $"Could not enable {row.Name}: {message}";

            RaiseCounts();
            return $"{row.Name} enabled. A restart may be needed to finish.";
        }
        catch (Exception ex)
        {
            Log.Error($"Enabling the optional feature '{row.Name}' failed", ex);
            return $"Could not enable {row.Name}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<string> UninstallWin32Async(Win32Row row, bool silent)
    {
        if (IsBusy)
            return "Another operation is still running.";

        if (silent)
        {
            IsBusy = true;
            BusyText = $"Uninstalling {row.DisplayName}…";
        }

        try
        {
            var (ok, message) = await row.UninstallAsync(silent);
            if (!ok)
                return $"Could not uninstall {row.DisplayName}: {message}";

            if (!silent)
                return $"The uninstaller for {row.DisplayName} is open. Finish it there, then refresh the list.";

            await ScanCoreAsync();

            // The rescan rebuilds every row, so put the result back on the app that was removed.
            var rebuilt = _allWin32.FirstOrDefault(r =>
                string.Equals(r.DisplayName, row.DisplayName, StringComparison.OrdinalIgnoreCase));
            rebuilt?.MarkResult(message);

            return $"{row.DisplayName} uninstalled.";
        }
        catch (Exception ex)
        {
            Log.Error($"Uninstalling '{row.DisplayName}' failed", ex);
            return $"Could not uninstall {row.DisplayName}: {ex.Message}";
        }
        finally
        {
            if (silent)
            {
                IsBusy = false;
                BusyText = "";
            }
        }
    }

    public async Task<string> RemoveProvisionedAsync(ProvisionedRow row)
    {
        if (IsBusy)
            return "Another operation is still running.";

        IsBusy = true;
        try
        {
            var (ok, message) = await row.RemoveAsync();
            if (!ok)
                return $"Could not remove {row.DisplayName}: {message}";

            Provisioned.Remove(row);
            RaiseCounts();
            return $"{row.DisplayName} is no longer provisioned for new users.";
        }
        catch (Exception ex)
        {
            Log.Error($"Removing the provisioned package '{row.DisplayName}' failed", ex);
            return $"Could not remove {row.DisplayName}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}

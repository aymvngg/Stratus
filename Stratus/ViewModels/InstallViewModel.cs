using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Stratus.Core.Services;

namespace Stratus.ViewModels;

/// <summary>One catalog app plus what winget reports about it on this machine.</summary>
public sealed class InstallRow : ObservableObject
{
    public InstallRow(AppEntry entry) => Entry = entry;

    public AppEntry Entry { get; }

    public string Id => Entry.Id;
    public string Name => Entry.Name;
    public string Description => Entry.Description;
    public string Category => Entry.Category;
    public bool Recommended => Entry.Recommended;

    private bool _isInstalled;
    public bool IsInstalled
    {
        get => _isInstalled;
        private set
        {
            if (Set(ref _isInstalled, value))
                Raise(nameof(StateText), nameof(ActionText), nameof(CanUninstall), nameof(CanAct));
        }
    }

    private string? _version;
    public string? Version
    {
        get => _version;
        private set
        {
            if (Set(ref _version, value))
                Raise(nameof(StateText));
        }
    }

    public string StateText => IsInstalled
        ? (string.IsNullOrWhiteSpace(Version) ? "Installed" : "Installed " + Version)
        : "Not installed";

    public string ActionText => IsInstalled ? "Upgrade" : "Install";

    private string _status = "";
    public string Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
                Raise(nameof(HasStatus));
        }
    }

    public bool HasStatus => _status.Length > 0;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
                Raise(nameof(CanAct), nameof(CanUninstall));
        }
    }

    public bool CanAct => !IsBusy;
    public bool CanUninstall => IsInstalled && !IsBusy;

    internal void MarkBusy(bool busy) => IsBusy = busy;

    internal void MarkInstalled(bool installed, string? version)
    {
        IsInstalled = installed;
        Version = version;
    }

    internal void SetStatus(string status) => Status = status;
}

public sealed class InstallViewModel : ObservableObject
{
    public ObservableCollection<InstallRow> Rows { get; } = new();

    // Full catalog; Rows shows the filtered view.
    private readonly List<InstallRow> _all = new();

    public ObservableCollection<string> Categories { get; } = new();

    private string _searchText = "";
    private string _selectedCategory = "All";
    private bool _isBusy;
    private string _busyText = "";
    private string _statusMessage = "";
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    private bool _statusOpen;
    private string _wingetText = "Checking winget...";

    // Assume winget is there until the first check says otherwise, so the warning
    // does not flash on launch while the version probe is still running.
    private bool _wingetAvailable = true;
    private string _countText = "";

    public InstallViewModel()
    {
        Categories.Add("All");
        foreach (var category in AppCatalog.Apps.Select(a => a.Category).Distinct())
            Categories.Add(category);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
                ApplyFilter();
        }
    }

    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (Set(ref _selectedCategory, value))
                ApplyFilter();
        }
    }

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

    public string BusyText { get => _busyText; private set => Set(ref _busyText, value); }
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public InfoBarSeverity StatusSeverity { get => _statusSeverity; private set => Set(ref _statusSeverity, value); }
    public bool StatusOpen { get => _statusOpen; set => Set(ref _statusOpen, value); }

    public string WingetText { get => _wingetText; private set => Set(ref _wingetText, value); }

    public bool WingetAvailable
    {
        get => _wingetAvailable;
        private set
        {
            if (Set(ref _wingetAvailable, value))
                Raise(nameof(WingetMissing));
        }
    }

    public bool WingetMissing => !WingetAvailable;

    public string CountText { get => _countText; private set => Set(ref _countText, value); }

    public bool IsEmpty => Rows.Count == 0;

    public void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
        StatusOpen = true;
    }

    /// <summary>Rebuilds the visible rows from the catalog using the search text and category.</summary>
    private void ApplyFilter()
    {
        var query = _searchText.Trim();

        Rows.Clear();
        foreach (var row in _all)
        {
            if (_selectedCategory != "All" &&
                !string.Equals(row.Category, _selectedCategory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (query.Length > 0 &&
                !row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !row.Id.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !row.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Rows.Add(row);
        }

        CountText = $"{Rows.Count} of {_all.Count} apps";
        Raise(nameof(IsEmpty));
    }

    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            ShowStatus("Another operation is still running.", InfoBarSeverity.Warning);
            return;
        }

        IsBusy = true;
        BusyText = "Reading winget and installed apps...";
        try
        {
            var version = await Task.Run(() => AppServices.Winget.Version());
            WingetAvailable = version is { Length: > 0 };
            WingetText = WingetAvailable
                ? "winget " + version
                : "winget is not installed on this machine.";

            if (!WingetAvailable)
            {
                _all.Clear();
                ApplyFilter();
                ShowStatus("Install App Installer from the Microsoft Store to use this page.", InfoBarSeverity.Warning);
                return;
            }

            var installed = await AppServices.Winget.GetInstalledIdsAsync();

            _all.Clear();
            foreach (var entry in AppCatalog.Apps)
            {
                var row = new InstallRow(entry);
                row.MarkInstalled(installed.Contains(entry.Id), null);
                _all.Add(row);
            }

            ApplyFilter();
            ShowStatus($"{installed.Count} installed apps detected.", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the app list", ex);
            ShowStatus("Could not read the app list: " + ex.Message, InfoBarSeverity.Warning);

            // Keep the page usable: show the catalog with unknown install state rather than
            // an empty list that looks like a broken filter.
            if (_all.Count == 0)
            {
                foreach (var entry in AppCatalog.Apps)
                    _all.Add(new InstallRow(entry));
                ApplyFilter();
            }
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    public async Task<string> InstallAsync(InstallRow row)
    {
        if (IsBusy)
        {
            ShowStatus("Another operation is still running.", InfoBarSeverity.Warning);
            return "Another operation is still running.";
        }

        // Read before the call: success flips the row to installed.
        var wasInstalled = row.IsInstalled;

        IsBusy = true;
        row.MarkBusy(true);
        BusyText = (wasInstalled ? "Upgrading " : "Installing ") + row.Name + "...";
        try
        {
            var result = wasInstalled
                ? await AppServices.Winget.UpgradeAsync(row.Entry)
                : await AppServices.Winget.InstallAsync(row.Entry);

            if (result.Success)
            {
                row.MarkInstalled(true, row.Version);
                row.SetStatus("Done");
                ShowStatus(result.Message, InfoBarSeverity.Success);
            }
            else
            {
                row.SetStatus(result.Message);
                ShowStatus(result.Message, InfoBarSeverity.Error);
            }

            return result.Message;
        }
        catch (Exception ex)
        {
            Log.Error($"Install of '{row.Name}' failed", ex);
            var message = "Could not " + (wasInstalled ? "upgrade " : "install ") + row.Name + ": " + ex.Message;
            row.SetStatus(message);
            ShowStatus(message, InfoBarSeverity.Error);
            return message;
        }
        finally
        {
            row.MarkBusy(false);
            IsBusy = false;
            BusyText = "";
        }
    }

    public async Task<string> UninstallAsync(InstallRow row)
    {
        if (IsBusy)
        {
            ShowStatus("Another operation is still running.", InfoBarSeverity.Warning);
            return "Another operation is still running.";
        }

        IsBusy = true;
        row.MarkBusy(true);
        BusyText = "Removing " + row.Name + "...";
        try
        {
            var result = await AppServices.Winget.UninstallAsync(row.Entry);
            if (result.Success)
            {
                row.MarkInstalled(false, null);
                row.SetStatus("Removed");
                ShowStatus(result.Message, InfoBarSeverity.Success);
            }
            else
            {
                row.SetStatus(result.Message);
                ShowStatus(result.Message, InfoBarSeverity.Error);
            }

            return result.Message;
        }
        catch (Exception ex)
        {
            Log.Error($"Uninstall of '{row.Name}' failed", ex);
            var message = "Could not remove " + row.Name + ": " + ex.Message;
            row.SetStatus(message);
            ShowStatus(message, InfoBarSeverity.Error);
            return message;
        }
        finally
        {
            row.MarkBusy(false);
            IsBusy = false;
            BusyText = "";
        }
    }

    public async Task<string> UpgradeAllAsync()
    {
        if (IsBusy)
        {
            ShowStatus("Another operation is still running.", InfoBarSeverity.Warning);
            return "Another operation is still running.";
        }

        var targets = _all.Where(r => r.IsInstalled).ToList();
        if (targets.Count == 0)
        {
            var none = "No installed apps in this list can be upgraded.";
            ShowStatus(none, InfoBarSeverity.Informational);
            return none;
        }

        IsBusy = true;
        BusyText = "Upgrading installed apps...";
        var ok = 0;
        var failures = new List<string>();
        try
        {
            foreach (var row in targets)
            {
                row.MarkBusy(true);
                try
                {
                    BusyText = "Upgrading " + row.Name + "...";
                    var result = await AppServices.Winget.UpgradeAsync(row.Entry);
                    if (result.Success)
                    {
                        ok++;
                        row.SetStatus("Done");
                    }
                    else
                    {
                        failures.Add(row.Name + ": " + result.Message);
                        row.SetStatus(result.Message);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"Upgrading '{row.Name}' failed", ex);
                    failures.Add(row.Name + ": " + ex.Message);
                    row.SetStatus("Could not upgrade: " + ex.Message);
                }
                finally
                {
                    row.MarkBusy(false);
                }
            }

            var message = $"{ok} of {targets.Count} apps upgraded." +
                          (failures.Count > 0 ? " " + failures[0] : "");
            ShowStatus(message, failures.Count > 0 ? InfoBarSeverity.Error : InfoBarSeverity.Success);
            return message;
        }
        catch (Exception ex)
        {
            Log.Error("Upgrading the installed apps failed", ex);
            var message = "Could not upgrade the installed apps: " + ex.Message;
            ShowStatus(message, InfoBarSeverity.Error);
            return message;
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }
}

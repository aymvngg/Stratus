using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml.Controls;
using Stratus.Core.Services;

namespace Stratus.ViewModels;

public sealed class ScheduledTaskRow : ObservableObject
{
    public ScheduledTaskRow(ScheduledTaskItem item) => Item = item;

    public ScheduledTaskItem Item { get; }

    public string Name => Item.Name;
    public string GroupText => Item.Group;
    public string Description => Item.Description;
    public string FolderText => Item.Folder;

    public string LastRunText => FormatTime(Item.LastRunUtc);
    public string NextRunText => FormatTime(Item.NextRunUtc);
    public string AuthorText => string.IsNullOrWhiteSpace(Item.Author) ? "Unknown author" : Item.Author;

    private static string FormatTime(DateTime? utc) =>
        utc is null
            ? "Never"
            : utc.Value.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Ticked for bulk enable or disable. The row stays visible, only its state changes.</summary>
    public Action? CheckedChanged { get; set; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (Set(ref _isChecked, value))
                CheckedChanged?.Invoke();
        }
    }

    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (Set(ref _isEnabled, value))
                Raise(nameof(StateText));
        }
    }

    public string StateText => IsEnabled ? "On" : "Off";

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value))
                Raise(nameof(ToggleEnabled));
        }
    }

    public bool ToggleEnabled => !IsBusy;
}

public sealed class ScheduledTasksViewModel : ObservableObject
{
    private const string AllFolders = "All folders";

    public ObservableCollection<ScheduledTaskRow> Rows { get; } = [];

    /// <summary>Rows that match the search box and the folder filter. Rows itself is never filtered in place.</summary>
    public ObservableCollection<ScheduledTaskRow> Filtered { get; } = [];

    public ObservableCollection<string> Folders { get; } = [];

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
                ApplyFilter();
        }
    }

    private string _folderFilter = AllFolders;
    public string FolderFilter
    {
        get => _folderFilter;
        set
        {
            if (Set(ref _folderFilter, value))
                ApplyFilter();
        }
    }

    private int _checkedCount;
    public int CheckedCount
    {
        get => _checkedCount;
        private set
        {
            if (Set(ref _checkedCount, value))
                Raise(nameof(SelectionText), nameof(HasChecked));
        }
    }

    public bool HasChecked => CheckedCount > 0;

    public string SelectionText => CheckedCount == 0
        ? "Tick tasks to enable or disable them together"
        : $"{CheckedCount} selected";

    private string _countText = "Scanning scheduled tasks…";
    public string CountText
    {
        get => _countText;
        private set => Set(ref _countText, value);
    }

    private bool _hasItems;
    public bool HasItems
    {
        get => _hasItems;
        private set
        {
            if (Set(ref _hasItems, value))
                Raise(nameof(Empty));
        }
    }

    public bool Empty => Filtered.Count == 0;

    public string EmptyText => Rows.Count == 0
        ? "None of the known tasks exist on this machine."
        : "No tasks match your search.";

    /// <summary>Rebuilds the visible list from Rows. Search covers name, description, folder and group.</summary>
    private void ApplyFilter()
    {
        var search = _searchText.Trim();
        Filtered.Clear();

        foreach (var row in Rows)
        {
            if (search.Length > 0 &&
                !row.Name.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !row.Description.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !row.FolderText.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !row.GroupText.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_folderFilter != AllFolders &&
                !string.Equals(row.FolderText, _folderFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Filtered.Add(row);
        }

        Raise(nameof(Empty), nameof(EmptyText));
        UpdateCount();
    }

    private void UpdateCheckedCount() => CheckedCount = Rows.Count(r => r.IsChecked);

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    private bool _statusOpen;
    public bool StatusOpen
    {
        get => _statusOpen;
        set => Set(ref _statusOpen, value);
    }

    private string _statusMessage = "";
    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Success;
    public InfoBarSeverity StatusSeverity
    {
        get => _statusSeverity;
        private set => Set(ref _statusSeverity, value);
    }

    public void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
        StatusOpen = true;
    }

    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var items = await Task.Run(() => AppServices.ScheduledTasks.Scan());

            Rows.Clear();
            foreach (var item in items)
            {
                var row = new ScheduledTaskRow(item) { IsEnabled = item.IsEnabled };
                row.CheckedChanged = UpdateCheckedCount;
                Rows.Add(row);
            }

            // Folder filter options follow the tasks that actually exist on this machine.
            var folders = Rows.Select(r => r.FolderText).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            Folders.Clear();
            Folders.Add(AllFolders);
            foreach (var folder in folders)
                Folders.Add(folder);
            if (!Folders.Contains(_folderFilter))
                FolderFilter = AllFolders;

            HasItems = Rows.Count > 0;
            CheckedCount = 0;
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Log.Warn("Scheduled task scan failed: " + ex.Message);
            ShowStatus($"Could not read the scheduled tasks: {ex.Message}", InfoBarSeverity.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateCount()
    {
        var disabled = Rows.Count(r => !r.IsEnabled);
        CountText = Rows.Count == 0
            ? "None of the known tasks exist on this machine"
            : $"{disabled} of {Rows.Count} disabled";
    }

    public async Task<bool> SetEnabledAsync(ScheduledTaskRow row, bool enabled)
    {
        row.IsBusy = true;
        try
        {
            var result = await Task.Run(() => AppServices.ScheduledTasks.SetEnabled(row.Item, enabled));

            if (result.Success)
            {
                row.IsEnabled = enabled;
                UpdateCount();
                ShowStatus(
                    enabled
                        ? $"{row.Name} will run again."
                        : $"{row.Name} will no longer run. The task is kept, so this is fully reversible.",
                    InfoBarSeverity.Success);
            }
            else
            {
                ShowStatus($"Could not change {row.Name}: {result.Error}", InfoBarSeverity.Error);
            }

            return result.Success;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not change the scheduled task '{row.Name}'", ex);
            ShowStatus($"Could not change {row.Name}: {ex.Message}", InfoBarSeverity.Error);
            return false;
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    /// <summary>Applies one state to every ticked row.</summary>
    public async Task<string> SetCheckedAsync(bool enabled)
    {
        var rows = Rows.Where(r => r.IsChecked).ToList();
        if (rows.Count == 0)
            return "Tick at least one task first.";

        var ok = 0;
        foreach (var row in rows)
        {
            if (await SetEnabledAsync(row, enabled))
                ok++;
        }

        var verb = enabled ? "Enabled" : "Disabled";
        return $"{verb} {ok} of {rows.Count} selected {(rows.Count == 1 ? "task" : "tasks")}.";
    }

    /// <summary>Turns a whole task group on or off, used by the preset menu.</summary>
    public async Task<string> ApplyGroupAsync(string group, bool enabled)
    {
        var rows = Rows
            .Where(r => string.Equals(r.GroupText, group, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (rows.Count == 0)
            return $"No {group} tasks exist on this machine.";

        var ok = 0;
        foreach (var row in rows)
        {
            if (await SetEnabledAsync(row, enabled))
                ok++;
        }

        var verb = enabled ? "Enabled" : "Disabled";
        return $"{verb} {ok} {group} {(ok == 1 ? "task" : "tasks")}.";
    }

    public async Task<string> ResetAllAsync()
    {
        IsBusy = true;
        try
        {
            var restored = await Task.Run(() => AppServices.ScheduledTasks.ResetAll());
            await RefreshAsync();

            return restored == 0
                ? "No scheduled task changes to reset."
                : $"Reset {restored} scheduled {(restored == 1 ? "task" : "tasks")} to the previous state.";
        }
        catch (Exception ex)
        {
            Log.Error("Resetting the scheduled tasks failed", ex);
            return $"Could not reset the scheduled tasks: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}

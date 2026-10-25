using System.Collections.ObjectModel;
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
    public ObservableCollection<ScheduledTaskRow> Rows { get; } = [];

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

    public bool Empty => !HasItems;

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
        var items = await Task.Run(() => AppServices.ScheduledTasks.Scan());

        Rows.Clear();
        foreach (var item in items)
            Rows.Add(new ScheduledTaskRow(item) { IsEnabled = item.IsEnabled });

        HasItems = Rows.Count > 0;
        UpdateCount();
        IsBusy = false;
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
        var result = await Task.Run(() => AppServices.ScheduledTasks.SetEnabled(row.Item, enabled));
        row.IsBusy = false;

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

    public async Task<string> ResetAllAsync()
    {
        IsBusy = true;
        var restored = await Task.Run(() => AppServices.ScheduledTasks.ResetAll());
        await RefreshAsync();

        return restored == 0
            ? "No scheduled task changes to reset."
            : $"Reset {restored} scheduled {(restored == 1 ? "task" : "tasks")} to the previous state.";
    }
}

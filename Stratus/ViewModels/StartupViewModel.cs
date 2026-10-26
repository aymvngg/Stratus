using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Stratus.Core.Models;
using Stratus.Core.Services;

namespace Stratus.ViewModels;

public sealed class StartupRow : ObservableObject
{
    public StartupRow(StartupItem item) => Item = item;

    public StartupItem Item { get; }

    public string Name => Item.Name;
    public string Command => Item.Command;
    public string ScopeText => Item.Scope;
    public string SourceText => Item.Source;

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

public sealed class StartupViewModel : ObservableObject
{
    public ObservableCollection<StartupRow> Rows { get; } = [];

    private string _countText = "Scanning startup entries…";
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
        try
        {
            var items = await Task.Run(() => AppServices.Startup.Scan());

            Rows.Clear();
            foreach (var item in items)
            {
                var row = new StartupRow(item) { IsEnabled = item.IsEnabled };
                Rows.Add(row);
            }

            HasItems = Rows.Count > 0;
            UpdateCount();
        }
        catch (Exception ex)
        {
            Log.Warn("Startup scan failed: " + ex.Message);
            ShowStatus($"Could not read the startup entries: {ex.Message}", InfoBarSeverity.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateCount()
    {
        var enabled = Rows.Count(r => r.IsEnabled);
        CountText = Rows.Count == 0
            ? "Nothing starts with Windows"
            : $"{enabled} of {Rows.Count} enabled";
    }

    public async Task<bool> SetEnabledAsync(StartupRow row, bool enabled)
    {
        row.IsBusy = true;
        try
        {
            var result = await Task.Run(() => AppServices.Startup.SetEnabled(row.Item, enabled));

            if (result.Success)
            {
                row.IsEnabled = enabled;
                UpdateCount();
                ShowStatus(
                    enabled
                        ? $"{row.Name} will start with Windows."
                        : $"{row.Name} will no longer start with Windows. The command is kept, so this is fully reversible.",
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
            Log.Error($"Could not change the startup entry '{row.Name}'", ex);
            ShowStatus($"Could not change {row.Name}: {ex.Message}", InfoBarSeverity.Error);
            return false;
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    public async Task<string> ResetAllAsync()
    {
        IsBusy = true;
        try
        {
            var restored = await Task.Run(() => AppServices.Startup.ResetAll());
            await RefreshAsync();

            return restored == 0
                ? "No startup changes to reset."
                : $"Reset {restored} startup {(restored == 1 ? "entry" : "entries")} to the previous state.";
        }
        catch (Exception ex)
        {
            Log.Error("Resetting the startup entries failed", ex);
            return $"Could not reset the startup entries: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}

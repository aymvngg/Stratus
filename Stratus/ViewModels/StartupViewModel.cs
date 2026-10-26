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

    public string PublisherText => string.IsNullOrWhiteSpace(Item.Publisher) ? "Unknown publisher" : Item.Publisher;
    public string SizeText => Item.FileSizeBytes > 0 ? Format.Bytes(Item.FileSizeBytes) : "Unknown size";
    public string ImpactText => $"{SizeText} · {PublisherText}";

    /// <summary>False when the command points at a file that no longer exists, so the button is hidden.</summary>
    public bool CanOpenLocation => !string.IsNullOrWhiteSpace(Item.ExecutablePath);

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
    private const string AllScopes = "All scopes";

    public ObservableCollection<StartupRow> Rows { get; } = [];

    /// <summary>Rows that match the scope filter. Rows itself is never filtered in place.</summary>
    public ObservableCollection<StartupRow> Filtered { get; } = [];

    public ObservableCollection<string> Scopes { get; } = [];

    private string _scopeFilter = AllScopes;
    public string ScopeFilter
    {
        get => _scopeFilter;
        set
        {
            if (Set(ref _scopeFilter, value))
                ApplyFilter();
        }
    }

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
                Raise(nameof(Empty), nameof(EmptyText));
        }
    }

    public bool Empty => Filtered.Count == 0;

    public string EmptyText => Rows.Count == 0
        ? "Nothing launches with Windows on this machine."
        : "No startup entries are registered for this scope.";

    /// <summary>Rebuilds the visible list from Rows using the scope filter.</summary>
    private void ApplyFilter()
    {
        Filtered.Clear();

        foreach (var row in Rows)
        {
            if (_scopeFilter != AllScopes &&
                !string.Equals(row.ScopeText, _scopeFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Filtered.Add(row);
        }

        Raise(nameof(Empty), nameof(EmptyText));
    }

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

            var scopes = Rows.Select(r => r.ScopeText).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            Scopes.Clear();
            Scopes.Add(AllScopes);
            foreach (var scope in scopes)
                Scopes.Add(scope);
            if (!Scopes.Contains(_scopeFilter))
                ScopeFilter = AllScopes;

            HasItems = Rows.Count > 0;
            ApplyFilter();
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

    /// <summary>Applies one state to every entry in a scope. Used by the per-group buttons.</summary>
    public async Task<string> SetScopeAsync(string scope, bool enabled)
    {
        var rows = Rows
            .Where(r => string.Equals(r.ScopeText, scope, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (rows.Count == 0)
            return $"No startup entries are registered for {scope.ToLowerInvariant()}.";

        var changed = 0;
        foreach (var row in rows)
        {
            if (row.IsEnabled == enabled)
                continue;

            if (await SetEnabledAsync(row, enabled))
                changed++;
        }

        var label = scope.ToLowerInvariant();
        var noun = rows.Count == 1 ? "entry" : "entries";
        if (changed == 0)
            return $"Every {label} {noun} is already {(enabled ? "enabled" : "disabled")}.";

        return $"{(enabled ? "Enabled" : "Disabled")} {changed} of {rows.Count} {label} {noun}.";
    }

    /// <summary>Opens Explorer with the entry's executable selected.</summary>
    public void OpenLocation(StartupRow row)
    {
        var path = row.Item.ExecutablePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowStatus($"{row.Name} does not point at a file that still exists.", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            // explorer.exe returns a non-zero exit code even when the window opens,
            // so only an exception counts as a failure here.
            ProcessRunner.Run("explorer.exe", $"/select,\"{path}\"", 5_000);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open the startup entry location: " + ex.Message);
            ShowStatus($"Could not open the folder for {row.Name}: {ex.Message}", InfoBarSeverity.Warning);
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

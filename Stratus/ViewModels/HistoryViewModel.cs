using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Stratus.Core;
using Stratus.Core.Models;
using Stratus.Core.Services;

namespace Stratus.ViewModels;

/// <summary>One row in the change timeline.</summary>
public sealed class HistoryRow : ObservableObject
{
    private static readonly SolidColorBrush OkBrush = new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x4C, 0xC3, 0x8A));
    private static readonly SolidColorBrush FailBrush = new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF1, 0x70, 0x7B));
    private static readonly SolidColorBrush NeutralBrush = new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x8A, 0x8A, 0x8A));

    private bool _isBusy;
    private readonly bool _stillApplied;

    public HistoryRow(HistoryEntry entry, Tweak? tweak, bool stillApplied)
    {
        Entry = entry;
        Tweak = tweak;
        _stillApplied = stillApplied;
    }

    public HistoryEntry Entry { get; }
    public Tweak? Tweak { get; }

    public string TimeText => Entry.TimestampUtc.ToLocalTime().ToString("MMM d, HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    public string TweakName => string.IsNullOrWhiteSpace(Entry.TweakName) ? Entry.TweakId : Entry.TweakName;
    public string ActionText => Entry.Action;
    public bool Succeeded => Entry.Success;
    public Brush StatusBrush => Entry.Success ? OkBrush : FailBrush;
    public Brush ActionBrush => Entry.Action == "Reverted" ? NeutralBrush : OkBrush;

    public string DetailText => Entry.Success
        ? (Entry.Action == "Reverted" ? "Restored to the value from before the change." : "Applied.")
        : "Failed: " + (Entry.Error ?? "unknown error");

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value))
                Raise(nameof(CanUndo));
        }
    }

    /// <summary>Undo is offered while the tweak is still applied on this machine.</summary>
    public bool CanUndo => !_isBusy && Entry.Action == "Applied" && Tweak is not null && _stillApplied;
}

/// <summary>One backed-up value inside a tweak group.</summary>
public sealed class BackupValueRow
{
    public BackupValueRow(BackupEntry entry)
    {
        Entry = entry;
    }

    public BackupEntry Entry { get; }

    public string TargetText => Entry.ScopeType == "service"
        ? "Service: " + Entry.ScopeId
        : FormatScope(Entry.ScopeId);

    public string NameText => Entry.ScopeType == "service" ? Entry.Key : Entry.Key;

    public string KindText => KindName(Entry.ValueKind);

    public string ValueText => DecodeValue(Entry);

    public string SavedText => Entry.SavedAtUtc.ToLocalTime().ToString("MMM d, HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatScope(string scopeId)
    {
        var parts = scopeId.Split('|', 3);
        return parts.Length == 3 ? parts[0] + "\\" + parts[1] : scopeId;
    }

    private static string KindName(string? kind) => kind switch
    {
        "1" => "STRING",
        "2" => "EXPAND",
        "3" => "BINARY",
        "4" => "DWORD",
        "7" => "MULTI",
        "11" => "QWORD",
        _ => "VALUE",
    };

    private static string DecodeValue(BackupEntry entry)
    {
        if (!entry.Existed)
            return "did not exist (revert deletes it)";

        var json = entry.ValueJson ?? "";
        return entry.ValueKind switch
        {
            "4" => long.TryParse(json, out var d) ? $"{d} (0x{d & 0xFFFFFFFF:X8})" : json,
            "11" => long.TryParse(json, out var q) ? q.ToString() : json,
            "3" => ToHex(json),
            "7" => string.Join(" | ", json.Split('\u0001')),
            _ => json,
        };
    }

    private static string ToHex(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            return string.Join(" ", bytes.Select(b => b.ToString("X2")));
        }
        catch
        {
            return base64;
        }
    }
}

/// <summary>All backed-up values for one tweak.</summary>
public sealed class BackupGroupRow : ObservableObject
{
    private bool _isBusy;

    public BackupGroupRow(string tweakId, string tweakName, IReadOnlyList<BackupValueRow> values, Tweak? tweak)
    {
        TweakId = tweakId;
        TweakName = tweakName;
        Values = values;
        Tweak = tweak;
    }

    public string TweakId { get; }
    public string TweakName { get; }
    public IReadOnlyList<BackupValueRow> Values { get; }
    public Tweak? Tweak { get; }

    public string CountText => Values.Count == 1 ? "1 value" : $"{Values.Count} values";

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value))
                Raise(nameof(CanRestore));
        }
    }

    public bool CanRestore => !_isBusy;
}

public sealed class HistoryViewModel : ObservableObject
{
    private readonly object _debounce = new();
    private int _token;

    private bool _isBusy;
    private string _busyText = "";
    private bool _statusOpen;
    private string _statusMessage = "";
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    private string _changesText = "No changes yet";
    private string _backupsText = "No backups stored";
    private bool _emptyHistory = true;
    private string _emptyHistoryText = "Nothing recorded yet. Apply a tweak and it shows up here with an Undo button.";
    private bool _emptyBackups = true;
    private string _searchText = "";
    private List<HistoryRow> _allChanges = new();

    public ObservableCollection<HistoryRow> Changes { get; } = [];
    public ObservableCollection<BackupGroupRow> Backups { get; } = [];

    public string SearchText
    {
        get => _searchText;
        set { _searchText = value; RefilterChanges(); }
    }

    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }
    public string BusyText { get => _busyText; set => Set(ref _busyText, value); }
    public bool StatusOpen { get => _statusOpen; set => Set(ref _statusOpen, value); }
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }
    public InfoBarSeverity StatusSeverity { get => _statusSeverity; set => Set(ref _statusSeverity, value); }
    public string ChangesText { get => _changesText; set => Set(ref _changesText, value); }
    public string BackupsText { get => _backupsText; set => Set(ref _backupsText, value); }
    public bool EmptyHistory { get => _emptyHistory; set => Set(ref _emptyHistory, value); }

    /// <summary>Empty-state text: distinguishes an empty log from an empty search result.</summary>
    public string EmptyHistoryText { get => _emptyHistoryText; private set => Set(ref _emptyHistoryText, value); }
    public bool EmptyBackups { get => _emptyBackups; set => Set(ref _emptyBackups, value); }

    /// <summary>Debounced refresh, called when the engine reports a change.</summary>
    public void RequestRefresh()
    {
        var token = Interlocked.Increment(ref _token);
        _ = Task.Run(async () =>
        {
            await Task.Delay(400);
            if (token != Volatile.Read(ref _token))
                return;
            await RefreshAsync();
        });
    }

    /// <summary>Registry probes must never take the page down: a broken key counts as "not applied".</summary>
    private static bool SafeIsApplied(Tweak tweak)
    {
        try { return tweak.IsApplicable && tweak.IsApplied; }
        catch { return false; }
    }

    public async Task RefreshAsync()
    {
        try
        {
            var snapshot = await Task.Run(() =>
            {
                var history = AppServices.History.All()
                    .Take(100)
                    .Select(e =>
                    {
                        var tweak = AppServices.Engine.Find(e.TweakId);
                        // Probe each tweak's live state here, off the UI thread. Doing it in the
                        // row's CanUndo getter meant registry reads on every search keystroke.
                        var stillApplied = tweak is not null && SafeIsApplied(tweak);
                        return (Entry: e, Tweak: tweak, StillApplied: stillApplied);
                    })
                    .ToList();

                var backups = AppServices.Backup.All()
                    .GroupBy(e => e.TweakId)
                    .Select(g => (
                        TweakId: g.Key,
                        Tweak: AppServices.Engine.Find(g.Key),
                        Values: g.OrderBy(e => e.SavedAtUtc).ToList()))
                    .OrderBy(g => g.Tweak?.Name ?? g.TweakId, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return (history, backups);
            });

            AppServices.OnUi(() =>
            {
                _allChanges = snapshot.history
                    .Select(t => new HistoryRow(t.Entry, t.Tweak, t.StillApplied))
                    .ToList();
                RefilterChanges();

                Backups.Clear();
                foreach (var group in snapshot.backups)
                {
                    var values = group.Values.Select(v => new BackupValueRow(v)).ToList();
                    Backups.Add(new BackupGroupRow(group.TweakId, group.Tweak?.Name ?? group.TweakId, values, group.Tweak));
                }

                var totalValues = snapshot.backups.Sum(g => g.Values.Count);
                ChangesText = _allChanges.Count == 0
                    ? "No changes yet"
                    : $"{_allChanges.Count} recorded change{(_allChanges.Count == 1 ? "" : "s")}, newest first";
                BackupsText = Backups.Count == 0
                    ? "No backups stored"
                    : $"{Backups.Count} tweak{(Backups.Count == 1 ? "" : "s")}, {totalValues} value{(totalValues == 1 ? "" : "s")}";
                EmptyHistory = Changes.Count == 0;
                EmptyBackups = Backups.Count == 0;
            });
        }
        catch (Exception ex)
        {
            Log.Warn("History refresh failed: " + ex.Message);
        }
    }

    /// <summary>Rebuilds the visible change rows from the backing list using the current search text.</summary>
    private void RefilterChanges()
    {
        var query = _searchText.Trim();
        Changes.Clear();
        foreach (var row in string.IsNullOrEmpty(query)
                     ? _allChanges
                     : _allChanges.Where(r =>
                         r.TweakName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                         r.DetailText.Contains(query, StringComparison.OrdinalIgnoreCase)))
            Changes.Add(row);

        EmptyHistory = Changes.Count == 0;
        EmptyHistoryText = _allChanges.Count == 0
            ? "Nothing recorded yet. Apply a tweak and it shows up here with an Undo button."
            : "No changes match your search.";
    }

    public async Task UndoAsync(HistoryRow row)
    {
        if (row.Tweak is null || row.IsBusy)
            return;

        row.IsBusy = true;
        BusyText = "Undoing " + row.TweakName + "...";

        try
        {
            var result = await AppServices.Engine.RevertAsync(row.Tweak);
            SetStatus(
                result.Success
                    ? "Undid " + row.TweakName + ". The previous value is back."
                    : "Could not undo " + row.TweakName + ": " + (result.Error ?? "unknown error"),
                result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            Log.Error($"Undo of '{row.TweakName}' failed", ex);
            SetStatus("Could not undo " + row.TweakName + ": " + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            row.IsBusy = false;
            BusyText = "";
        }

        await RefreshAsync();
    }

    public async Task RestoreGroupAsync(BackupGroupRow group)
    {
        if (group.IsBusy)
            return;

        group.IsBusy = true;
        BusyText = "Restoring " + group.TweakName + "...";

        try
        {
            // Tweaks revert through the engine (so the revert is recorded in history).
            // Anything else, such as a startup entry, is restored straight from its backup.
            var result = group.Tweak is { } tweak
                ? await AppServices.Engine.RevertAsync(tweak)
                : await Task.Run(() =>
                {
                    try
                    {
                        // RestoreAll keeps the backups when only some values could be written,
                        // so a retry is possible instead of losing the rollback data.
                        return AppServices.Registry.RestoreAll(group.TweakId)
                            ? TweakResult.Ok
                            : TweakResult.Fail("Some values could not be restored. See the Stratus log for details.");
                    }
                    catch (Exception ex)
                    {
                        return TweakResult.Fail(ex.Message);
                    }
                });

            SetStatus(
                result.Success
                    ? "Restored " + group.TweakName + " from its backup."
                    : "Could not restore " + group.TweakName + ": " + (result.Error ?? "unknown error"),
                result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            Log.Error($"Restore of '{group.TweakName}' failed", ex);
            SetStatus("Could not restore " + group.TweakName + ": " + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            group.IsBusy = false;
            BusyText = "";
        }

        await RefreshAsync();
    }

    public async Task ClearHistoryAsync()
    {
        try
        {
            await Task.Run(AppServices.History.Clear);
            SetStatus("Change history cleared. Backups are untouched.", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            Log.Error("Clearing the change history failed", ex);
            SetStatus("Could not clear the change history: " + ex.Message, InfoBarSeverity.Error);
        }

        await RefreshAsync();
    }

    public void SetStatus(string message, InfoBarSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
        StatusOpen = true;
    }
}

using System.Text.Json;
using Stratus.Core.Models;

namespace Stratus.Core.Services;

/// <summary>
/// Append-only change timeline persisted as JSON. Newest first, capped so the
/// file stays small. History must never break a change, so all IO is best effort.
/// </summary>
public sealed class HistoryStore
{
    private const int MaxEntries = 500;

    private readonly string _filePath;
    private readonly object _lock = new();
    private List<HistoryEntry> _entries;

    public HistoryStore(string filePath)
    {
        _filePath = filePath;
        _entries = Load();
    }

    private List<HistoryEntry> Load() => JsonFile.Load(_filePath, "history file", () => new List<HistoryEntry>());

    private void Save() => JsonFile.Save(_filePath, _entries);

    public void Append(HistoryEntry entry)
    {
        lock (_lock)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);

            try { Save(); } catch { /* timeline is informational only */ }
        }
    }

    /// <summary>Newest first.</summary>
    public List<HistoryEntry> All()
    {
        lock (_lock)
            return [.. _entries];
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            try { Save(); } catch { /* best effort */ }
        }
    }
}

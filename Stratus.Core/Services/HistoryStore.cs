using Stratus.Core.Models;

namespace Stratus.Core.Services;

/// <summary>
/// Append-only change timeline persisted as JSON. Newest first, capped so the file stays
/// small. History must never break a change, so failures are only logged. Every mutation
/// re-reads the file under the shared state lock and persists the candidate before it
/// counts, so a second Stratus process cannot lose an entry and a failed save leaves no
/// in-memory entry that the file never received.
/// </summary>
public sealed class HistoryStore
{
    private const int MaxEntries = 500;

    private readonly string _filePath;

    public HistoryStore(string filePath)
    {
        _filePath = filePath;

        // Same early read the cached copy performed, so a broken file warns at startup.
        // Reads stay fresh; nothing is cached.
        JsonFile.ReadLatest(_filePath, "history file", () => new List<HistoryEntry>());
    }

    public void Append(HistoryEntry entry)
    {
        try
        {
            JsonFile.Update(_filePath, "history file", () => new List<HistoryEntry>(), entries =>
            {
                entries.Insert(0, entry);
                if (entries.Count > MaxEntries)
                    entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);

                return entries;
            });
        }
        catch (Exception ex) { Log.Warn("The change timeline could not be saved: " + ex.Message); }
    }

    /// <summary>Newest first.</summary>
    public List<HistoryEntry> All() =>
        JsonFile.ReadLatest(_filePath, "history file", () => new List<HistoryEntry>());

    public void Clear()
    {
        try
        {
            JsonFile.Update(_filePath, "history file", () => new List<HistoryEntry>(), entries =>
            {
                entries.Clear();
                return entries;
            });
        }
        catch (Exception ex) { Log.Warn("The change timeline could not be cleared: " + ex.Message); }
    }
}

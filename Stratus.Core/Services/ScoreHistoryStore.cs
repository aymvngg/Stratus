using Stratus.Core.Models;

namespace Stratus.Core.Services;

/// <summary>
/// Persists score snapshots for the Dashboard graph. A point is appended only when the score
/// actually changes, so the timeline stays readable. Capped, best-effort, never throws into
/// a scan. Every mutation re-reads the file under the shared state lock and persists before
/// it counts, so a failed save reports false instead of keeping a point the file never got.
/// </summary>
public sealed class ScoreHistoryStore
{
    private const int Cap = 500;

    private readonly string _filePath;

    public ScoreHistoryStore(string filePath)
    {
        _filePath = filePath;

        // Same early read the cached copy performed; reads stay fresh, nothing is cached.
        JsonFile.ReadLatest(_filePath, "Score history", () => new List<ScoreSnapshot>());
    }

    /// <summary>Appends a snapshot when the score differs from the latest one. True when a point was persisted.</summary>
    public bool AppendIfChanged(int score)
    {
        try
        {
            var added = false;
            JsonFile.Update(_filePath, "Score history", () => new List<ScoreSnapshot>(), entries =>
            {
                if (entries.Count > 0 && entries[^1].Score == score)
                    return null; // unchanged: nothing to persist

                added = true;
                entries.Add(new ScoreSnapshot(DateTime.UtcNow, score));
                if (entries.Count > Cap)
                    entries.RemoveRange(0, entries.Count - Cap);

                return entries;
            });
            return added;
        }
        catch (Exception ex)
        {
            // The graph is cosmetic. A failed write must never break a scan or a tweak.
            Log.Warn("The score history could not be saved: " + ex.Message);
            return false;
        }
    }

    /// <summary>All snapshots, oldest first, read fresh so another Stratus process's points are visible.</summary>
    public List<ScoreSnapshot> All() =>
        JsonFile.ReadLatest(_filePath, "Score history", () => new List<ScoreSnapshot>());

    public void Clear()
    {
        try
        {
            JsonFile.Update(_filePath, "Score history", () => new List<ScoreSnapshot>(), entries =>
            {
                entries.Clear();
                return entries;
            });
        }
        catch (Exception ex)
        {
            Log.Warn("The score history could not be cleared: " + ex.Message);
        }
    }
}

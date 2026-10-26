using Stratus.Core.Models;

namespace Stratus.Core.Services;

/// <summary>
/// Persists score snapshots for the Dashboard graph. A point is appended only when the
/// score actually changes, so the timeline stays readable. Capped, best-effort, never throws
/// into a scan.
/// </summary>
public sealed class ScoreHistoryStore
{
    private const int Cap = 500;

    private readonly string _filePath;
    private readonly object _lock = new();
    private List<ScoreSnapshot> _entries;

    public ScoreHistoryStore(string filePath)
    {
        _filePath = filePath;
        _entries = Load();
    }

    /// <summary>Appends a snapshot when the score differs from the latest one. True when a point was added.</summary>
    public bool AppendIfChanged(int score)
    {
        lock (_lock)
        {
            if (_entries.Count > 0 && _entries[^1].Score == score)
                return false;

            _entries.Add(new ScoreSnapshot(DateTime.UtcNow, score));
            if (_entries.Count > Cap)
                _entries.RemoveRange(0, _entries.Count - Cap);
            Save();
            return true;
        }
    }

    /// <summary>All snapshots, oldest first. Returns a copy.</summary>
    public List<ScoreSnapshot> All()
    {
        lock (_lock)
            return new List<ScoreSnapshot>(_entries);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            Save();
        }
    }

    private List<ScoreSnapshot> Load()
    {
        return JsonFile.Load(_filePath, "Score history", () => new List<ScoreSnapshot>());
    }

    private void Save()
    {
        try
        {
            JsonFile.Save(_filePath, _entries);
        }
        catch
        {
            // The graph is cosmetic. A failed write must never break a scan or a tweak.
        }
    }
}

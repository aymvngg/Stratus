using System.Text.Json;
using Stratus.Core.Models;

namespace Stratus.Core.Services;

public interface IBackupStore
{
    void Append(BackupEntry entry);
    bool HasEntries(string tweakId);
    BackupEntry? GetLatest(string tweakId, string scopeType, string scopeId, string key);
    List<BackupEntry> LatestPerKey(string tweakId);
    List<BackupEntry> All();
    void Remove(string tweakId);
}

/// <summary>Persists every pre-change system value to a JSON file so all tweaks are exactly reversible.</summary>
public sealed class BackupStore : IBackupStore
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private List<BackupEntry> _entries;

    public BackupStore(string filePath)
    {
        _filePath = filePath;
        _entries = Load();
    }

    private List<BackupEntry> Load() => JsonFile.Load(_filePath, "backup file", () => new List<BackupEntry>());

    private void Save() => JsonFile.Save(_filePath, _entries);

    public void Append(BackupEntry entry)
    {
        lock (_lock)
        {
            // Replace any previous backup for the exact same key from the same tweak.
            _entries.RemoveAll(e => e.TweakId == entry.TweakId
                                    && e.ScopeType == entry.ScopeType
                                    && e.ScopeId == entry.ScopeId
                                    && e.Key == entry.Key);
            _entries.Add(entry);
            Save();
        }
    }

    public bool HasEntries(string tweakId)
    {
        lock (_lock)
            return _entries.Any(e => e.TweakId == tweakId);
    }

    public BackupEntry? GetLatest(string tweakId, string scopeType, string scopeId, string key)
    {
        lock (_lock)
            return _entries
                .Where(e => e.TweakId == tweakId && e.ScopeType == scopeType && e.ScopeId == scopeId && e.Key == key)
                .OrderByDescending(e => e.SavedAtUtc)
                .FirstOrDefault();
    }

    public List<BackupEntry> LatestPerKey(string tweakId)
    {
        lock (_lock)
            return _entries
                .Where(e => e.TweakId == tweakId)
                .GroupBy(e => (e.ScopeType, e.ScopeId, e.Key))
                .Select(g => g.OrderByDescending(e => e.SavedAtUtc).First())
                .ToList();
    }

    public void Remove(string tweakId)
    {
        lock (_lock)
        {
            _entries.RemoveAll(e => e.TweakId == tweakId);
            Save();
        }
    }

    /// <summary>Every stored entry, newest first. Used by the backup viewer.</summary>
    public List<BackupEntry> All()
    {
        lock (_lock)
            return _entries.OrderByDescending(e => e.SavedAtUtc).ToList();
    }
}

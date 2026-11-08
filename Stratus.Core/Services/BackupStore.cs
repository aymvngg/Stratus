using System.Security.Cryptography;
using System.Text;
using Stratus.Core.Models;

namespace Stratus.Core.Services;

public interface IBackupStore
{
    /// <summary>
    /// Replaces any stored entry for the same tweak, scope and key. Fixture and viewer use
    /// only: durable originals must go through <see cref="TryAddOriginal"/>, which keeps the
    /// first saved original on a repeat apply.
    /// </summary>
    void Append(BackupEntry entry);

    /// <summary>
    /// Stores the entry only when no backup exists yet for the same tweak, scope and key.
    /// Returns true when it was stored. A repeat apply therefore keeps the first original.
    /// </summary>
    bool TryAddOriginal(BackupEntry entry);

    bool HasEntries(string tweakId);
    BackupEntry? GetLatest(string tweakId, string scopeType, string scopeId, string key);
    List<BackupEntry> LatestPerKey(string tweakId);
    List<BackupEntry> All();
    void Remove(string tweakId);
    void RemoveOne(string tweakId, string scopeType, string scopeId, string key);
}

/// <summary>
/// Persists every pre-change system value to a JSON file so all tweaks are exactly reversible.
/// Each change re-reads the file under a named mutex, applies the change to that fresh list,
/// saves the candidate and only then publishes it in memory. Another Stratus process (the CLI
/// next to the app) can therefore never lose an original to an older snapshot.
/// </summary>
public sealed class BackupStore : IBackupStore
{
    private readonly string _filePath;
    private readonly string _mutexName;
    private readonly object _lock = new();
    private List<BackupEntry> _entries = [];
    private (long Length, DateTime WriteUtc) _stamp;
    private bool _loaded;

    public BackupStore(string filePath)
    {
        _filePath = filePath;
        _mutexName = "StratusBackup_" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(filePath))))[..32];

        lock (_lock)
        {
            try
            {
                RefreshFromDisk();
            }
            catch (Exception ex)
            {
                // The file stays untouched. Every mutation retries the load and refuses to save
                // an empty list over it, so nothing is lost even when the read fails at startup.
                Log.Error("Backup file could not be loaded; the file was left untouched", ex);
            }
        }
    }

    private readonly struct FileLock : IDisposable
    {
        private readonly Mutex _mutex;

        public FileLock(Mutex mutex) => _mutex = mutex;

        public void Dispose()
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* already released */ }
            _mutex.Dispose();
        }
    }

    private FileLock EnterFileLock()
    {
        var mutex = new Mutex(false, _mutexName);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("Another Stratus process kept the backup file locked for 30 seconds.");
        }
        catch (AbandonedMutexException)
        {
            // The other process died while holding it; the lock is ours now.
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
        return new FileLock(mutex);
    }

    private (long Length, DateTime WriteUtc) CurrentStamp()
    {
        var info = new FileInfo(_filePath);
        return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (-1, DateTime.MinValue);
    }

    private void RefreshFromDisk()
    {
        if (_loaded && CurrentStamp() == _stamp)
            return;

        _entries = JsonFile.Load(_filePath, "backup file", () => new List<BackupEntry>(), required: true);
        _stamp = CurrentStamp();
        _loaded = true;
    }

    private void Mutate(Action<List<BackupEntry>> change)
    {
        lock (_lock)
        {
            using var fileLock = EnterFileLock();
            RefreshFromDisk();

            var candidate = _entries.ToList();
            change(candidate);

            // Save first: a failed write leaves the published list and the file unchanged.
            JsonFile.Save(_filePath, candidate);
            _entries = candidate;
            _stamp = CurrentStamp();
            _loaded = true;
        }
    }

    private T Read<T>(Func<List<BackupEntry>, T> read, T fallback)
    {
        lock (_lock)
        {
            using var fileLock = EnterFileLock();

            try
            {
                RefreshFromDisk();
            }
            catch (Exception ex) when (_loaded)
            {
                // The last successfully loaded copy still has every saved original. Serving it
                // is stale but safe; serving an empty list would let a revert fall back to a
                // hardcoded default and overwrite the real original.
                Log.Error("Backup file could not be re-read; using the last loaded copy", ex);
            }

            return _loaded ? read(_entries) : fallback;
        }
    }

    /// <summary>Replaces the entry for the same tweak, scope and key. Never use for durable originals.</summary>
    public void Append(BackupEntry entry) => Mutate(entries =>
    {
        entries.RemoveAll(e => e.TweakId == entry.TweakId
                               && e.ScopeType == entry.ScopeType
                               && e.ScopeId == entry.ScopeId
                               && e.Key == entry.Key);
        entries.Add(entry);
    });

    public bool TryAddOriginal(BackupEntry entry)
    {
        var added = false;
        Mutate(entries =>
        {
            if (entries.Any(e => e.TweakId == entry.TweakId
                                 && e.ScopeType == entry.ScopeType
                                 && e.ScopeId == entry.ScopeId
                                 && e.Key == entry.Key))
                return;

            entries.Add(entry);
            added = true;
        });
        return added;
    }

    public bool HasEntries(string tweakId) =>
        Read(entries => entries.Any(e => e.TweakId == tweakId), false);

    public BackupEntry? GetLatest(string tweakId, string scopeType, string scopeId, string key) =>
        Read(entries => entries
            .Where(e => e.TweakId == tweakId && e.ScopeType == scopeType && e.ScopeId == scopeId && e.Key == key)
            .OrderByDescending(e => e.SavedAtUtc)
            .FirstOrDefault(), null);

    public List<BackupEntry> LatestPerKey(string tweakId) =>
        Read(entries => entries
            .Where(e => e.TweakId == tweakId)
            .GroupBy(e => (e.ScopeType, e.ScopeId, e.Key))
            .Select(g => g.OrderByDescending(e => e.SavedAtUtc).First())
            .ToList(), []);

    public void Remove(string tweakId) =>
        Mutate(entries => entries.RemoveAll(e => e.TweakId == tweakId));

    public void RemoveOne(string tweakId, string scopeType, string scopeId, string key) =>
        Mutate(entries => entries.RemoveAll(e => e.TweakId == tweakId
                                                && e.ScopeType == scopeType
                                                && e.ScopeId == scopeId
                                                && e.Key == key));

    /// <summary>Every stored entry, newest first. Used by the backup viewer.</summary>
    public List<BackupEntry> All() =>
        Read(entries => entries.OrderByDescending(e => e.SavedAtUtc).ToList(), []);
}

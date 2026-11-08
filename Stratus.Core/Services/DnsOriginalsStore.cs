using System.Security.Cryptography;
using System.Text;

namespace Stratus.Core.Services;

internal interface IDnsOriginalsStore
{
    /// <summary>Reads the saved originals. A corrupt file is an error, never an empty result.</summary>
    Dictionary<string, DnsAdapterOriginal> Load();

    void Save(IReadOnlyDictionary<string, DnsAdapterOriginal> originals);

    /// <summary>
    /// Takes the cross-process lock for a whole apply or revert, so the app and the CLI cannot
    /// interleave read-modify-write and leave a stale original behind. Null means the lock could
    /// not be taken and the operation must not change anything.
    /// </summary>
    IDisposable? TryBeginTransaction();
}

/// <summary>
/// Durable originals for the DNS adapters Stratus has changed. Saved through <see cref="JsonFile"/>
/// before the first netsh call so a crash cannot leave the adapter without a recoverable original.
/// </summary>
internal sealed class DnsOriginalsStore : IDnsOriginalsStore
{
    public static string DefaultPath => Path.Combine(AppPaths.ProgramData, "dns-originals.json");

    private readonly string _path;
    private readonly TimeSpan _lockTimeout;

    public DnsOriginalsStore(string path, TimeSpan? lockTimeout = null)
    {
        _path = path;
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(15);
    }

    public Dictionary<string, DnsAdapterOriginal> Load() =>
        JsonFile.Load(_path, "DNS originals file",
            () => new Dictionary<string, DnsAdapterOriginal>(StringComparer.OrdinalIgnoreCase),
            required: true);

    public void Save(IReadOnlyDictionary<string, DnsAdapterOriginal> originals) =>
        JsonFile.Save(_path, originals);

    public IDisposable? TryBeginTransaction()
    {
        var mutex = TryCreateMutex(out var name);
        if (mutex is null)
        {
            // A named mutex the runtime refuses to create cannot coordinate processes, so the
            // caller is told rather than being allowed to risk a stale original.
            Log.Error($"Could not create the DNS originals lock '{name}'.");
            return null;
        }

        try
        {
            if (!mutex.WaitOne(_lockTimeout))
            {
                mutex.Dispose();
                Log.Warn("Another Stratus process held the DNS originals lock for too long.");
                return null;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died while holding the lock. We own it now, and the file is
            // still validated by Load before any change.
        }
        catch (Exception ex)
        {
            Log.Error("Could not take the DNS originals lock", ex);
            mutex.Dispose();
            return null;
        }

        return new Transaction(mutex);
    }

    private Mutex? TryCreateMutex(out string name)
    {
        name = MutexName(_path);
        try
        {
            return new Mutex(initiallyOwned: false, name);
        }
        catch (Exception ex)
        {
            Log.Warn($"Named mutex unavailable, falling back to a process-local lock: {ex.Message}");
            name = "";
            return null;
        }
    }

    /// <summary>Stable across processes: string hashing is randomized per process, a file digest is not.</summary>
    private static string MutexName(string path)
    {
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..16];
        return "Global\\Stratus-DnsOriginals-" + digest;
    }

    private sealed class Transaction : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _released;

        public Transaction(Mutex mutex) => _mutex = mutex;

        public void Dispose()
        {
            if (_released)
                return;

            _released = true;
            try { _mutex.ReleaseMutex(); } catch { /* not owned any more */ }
            _mutex.Dispose();
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Stratus.Core.Services;

/// <summary>
/// Atomic JSON persistence shared by the small stores (backups, history, scores, settings).
///
/// A save goes to a temporary file that is then moved over the real one, so a crash, a full
/// disk or a second Stratus process can never leave a half written file behind. Loads move an
/// unreadable file aside as ".corrupt" instead of returning an empty list that the next save
/// would silently make permanent.
/// </summary>
internal static class JsonFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <param name="required">
    /// When true an unreadable file is left in place and the error is thrown instead of
    /// falling back to an empty value. Stores that must never lose data use this before
    /// a read-modify-write, so an unreadable file cannot be replaced by an empty one.
    /// </param>
    public static T Load<T>(string path, string label, Func<T> empty, bool required = false)
    {
        try
        {
            if (Directory.Exists(path))
                throw new IOException("The state file path is a directory.");
            if (!File.Exists(path))
                return empty();

            var json = File.ReadAllText(path);
            var value = JsonSerializer.Deserialize<T>(json, Options);
            if (value is null && required)
                throw new InvalidDataException("The state file contains null instead of saved state.");
            return value is null ? empty() : value;
        }
        catch (Exception ex) when (!required && IsCorruptContent(ex))
        {
            Log.Warn($"{label} could not be read, starting empty: {ex.Message}");
            Quarantine(path, label);
            return empty();
        }
        catch (Exception ex) when (!required)
        {
            // A transient IO failure (a locked file, missing permissions) is not proof the
            // content is bad, so the file stays untouched. Serving empty here is safe because
            // every mutation re-reads the file and refuses to save over one it could not read.
            Log.Warn($"{label} could not be read, starting empty: {ex.Message}");
            return empty();
        }
        catch (Exception ex)
        {
            Log.Error($"{label} could not be read and was left untouched", ex);
            throw new InvalidOperationException(
                $"{label} could not be read. It was left untouched so no saved value is lost.", ex);
        }
    }

    /// <summary>Only unreadable content may be quarantined. Locked or unreadable files keep their bytes.</summary>
    private static bool IsCorruptContent(Exception ex) => ex is JsonException or InvalidDataException;

    /// <summary>
    /// Read-modify-write for a small state file, exclusive across all Stratus processes.
    /// Re-reads the latest file content, applies change to it, and saves the candidate before
    /// returning it, so the caller only publishes what the file now holds. When change returns
    /// null nothing is saved and the loaded value comes back. Throws when the file cannot be
    /// read or written, so the caller keeps its previous state instead of drifting from disk.
    /// </summary>
    public static T? Update<T>(string path, string label, Func<T> empty, Func<T, T?> change) where T : class
    {
        using var fileLock = EnterStateLock(path);
        var latest = Load(path, label, empty, required: true);
        var candidate = change(latest);
        if (candidate is null)
            return latest;

        Save(path, candidate);
        return candidate;
    }

    /// <summary>The latest file content, read under the state lock so a concurrent save cannot be half seen.</summary>
    public static T ReadLatest<T>(string path, string label, Func<T> empty) where T : class
    {
        using var fileLock = EnterStateLock(path);
        return Load(path, label, empty);
    }

    private static StateFileLock EnterStateLock(string path)
    {
        var mutex = new Mutex(false, "StratusState_" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..32]);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("Another Stratus process kept the state file locked for 30 seconds.");
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
        return new StateFileLock(mutex);
    }

    public static void Save<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // A unique temp name per save: the app and the CLI can persist to the same
        // ProgramData file, and a shared ".tmp" name would let them overwrite each other.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort cleanup */ }
            throw;
        }
    }

    private static void Quarantine(string path, string label)
    {
        try
        {
            if (!File.Exists(path))
                return;

            var broken = path + ".corrupt";
            File.Move(path, broken, overwrite: true);
            Log.Warn($"The unreadable {label} was moved to {broken}.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not move the unreadable {label} aside: {ex.Message}");
        }
    }
}

/// <summary>Releases the cross-process state file mutex exactly once.</summary>
internal readonly struct StateFileLock : IDisposable
{
    private readonly Mutex _mutex;

    public StateFileLock(Mutex mutex) => _mutex = mutex;

    public void Dispose()
    {
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* already released */ }
        _mutex.Dispose();
    }
}

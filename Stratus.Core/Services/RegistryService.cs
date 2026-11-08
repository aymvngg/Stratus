using System.Globalization;
using Microsoft.Win32;
using Stratus.Core.Models;

namespace Stratus.Core.Services;

public sealed record RegistryRead(bool Exists, RegistryValueKind Kind, object? Value)
{
    /// <summary>
    /// True when the value could not be read at all (access denied, unreadable hive, bad key).
    /// <see cref="Exists"/> is false in that case too, for callers that only ask "can I use it",
    /// but those callers must not treat the pair as proof the value is missing. Anything that
    /// writes, snapshots or restores must check this flag (or use ReadRequired).
    /// </summary>
    public bool ReadFailed { get; init; }

    public bool IsKnown => !ReadFailed;
}

/// <summary>A registry value could not be read, so changing it cannot be safe.</summary>
public sealed class RegistryReadException : Exception
{
    public RegistryReadException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// All registry access goes through here so every write is backed up first
/// and every change is exactly reversible.
/// </summary>
public sealed class RegistryService
{
    private readonly IBackupStore _backup;
    private readonly Func<RegistryHive, string, string, RegistryRead> _read;
    public const string ScopeType = "registry";

    public RegistryService(IBackupStore backup) : this(backup, ReadFromRegistry) { }

    /// <summary>Injection seam for tests that need a read to fail without a real unreadable key.</summary>
    internal RegistryService(IBackupStore backup, Func<RegistryHive, string, string, RegistryRead> read)
    {
        _backup = backup;
        _read = read;
    }

    private static RegistryKey OpenBase(RegistryHive hive) => hive switch
    {
        RegistryHive.LocalMachine => Registry.LocalMachine,
        RegistryHive.CurrentUser => Registry.CurrentUser,
        RegistryHive.Users => Registry.Users,
        RegistryHive.PerformanceData => throw new NotSupportedException(),
        RegistryHive.CurrentConfig => Registry.CurrentConfig,
        _ => throw new NotSupportedException($"Hive {hive} not supported")
    };

    /// <summary>
    /// Reads a value. An unreadable key is reported as <see cref="RegistryRead.ReadFailed"/>, never
    /// as a plain "absent", so callers that write or restore can refuse to treat unknown state as
    /// missing. Display code still sees <see cref="RegistryRead.Exists"/> false.
    /// </summary>
    public RegistryRead Read(RegistryHive hive, string path, string name)
    {
        var read = _read(hive, path, name);
        if (read.ReadFailed)
            Log.Warn($"Could not read {hive}\\{path}\\{name}.");
        return read;
    }

    private static RegistryRead ReadFromRegistry(RegistryHive hive, string path, string name)
    {
        try
        {
            using var key = OpenBase(hive).OpenSubKey(path, writable: false);
            if (key is null)
                return new RegistryRead(false, RegistryValueKind.None, null);

            var value = key.GetValue(name);
            if (value is null)
                return new RegistryRead(false, RegistryValueKind.None, null);

            return new RegistryRead(true, key.GetValueKind(name), value);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read {hive}\\{path}\\{name}: {ex.Message}");
            return new RegistryRead(false, RegistryValueKind.None, null) { ReadFailed = true };
        }
    }

    /// <summary>
    /// Reads a value and throws when it cannot be read. Used before every write and backup so an
    /// unknown original is never mistaken for a missing one.
    /// </summary>
    public RegistryRead ReadRequired(RegistryHive hive, string path, string name)
    {
        var read = Read(hive, path, name);
        if (read.ReadFailed)
            throw new RegistryReadException($"The registry value {hive}\\{path}\\{name} could not be read, so it was not changed.");
        return read;
    }

    public bool ValueEquals(RegistryHive hive, string path, string name, RegistryValueKind kind, object? expected)
    {
        var read = Read(hive, path, name);
        if (read.ReadFailed || !read.Exists)
            return false;
        if (read.Kind != kind)
            return false;
        return ValuesEqual(kind, read.Value, expected);
    }

    public static bool ValuesEqual(RegistryValueKind kind, object? actual, object? expected) => kind switch
    {
        RegistryValueKind.DWord => Convert.ToInt64(actual, CultureInfo.InvariantCulture) == Convert.ToInt64(expected, CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => Convert.ToInt64(actual, CultureInfo.InvariantCulture) == Convert.ToInt64(expected, CultureInfo.InvariantCulture),
        RegistryValueKind.String or RegistryValueKind.ExpandString => string.Equals((string?)actual, (string?)expected, StringComparison.Ordinal),
        RegistryValueKind.Binary => (actual as byte[]) is { } a && (expected as byte[]) is { } b && a.AsSpan().SequenceEqual(b),
        RegistryValueKind.MultiString => (actual as string[]) is { } m && (expected as string[]) is { } n && m.SequenceEqual(n),
        _ => Equals(actual, expected)
    };

    /// <summary>Converts a user-facing value into the exact type RegistryKey.SetValue expects for the kind.</summary>
    public static object Normalize(RegistryValueKind kind, object? value) => kind switch
    {
        RegistryValueKind.DWord => Convert.ToInt32(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        RegistryValueKind.QWord => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        RegistryValueKind.String or RegistryValueKind.ExpandString => (string)value!,
        RegistryValueKind.Binary => (byte[])value!,
        RegistryValueKind.MultiString => (string[])value!,
        _ => value!
    };

    private static string SerializeValue(RegistryValueKind kind, object? value) => kind switch
    {
        RegistryValueKind.DWord or RegistryValueKind.QWord => Convert.ToString(Convert.ToInt64(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)!,
        RegistryValueKind.String or RegistryValueKind.ExpandString => (string?)value ?? "",
        RegistryValueKind.Binary => Convert.ToBase64String((byte[]?)value ?? []),
        RegistryValueKind.MultiString => string.Join("\u0001", (string[]?)value ?? []),
        _ => value?.ToString() ?? ""
    };

    private static object? DeserializeValue(RegistryValueKind kind, string json) => kind switch
    {
        RegistryValueKind.DWord => Convert.ToInt32(long.Parse(json, CultureInfo.InvariantCulture)),
        RegistryValueKind.QWord => long.Parse(json, CultureInfo.InvariantCulture),
        RegistryValueKind.String or RegistryValueKind.ExpandString => json,
        RegistryValueKind.Binary => Convert.FromBase64String(json),
        RegistryValueKind.MultiString => json.Split('\u0001'),
        _ => json
    };

    internal static string ScopeIdOf(RegistryHive hive, string path, string name) => $"{hive}|{path}|{name}";

    /// <summary>
    /// Snapshot of the live value, shaped like a stored backup so it can be restored the same way.
    /// Throws when the value cannot be read: a snapshot that claims "did not exist" would make a
    /// later rollback delete a value that is really there.
    /// </summary>
    internal BackupEntry Snapshot(RegistryHive hive, string path, string name, string tweakId)
    {
        var current = ReadRequired(hive, path, name);
        return new BackupEntry
        {
            TweakId = tweakId,
            ScopeType = ScopeType,
            ScopeId = ScopeIdOf(hive, path, name),
            Key = name,
            ValueKind = current.Exists ? ((int)current.Kind).ToString(CultureInfo.InvariantCulture) : null,
            ValueJson = current.Exists ? SerializeValue(current.Kind, current.Value) : null,
            Existed = current.Exists
        };
    }

    /// <summary>Stores the live value as the durable original unless one is already saved. Returns true when added.</summary>
    internal bool RecordOriginal(RegistryHive hive, string path, string name, string tweakId) =>
        _backup.TryAddOriginal(Snapshot(hive, path, name, tweakId));

    /// <summary>
    /// Writes a value after saving its original. A repeat write of the same value does nothing,
    /// so the first saved original survives every later apply. Returns true when the value changed.
    /// </summary>
    public bool WriteValue(RegistryHive hive, string path, string name, RegistryValueKind kind, object? value, string tweakId)
    {
        // Read before comparing or saving: an unreadable value must stop the change, not look
        // like a missing one that is safe to overwrite.
        var current = ReadRequired(hive, path, name);
        if (current.Exists && current.Kind == kind && ValuesEqual(kind, current.Value, value))
            return false;

        var attempt = BeginAttempt(tweakId);
        try
        {
            attempt.Write(hive, path, name, kind, value);
            attempt.Commit();
            return true;
        }
        catch
        {
            // The write failed, so an original saved above would restore a value that was
            // never changed. Roll back and drop it before the failure surfaces.
            attempt.Rollback();
            throw;
        }
    }

    /// <summary>Writes without touching the backup file. Only a registry attempt rollback uses this.</summary>
    internal void WriteValueRaw(RegistryHive hive, string path, string name, RegistryValueKind kind, object? value)
    {
        using var key = EnsureKey(hive, path);
        key.SetValue(name, Normalize(kind, value), kind);
    }

    public bool DeleteValue(RegistryHive hive, string path, string name, string tweakId)
    {
        if (!ReadRequired(hive, path, name).Exists)
            return false;

        var attempt = BeginAttempt(tweakId);
        try
        {
            attempt.Delete(hive, path, name);
            attempt.Commit();
            return true;
        }
        catch
        {
            attempt.Rollback();
            throw;
        }
    }

    /// <summary>Restores one snapshot. Used by attempt rollback and by revert.</summary>
    internal void RestoreEntry(BackupEntry entry)
    {
        var parts = entry.ScopeId.Split('|', 3);
        if (parts.Length != 3)
            throw new FormatException($"Malformed scope id '{entry.ScopeId}'.");

        var hive = (RegistryHive)Enum.Parse(typeof(RegistryHive), parts[0]);
        var path = parts[1];
        var name = parts[2];

        if (!entry.Existed)
        {
            using var key = OpenBase(hive).OpenSubKey(path, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
            return;
        }

        var kind = (RegistryValueKind)int.Parse(
            entry.ValueKind ?? ((int)RegistryValueKind.String).ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture);
        var value = DeserializeValue(kind, entry.ValueJson ?? "");
        using var keyForWrite = EnsureKey(hive, path);
        keyForWrite.SetValue(name, Normalize(kind, value), kind);
    }

    public RegistryKey EnsureKey(RegistryHive hive, string path) =>
        OpenBase(hive).CreateSubKey(path, writable: true) ?? throw new InvalidOperationException($"Failed to open/create {hive}\\{path}");

    /// <summary>
    /// Starts an attempt. Values written through the attempt record their pre-write state, so a
    /// failed multi-value apply can put the machine back exactly as it was. Durable originals are
    /// kept, and only the originals first saved by this attempt are dropped once the rollback worked.
    /// </summary>
    public RegistryAttempt BeginAttempt(string tweakId) => new(this, tweakId);

    /// <summary>
    /// Restores every backed-up value for a tweak (latest per key) and drops those backups once
    /// everything was restored. A single bad entry is logged, its backup kept, and false returned
    /// so the caller can report the partial failure and retry later.
    /// </summary>
    public bool RestoreAll(string tweakId) => RestoreAll(tweakId, null);

    internal bool RestoreAll(string tweakId, Func<bool>? completeRestore)
    {
        var entries = _backup.LatestPerKey(tweakId)
            .Where(e => e.ScopeType == ScopeType)
            .ToList();
        var failed = new List<string>();
        var restored = new List<BackupEntry>();

        foreach (var entry in entries)
        {
            try
            {
                RestoreEntry(entry);
                restored.Add(entry);
            }
            catch (Exception ex)
            {
                failed.Add($"{entry.ScopeId}: {ex.Message}");
            }
        }

        if (failed.Count > 0)
        {
            // Every backup stays: a retry restores the values that already match again, which
            // is harmless, and a partial revert must never drop an original.
            Log.Warn($"Restore of '{tweakId}' left {failed.Count} value(s) unrestored: {string.Join(" | ", failed)}");
            return false;
        }

        if (completeRestore is not null)
        {
            try
            {
                if (!completeRestore())
                    return false;
            }
            catch (Exception ex)
            {
                Log.Error($"Completing restore of '{tweakId}' failed; its originals stay backed up", ex);
                return false;
            }
        }

        foreach (var entry in restored)
            _backup.RemoveOne(tweakId, ScopeType, entry.ScopeId, entry.Key);

        return true;
    }

    public bool HasBackups(string tweakId) => _backup.HasEntries(tweakId);

    /// <summary>A saved original for a value outside the registry scope, e.g. the active power plan.</summary>
    public BackupEntry? SavedOriginal(string tweakId, string scopeType, string scopeId, string key) =>
        _backup.GetLatest(tweakId, scopeType, scopeId, key);

    /// <summary>Stores the original unless one is already saved. True when this call added it.</summary>
    public bool SaveOriginal(BackupEntry entry) => _backup.TryAddOriginal(entry);
    public void RemoveSavedOriginal(string tweakId, string scopeType, string scopeId, string key) =>
        _backup.RemoveOne(tweakId, scopeType, scopeId, key);

    /// <summary>True when this tweak has an original saved for the value.</summary>
    public bool HasBackup(string tweakId, RegistryHive hive, string path, string name) =>
        _backup.GetLatest(tweakId, ScopeType, ScopeIdOf(hive, path, name), name) is not null;

    /// <summary>
    /// Every registry value with an original saved by any tweak. Two catalog entries can set the
    /// same value, so a revert must not replace the other entry's original with a declared default.
    /// </summary>
    public IReadOnlySet<string> OwnedScopeIds() =>
        _backup.All()
            .Where(e => e.ScopeType == ScopeType)
            .Select(e => e.ScopeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal void RemoveBackup(string tweakId, string scopeId, string key) =>
        _backup.RemoveOne(tweakId, ScopeType, scopeId, key);
}

/// <summary>
/// One apply or revert attempt on a tweak's registry values. Each write records the value that
/// was there before, so <see cref="Rollback"/> can undo the attempt without touching the durable
/// originals kept for a later revert. Dispose without an explicit result keeps the originals:
/// losing one is worse than keeping an extra.
/// </summary>
public sealed class RegistryAttempt : IDisposable
{
    private readonly RegistryService _registry;
    private readonly string _tweakId;
    private readonly List<BackupEntry> _before = [];
    private readonly List<(string ScopeId, string Key)> _added = [];
    private bool _closed;

    internal RegistryAttempt(RegistryService registry, string tweakId)
    {
        _registry = registry;
        _tweakId = tweakId;
    }

    public void Write(RegistryHive hive, string path, string name, RegistryValueKind kind, object? value)
    {
        var before = _registry.Snapshot(hive, path, name, _tweakId);
        var added = _registry.RecordOriginal(hive, path, name, _tweakId);
        _before.Add(before);
        if (added)
            _added.Add((before.ScopeId, name));

        if (!_registry.ValueEquals(hive, path, name, kind, value))
            _registry.WriteValueRaw(hive, path, name, kind, value);
    }

    public void Delete(RegistryHive hive, string path, string name)
    {
        var current = _registry.ReadRequired(hive, path, name);
        if (!current.Exists)
            return;

        var before = _registry.Snapshot(hive, path, name, _tweakId);
        var added = _registry.RecordOriginal(hive, path, name, _tweakId);
        _before.Add(before);
        if (added)
            _added.Add((before.ScopeId, name));

        using var key = OpenWritable(hive, path);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    private static Microsoft.Win32.RegistryKey? OpenWritable(RegistryHive hive, string path) => hive switch
    {
        RegistryHive.LocalMachine => Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path, writable: true),
        RegistryHive.CurrentUser => Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path, writable: true),
        RegistryHive.Users => Microsoft.Win32.Registry.Users.OpenSubKey(path, writable: true),
        RegistryHive.CurrentConfig => Microsoft.Win32.Registry.CurrentConfig.OpenSubKey(path, writable: true),
        _ => null
    };

    /// <summary>Undoes every value this attempt wrote. True when the machine is back at its starting state.</summary>
    public bool Rollback()
    {
        _closed = true;
        var failed = false;

        for (var i = _before.Count - 1; i >= 0; i--)
        {
            try
            {
                _registry.RestoreEntry(_before[i]);
            }
            catch (Exception ex)
            {
                failed = true;
                Log.Warn($"Could not roll back '{_before[i].ScopeId}': {ex.Message}");
            }
        }

        if (failed)
        {
            // Keep the originals this attempt saved: a later revert can still use them.
            Log.Warn($"Rollback of '{_tweakId}' was incomplete; the saved originals were kept.");
            return false;
        }

        foreach (var (scopeId, key) in _added)
            _registry.RemoveBackup(_tweakId, scopeId, key);

        return true;
    }

    /// <summary>The attempt succeeded: the saved originals stay for the next revert.</summary>
    public void Commit() => _closed = true;

    public void Dispose()
    {
        if (!_closed)
            Commit();
    }
}

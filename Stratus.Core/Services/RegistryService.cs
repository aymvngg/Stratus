using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;
using Stratus.Core.Models;

namespace Stratus.Core.Services;

public sealed record RegistryRead(bool Exists, RegistryValueKind Kind, object? Value);

/// <summary>
/// All registry access goes through here so every write is backed up first
/// and every change is exactly reversible.
/// </summary>
public sealed class RegistryService
{
    private readonly IBackupStore _backup;
    public const string ScopeType = "registry";

    public RegistryService(IBackupStore backup) => _backup = backup;

    private static RegistryKey OpenBase(RegistryHive hive) => hive switch
    {
        RegistryHive.LocalMachine => Registry.LocalMachine,
        RegistryHive.CurrentUser => Registry.CurrentUser,
        RegistryHive.Users => Registry.Users,
        RegistryHive.PerformanceData => throw new NotSupportedException(),
        RegistryHive.CurrentConfig => Registry.CurrentConfig,
        _ => throw new NotSupportedException($"Hive {hive} not supported")
    };

    public RegistryRead Read(RegistryHive hive, string path, string name)
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
        catch
        {
            return new RegistryRead(false, RegistryValueKind.None, null);
        }
    }

    public bool ValueEquals(RegistryHive hive, string path, string name, RegistryValueKind kind, object? expected)
    {
        var read = Read(hive, path, name);
        if (!read.Exists)
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

    public void WriteValue(RegistryHive hive, string path, string name, RegistryValueKind kind, object? value, string tweakId)
    {
        Backup(hive, path, name, kind, tweakId);

        using var key = EnsureKey(hive, path);
        key.SetValue(name, Normalize(kind, value), kind);
    }

    public void WriteValueRaw(RegistryHive hive, string path, string name, RegistryValueKind kind, object? value)
    {
        using var key = EnsureKey(hive, path);
        key.SetValue(name, Normalize(kind, value), kind);
    }

    public void DeleteValue(RegistryHive hive, string path, string name, string tweakId)
    {
        Backup(hive, path, name, RegistryValueKind.None, tweakId);

        using var key = OpenBase(hive).OpenSubKey(path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    private void Backup(RegistryHive hive, string path, string name, RegistryValueKind kind, string tweakId)
    {
        var current = Read(hive, path, name);
        _backup.Append(new BackupEntry
        {
            TweakId = tweakId,
            ScopeType = ScopeType,
            ScopeId = $"{hive}|{path}|{name}",
            Key = name,
            ValueKind = current.Exists ? ((int)readKindSafe(current, kind)).ToString(CultureInfo.InvariantCulture) : null,
            ValueJson = current.Exists ? SerializeValue(current.Kind, current.Value) : null,
            Existed = current.Exists
        });
    }

    private static RegistryValueKind readKindSafe(RegistryRead read, RegistryValueKind fallback) =>
        read.Exists ? read.Kind : fallback;

    public RegistryKey EnsureKey(RegistryHive hive, string path) =>
        OpenBase(hive).CreateSubKey(path, writable: true) ?? throw new InvalidOperationException($"Failed to open/create {hive}\\{path}");

    /// <summary>
    /// Restores every backed-up value for a tweak (latest per key) and drops the backups once
    /// everything was restored. A single bad entry (malformed scope id, access denied) no longer
    /// aborts the whole revert or silently discards the remaining backups: it is logged, the
    /// backups are kept and false is returned so the caller can report the partial failure.
    /// </summary>
    public bool RestoreAll(string tweakId)
    {
        var entries = _backup.LatestPerKey(tweakId).Where(e => e.ScopeType == ScopeType).ToList();
        var failed = new List<string>();

        foreach (var entry in entries)
        {
            try
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
                }
                else
                {
                    var kind = (RegistryValueKind)int.Parse(entry.ValueKind ?? ((int)RegistryValueKind.String).ToString(), CultureInfo.InvariantCulture);
                    var value = DeserializeValue(kind, entry.ValueJson ?? "");
                    using var key = EnsureKey(hive, path);
                    key.SetValue(name, Normalize(kind, value), kind);
                }
            }
            catch (Exception ex)
            {
                failed.Add($"{entry.ScopeId}: {ex.Message}");
            }
        }

        if (failed.Count == 0)
        {
            _backup.Remove(tweakId);
            return true;
        }

        Log.Warn($"Restore of '{tweakId}' left {failed.Count} value(s) unrestored: {string.Join(" | ", failed)}");
        return false;
    }

    public bool HasBackups(string tweakId) => _backup.HasEntries(tweakId);
}

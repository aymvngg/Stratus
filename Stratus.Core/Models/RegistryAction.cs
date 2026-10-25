using Microsoft.Win32;

namespace Stratus.Core.Models;

public enum RegistryOp
{
    SetValue,
    DeleteValue
}

/// <summary>
/// One registry value change. <see cref="TweakedValue"/> is written on Apply;
/// on Revert the backed-up original is restored, falling back to <see cref="DefaultValue"/>.
/// </summary>
public sealed record RegistryAction
{
    public required RegistryHive Hive { get; init; }
    public required string Path { get; init; }
    public required string Name { get; init; }
    public RegistryOp Op { get; init; } = RegistryOp.SetValue;
    public RegistryValueKind Kind { get; init; } = RegistryValueKind.DWord;
    public object? TweakedValue { get; init; }
    public object? DefaultValue { get; init; }

    /// <summary>For SetValue ops: a missing value counts as applied (e.g. defaults that are "on").</summary>
    public bool MissingMeansApplied { get; init; }

    public static RegistryAction SetDword(RegistryHive hive, string path, string name, int tweaked, int? @default = null, bool missingMeansApplied = false) =>
        new() { Hive = hive, Path = path, Name = name, Kind = RegistryValueKind.DWord, TweakedValue = tweaked, DefaultValue = @default, MissingMeansApplied = missingMeansApplied };

    public static RegistryAction SetString(RegistryHive hive, string path, string name, string tweaked, string? @default = null, RegistryValueKind kind = RegistryValueKind.String, bool missingMeansApplied = false) =>
        new() { Hive = hive, Path = path, Name = name, Kind = kind, TweakedValue = tweaked, DefaultValue = @default, MissingMeansApplied = missingMeansApplied };

    public static RegistryAction SetBinary(RegistryHive hive, string path, string name, byte[] tweaked, byte[]? @default = null, bool missingMeansApplied = false) =>
        new() { Hive = hive, Path = path, Name = name, Kind = RegistryValueKind.Binary, TweakedValue = tweaked, DefaultValue = @default, MissingMeansApplied = missingMeansApplied };
}

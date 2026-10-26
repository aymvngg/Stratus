using Microsoft.Win32;
using Stratus.Core.Models;

namespace Stratus.Core.Services;

/// <summary>One thing that launches with Windows.</summary>
public sealed record StartupItem(
    string Id,
    string Name,
    string Command,
    string Scope,
    string Source,
    RegistryHive Hive,
    string ApprovedPath,
    string ValueName,
    bool IsEnabled);

/// <summary>
/// Reads and toggles startup entries the same way Task Manager does: the command
/// itself is never touched, only the StartupApproved blob (02 = enabled, 03 = disabled
/// plus a timestamp). That makes every toggle instantly reversible and keeps the
/// original command intact even if the app is uninstalled later.
/// </summary>
public sealed class StartupService
{
    private const string ApprovedRunUser = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedRunMachine = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedRun32 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private const string ApprovedFolderUser = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    private const string ApprovedFolderMachine = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    private sealed record Location(
        RegistryHive Hive,
        string RunPath,
        string ApprovedPath,
        string Scope,
        string Source);

    private static readonly Location[] RunLocations =
    [
        new(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run",
            ApprovedRunUser, "Current user", "Registry"),
        new(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            ApprovedRunMachine, "All users", "Registry"),
        new(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
            ApprovedRun32, "All users", "Registry 32-bit"),
    ];

    private readonly RegistryService _reg;

    public StartupService(RegistryService reg) => _reg = reg;

    private static RegistryKey OpenBase(RegistryHive hive) => hive switch
    {
        RegistryHive.LocalMachine => Registry.LocalMachine,
        RegistryHive.CurrentUser => Registry.CurrentUser,
        _ => throw new NotSupportedException($"Hive {hive} not supported"),
    };

    public static string TweakId(StartupItem item)
    {
        var slug = new string(item.Id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray());
        return "startup-" + slug;
    }

    public List<StartupItem> Scan()
    {
        var items = new List<StartupItem>();

        foreach (var location in RunLocations)
        {
            try
            {
                using var key = OpenBase(location.Hive).OpenSubKey(location.RunPath, writable: false);
                if (key is null)
                    continue;

                foreach (var name in key.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    items.Add(new StartupItem(
                        // The RunPath is part of the id: HKLM\...\Run and HKLM\...\WOW6432Node\...\Run
                        // can hold entries with the same name, and they must not share a backup.
                        Id: $"run:{location.Hive}:{location.RunPath}:{name}",
                        Name: name,
                        Command: key.GetValue(name)?.ToString() ?? "",
                        Scope: location.Scope,
                        Source: location.Source,
                        Hive: location.Hive,
                        ApprovedPath: location.ApprovedPath,
                        ValueName: name,
                        IsEnabled: IsEnabled(location.Hive, location.ApprovedPath, name)));
                }
            }
            catch
            {
                // A missing or unreadable key simply contributes no entries.
            }
        }

        AddFolderItems(items,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs\Startup"),
            RegistryHive.CurrentUser, ApprovedFolderUser, "Current user");

        AddFolderItems(items,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Microsoft\Windows\Start Menu\Programs\Startup"),
            RegistryHive.LocalMachine, ApprovedFolderMachine, "All users");

        return items
            .OrderByDescending(i => i.IsEnabled)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void AddFolderItems(List<StartupItem> items, string folder, RegistryHive hive, string approvedPath, string scope)
    {
        try
        {
            if (!Directory.Exists(folder))
                return;

            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                var ext = Path.GetExtension(file);
                if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".url", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                items.Add(new StartupItem(
                    Id: $"folder:{hive}:{name}",
                    Name: Path.GetFileNameWithoutExtension(file),
                    Command: file,
                    Scope: scope,
                    Source: "Startup folder",
                    Hive: hive,
                    ApprovedPath: approvedPath,
                    ValueName: name,
                    IsEnabled: IsEnabled(hive, approvedPath, name)));
            }
        }
        catch
        {
            // Unreadable folder: skip it.
        }
    }

    private bool IsEnabled(RegistryHive hive, string approvedPath, string valueName)
    {
        var read = _reg.Read(hive, approvedPath, valueName);
        if (!read.Exists || read.Value is not byte[] { Length: > 0 } blob)
            return true;

        // Task Manager writes 02 for enabled, 03 for disabled. Only the low bit matters.
        return (blob[0] & 1) == 0;
    }

    private static byte[] EnabledBlob() => [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private static byte[] DisabledBlob()
    {
        var blob = new byte[12];
        blob[0] = 0x03;
        BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(blob, 4);
        return blob;
    }

    public TweakResult SetEnabled(StartupItem item, bool enabled)
    {
        try
        {
            _reg.WriteValue(item.Hive, item.ApprovedPath, item.ValueName,
                RegistryValueKind.Binary, enabled ? EnabledBlob() : DisabledBlob(), TweakId(item));
            Log.Info((enabled ? "Enabled" : "Disabled") + " startup entry: " + item.Name);
            return TweakResult.Ok;
        }
        catch (Exception ex)
        {
            Log.Warn("Startup toggle failed for " + item.Name + ": " + ex.Message);
            return TweakResult.Fail(ex.Message);
        }
    }

    public bool IsModified(StartupItem item) => _reg.HasBackups(TweakId(item));

    public TweakResult Reset(StartupItem item)
    {
        if (!IsModified(item))
            return TweakResult.Fail("No change to reset for this entry.");

        try
        {
            return _reg.RestoreAll(TweakId(item))
                ? TweakResult.Ok
                : TweakResult.Fail("Some values could not be restored. See the Stratus log for details.");
        }
        catch (Exception ex)
        {
            return TweakResult.Fail(ex.Message);
        }
    }

    /// <summary>Restores every entry Stratus changed to its exact previous state.</summary>
    public int ResetAll()
    {
        var restored = 0;
        foreach (var item in Scan())
        {
            if (!IsModified(item))
                continue;

            try
            {
                if (_reg.RestoreAll(TweakId(item)))
                    restored++;
            }
            catch
            {
                // Keep going: one bad key must not block the rest.
            }
        }

        return restored;
    }
}

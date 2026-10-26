using Stratus.Core.Services;

namespace Stratus.Core.Services;

public enum DebloatGroup { Recommended, OtherMicrosoft, ThirdParty }

public sealed record UwpAppItem(
    string Name,
    string FullName,
    string Publisher,
    DebloatGroup Group,
    string? FamilyName = null)
{
    public bool Selected { get; set; }
    public string? Result { get; set; }
}

public sealed record Win32AppItem(
    string DisplayName,
    string Publisher,
    string UninstallString,
    long SizeBytes)
{
    public string RegistryKey { get; set; } = "";

    /// <summary>"All users" for HKLM entries, "Current user" for HKCU entries.</summary>
    public string Scope { get; set; } = "All users";

    /// <summary>No-prompt uninstall command, null when the installer family is unknown.</summary>
    public string? SilentCommand { get; set; }
}

/// <summary>A package staged for new user profiles but not installed for the current user.</summary>
public sealed record ProvisionedAppItem(string Name, string DisplayName, string? FamilyName);

/// <summary>UWP bloat removal via PackageManager (elevated: removes for all users) + Win32 list from the registry.</summary>
public sealed class DebloatService
{
    // Id.Name values of known-removable Microsoft apps (safe to remove for a gaming machine).
    private static readonly HashSet<string> RecommendedBloat = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.BingNews",
        "Microsoft.BingWeather",
        "Microsoft.BingSearch",
        "Microsoft.549981C3F5F10",            // Cortana
        "Microsoft.GetHelp",
        "Microsoft.Getstarted",
        "Microsoft.WindowsFeedbackHub",
        "Microsoft.MicrosoftSolitaireCollection",
        "Microsoft.People",
        "Microsoft.WindowsMaps",
        "Microsoft.MicrosoftOfficeHub",
        "MicrosoftTeams",
        "MicrosoftCorporationII.MicrosoftFamily",
        "Clipchamp.Clipchamp",
        "Microsoft.MixedReality.Portal",
        "Microsoft.Microsoft3DViewer",
        "Microsoft.3DViewer",
        "Microsoft.Print3D",
        "Microsoft.OneConnect",
        "Microsoft.Messaging",
        "Microsoft.Wallet",
        "Microsoft.Windows.DevHome",
        "microsoft.windowscommunicationsapps", // Mail/Calendar (legacy)
        "Microsoft.YourPhone"                  // Phone Link: removable, only useful with an Android phone
    };

    // Frameworks/runtimes other apps depend on - never offer these.
    private static readonly string[] SkipPatterns =
    [
        "Microsoft.NET.Native",
        "Microsoft.VCLibs",
        "Microsoft.UI.Xaml",
        "Microsoft.WindowsAppRuntime",
        "Microsoft.Services.Store",
        "Microsoft.Advertising.Xaml",
        "Microsoft.WindowsStore",
        "Microsoft.WindowsCalculator",
        "Microsoft.WindowsNotepad",
        "Microsoft.Paint",
        "Microsoft.ScreenSketch",
        "Microsoft.WindowsTerminal",
        "Microsoft.DesktopAppInstaller",
        "Microsoft.Windows.Photos",
        "Microsoft.WindowsSoundRecorder",
        "Microsoft.Windows.SnippingTool",
        "Microsoft.QuickAssist",
        "Microsoft.SecHealthUI",
        "Microsoft.LockApp",
        "Microsoft.Windows.ShellExperienceHost",
        "Microsoft.Windows.StartMenuExperienceHost",
        "Microsoft.Copilot",
        "Microsoft.HEIFImageExtension",
        "Microsoft.WebMediaExtensions",
        "Microsoft.WebpImageExtension",
        "Microsoft.VP9VideoExtensions",
        "Microsoft.Av1VideoExtension",
        "Microsoft.RawImageExtension",
        "Microsoft.MPEG2VideoExtension",
        "Microsoft.WindowsCamera",
        "Microsoft.Windows.Cortana"
    ];

    public List<UwpAppItem> ScanUwp()
    {
        var items = new List<UwpAppItem>();
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            foreach (var package in manager.FindPackages())
            {
                var id = package.Id;
                if (id is null || package.IsFramework || package.IsResourcePackage)
                    continue;

                var name = id.Name ?? "";
                if (SkipPatterns.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var publisher = package.Id?.Publisher ?? "";
                var display = string.IsNullOrWhiteSpace(package.DisplayName) ? name : package.DisplayName;

                DebloatGroup group;
                if (RecommendedBloat.Contains(name))
                    group = DebloatGroup.Recommended;
                else if (publisher.Contains("CN=Microsoft", StringComparison.OrdinalIgnoreCase))
                    group = DebloatGroup.OtherMicrosoft;
                else
                    group = DebloatGroup.ThirdParty;

                items.Add(new UwpAppItem(display, id.FullName ?? name, publisher, group, id.FamilyName));
            }
        }
        catch (Exception ex)
        {
            items.Add(new UwpAppItem($"Scan failed: {ex.Message}", "", "", DebloatGroup.OtherMicrosoft));
        }

        return items.OrderBy(i => i.Group).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Removes one UWP package and deprovisions it (so new users never get it).
    /// Elevated, it is removed for every user account because the elevated scan lists
    /// packages from all profiles; otherwise it falls back to the current user.
    /// </summary>
    public async Task<(bool Success, string Message)> RemoveAsync(UwpAppItem item)
    {
        if (string.IsNullOrWhiteSpace(item.FullName))
            return (false, "This entry has no package name and cannot be removed.");

        var manager = new Windows.Management.Deployment.PackageManager();
        var options = SystemInfoService.IsElevated()
            ? Windows.Management.Deployment.RemovalOptions.RemoveForAllUsers
            : Windows.Management.Deployment.RemovalOptions.None;

        // Deployment calls can block for minutes when the servicing stack is busy, and one
        // stuck package used to freeze the whole batch. Cap every step.
        using var removeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            var result = await manager.RemovePackageAsync(item.FullName, options).AsTask(removeTimeout.Token);
            if (result.ErrorText.Length > 0)
            {
                Log.Warn("Debloat remove failed for " + item.Name + ": " + result.ErrorText);
                return (false, result.ErrorText);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Warn("Debloat remove timed out for " + item.Name + ".");
            return (false, "Timed out after 90 seconds.");
        }
        catch (Exception ex)
        {
            Log.Warn("Debloat remove failed for " + item.Name + ": " + ex.Message);
            return (false, ex.Message);
        }

        // Deprovision so future user profiles do not get the package back. This used to shell
        // out to "dism /Online /Remove-ProvisionedAppxPackage", which waits on the servicing
        // stack and took minutes per package (the page looked frozen on "Removing 1 of 9").
        // The deployment API is the supported replacement and returns once the entry is gone.
        if (!string.IsNullOrEmpty(item.FamilyName) && SystemInfoService.IsElevated())
        {
            try
            {
                using var deprovisionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await manager
                    .DeprovisionPackageForAllUsersAsync(item.FamilyName)
                    .AsTask(deprovisionTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Warn("Debloat deprovision timed out for " + item.Name + ".");
            }
            catch (Exception ex)
            {
                // The package is already gone for this user, so provisioning stays best effort.
                Log.Warn("Debloat deprovision failed for " + item.Name + ": " + ex.Message);
            }
        }

        Log.Info("Debloat removed " + item.Name + ".");
        return (true, "Removed");
    }

    public List<Win32AppItem> ScanWin32()
    {
        var items = new List<Win32AppItem>();
        using var machine = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        using var machineWow = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        using var user = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        var roots = new[] { (Key: machine, Scope: "All users"), (Key: machineWow, Scope: "All users"), (Key: user, Scope: "Current user") };

        foreach (var (root, scope) in roots)
        {
            if (root is null) continue;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(sub);
                if (key is null) continue;
                if (key.GetValue("SystemComponent") is int system && system == 1) continue;
                if (key.GetValue("WindowsInstaller") is int msi && msi == 1 && key.GetValue("UninstallString") is null) continue;

                var display = key.GetValue("DisplayName") as string;
                var uninstall = key.GetValue("UninstallString") as string;
                if (string.IsNullOrWhiteSpace(display) || string.IsNullOrWhiteSpace(uninstall))
                    continue;

                var size = key.GetValue("EstimatedSize") switch
                {
                    int kb => (long)kb * 1024,
                    long kb => kb * 1024,
                    _ => 0L,
                };

                // QuietUninstallString already carries the vendor's silent flags; only derive
                // arguments ourselves when the entry does not provide one.
                var quiet = key.GetValue("QuietUninstallString") as string;

                items.Add(new Win32AppItem(
                    display,
                    key.GetValue("Publisher") as string ?? "",
                    uninstall,
                    size)
                {
                    RegistryKey = sub,
                    Scope = scope,
                    SilentCommand = string.IsNullOrWhiteSpace(quiet) ? BuildSilentCommand(uninstall) : quiet,
                });
            }
        }

        return items
            .GroupBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(i => i.SizeBytes)
            .ToList();
    }

    /// <summary>Launches the app's own uninstaller (interactive). Returns false if it couldn't start.</summary>
    public bool LaunchUninstaller(Win32AppItem item)
    {
        try
        {
            var (exe, args) = CommandLine.Split(item.UninstallString);

            // MsiExec /X{guid} needs a silent flag we cannot assume - keep it interactive.
            var psi = new System.Diagnostics.ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs the no-prompt uninstall command and waits for it to finish, so the page can
    /// report the real outcome instead of guessing. Capped at five minutes.
    /// </summary>
    public async Task<(bool Success, string Message)> UninstallSilentlyAsync(Win32AppItem item)
    {
        if (string.IsNullOrWhiteSpace(item.SilentCommand))
            return (false, "This program has no known silent uninstall command.");

        try
        {
            var (exe, args) = CommandLine.Split(item.SilentCommand);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
                return (false, "The uninstaller did not start.");

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await process.WaitForExitAsync(timeout.Token);

            if (process.ExitCode == 0)
            {
                Log.Info("Silent uninstall finished: " + item.DisplayName);
                return (true, "Uninstalled");
            }

            Log.Warn($"Silent uninstall of {item.DisplayName} exited with code {process.ExitCode}.");
            return (false, $"The uninstaller exited with code {process.ExitCode}.");
        }
        catch (OperationCanceledException)
        {
            Log.Warn("Silent uninstall timed out: " + item.DisplayName);
            return (false, "The uninstaller is still running after five minutes.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Silent uninstall of {item.DisplayName} failed: {ex.Message}");
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Adds no-prompt flags for the installer families we can recognise. Unknown installers
    /// stay interactive, because guessing flags can leave a half-removed program.
    /// </summary>
    private static string? BuildSilentCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var text = command.Trim();
        var lower = text.ToLowerInvariant();

        if (lower.Contains("msiexec") && lower.Contains("/x"))
            return text + " /qn /norestart";

        if (lower.Contains("unins000.exe"))
            return text + " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART";

        if (lower.EndsWith("uninstall.exe") || lower.Contains("uninstall.exe\""))
            return text + " /S";

        return null;
    }

    /// <summary>Packages staged for new user profiles. Empty when nothing is provisioned.</summary>
    public List<ProvisionedAppItem> ScanProvisioned()
    {
        var items = new List<ProvisionedAppItem>();
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            foreach (var package in manager.FindProvisionedPackages())
            {
                var id = package.Id;
                if (id is null || string.IsNullOrWhiteSpace(id.FamilyName))
                    continue;

                var name = id.Name ?? "";
                var display = string.IsNullOrWhiteSpace(package.DisplayName) ? name : package.DisplayName;
                items.Add(new ProvisionedAppItem(name, display, id.FamilyName));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Provisioned package scan failed: " + ex.Message);
        }

        return items.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Removes a provisioned package so new user profiles no longer receive it.</summary>
    public async Task<(bool Success, string Message)> DeprovisionAsync(ProvisionedAppItem item)
    {
        if (string.IsNullOrWhiteSpace(item.FamilyName))
            return (false, "This package has no family name and cannot be deprovisioned.");

        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await manager.DeprovisionPackageForAllUsersAsync(item.FamilyName).AsTask(timeout.Token);
            Log.Info("Deprovisioned package: " + item.Name);
            return (true, "Removed");
        }
        catch (OperationCanceledException)
        {
            Log.Warn("Deprovision timed out: " + item.Name);
            return (false, "Timed out after 60 seconds.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Deprovision of {item.Name} failed: {ex.Message}");
            return (false, ex.Message);
        }
    }
}

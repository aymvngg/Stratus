using Stratus.Core.Services;

namespace Stratus.Core.Services;

public enum DebloatGroup { Recommended, OtherMicrosoft, ThirdParty }

public sealed record UwpAppItem(
    string Name,
    string FullName,
    string Publisher,
    DebloatGroup Group)
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
}

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

                items.Add(new UwpAppItem(display, id.FullName ?? name, publisher, group));
            }
        }
        catch (Exception ex)
        {
            items.Add(new UwpAppItem($"Scan failed: {ex.Message}", "", "", DebloatGroup.OtherMicrosoft));
        }

        return items.OrderBy(i => i.Group).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Removes one UWP package for the current user and deprovisions it (so new users never get it).</summary>
    public async Task<(bool Success, string Message)> RemoveAsync(UwpAppItem item)
    {
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            var result = await manager.RemovePackageAsync(item.FullName).AsTask();
            if (result.ErrorText.Length > 0)
            {
                Log.Warn("Debloat remove failed for " + item.Name + ": " + result.ErrorText);
                return (false, result.ErrorText);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Debloat remove failed for " + item.Name + ": " + ex.Message);
            return (false, ex.Message);
        }

        // Best effort: drop the provisioning so future user profiles don't reinstall it.
        // DISM takes seconds to minutes per package and ProcessRunner blocks, so keep it off
        // the UI thread (this method is awaited from the UI and resumes there).
        if (SystemInfoService.IsElevated())
        {
            await Task.Run(() => ProcessRunner.Run(
                "dism.exe",
                $"/Online /Remove-ProvisionedAppxPackage /PackageName:{item.FullName}",
                120_000));
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
        var roots = new[] { machine, machineWow, user };

        foreach (var root in roots)
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
                items.Add(new Win32AppItem(
                    display,
                    key.GetValue("Publisher") as string ?? "",
                    uninstall,
                    size)
                {
                    RegistryKey = sub
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
            string exe;
            string args;
            var s = item.UninstallString.Trim();
            if (s.StartsWith('"'))
            {
                var end = s.IndexOf('"', 1);
                if (end < 0)
                {
                    exe = s.Trim('"');
                    args = "";
                }
                else
                {
                    exe = s[1..end];
                    args = s[(end + 1)..].Trim();
                }
            }
            else if (System.IO.File.Exists(s))
            {
                exe = s;
                args = "";
            }
            else
            {
                // Unquoted paths with spaces are common ("C:\Program Files\App\unins000.exe /S").
                // Use the longest prefix that exists on disk as the executable, the rest as args.
                exe = "";
                args = "";
                for (var space = s.IndexOf(' '); space > 0; space = s.IndexOf(' ', space + 1))
                {
                    var candidate = s[..space];
                    if (System.IO.File.Exists(candidate))
                    {
                        exe = candidate;
                        args = s[(space + 1)..].Trim();
                        break;
                    }
                }

                if (exe.Length == 0)
                {
                    var space = s.IndexOf(' ');
                    exe = space < 0 ? s : s[..space];
                    args = space < 0 ? "" : s[(space + 1)..].Trim();
                }
            }

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
}

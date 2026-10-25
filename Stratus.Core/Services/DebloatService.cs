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
        "Microsoft.YourPhone"                  // listed under Other for cautious users
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
        "Microsoft.WindowsNotepad",
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
                return (false, result.ErrorText);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        // Best effort: drop the provisioning so future user profiles don't reinstall it.
        if (SystemInfoService.IsElevated())
        {
            ProcessRunner.Run("dism.exe", $"/Online /Remove-ProvisionedAppxPackage /PackageName:{item.FullName}", 120_000);
        }

        return (true, "Removed");
    }

    public List<Win32AppItem> ScanWin32()
    {
        var items = new List<Win32AppItem>();
        var roots = new[]
        {
            Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
        };

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

                var size = key.GetValue("EstimatedSize") is int kb ? (long)kb * 1024 : 0;
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
            string exe, args;
            var s = item.UninstallString.Trim();
            if (s.StartsWith('"'))
            {
                var end = s.IndexOf('"', 1);
                exe = s[1..end];
                args = s[(end + 1)..].Trim();
            }
            else
            {
                var space = s.IndexOf(' ');
                exe = space < 0 ? s : s[..space];
                args = space < 0 ? "" : s[(space + 1)..].Trim();
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

namespace Stratus.Core.Services;

/// <summary>One removable Windows optional feature or capability.</summary>
public sealed record OptionalFeatureItem(
    string Name,
    string DisplayName,
    string Kind,
    bool IsInstalled);

/// <summary>
/// Lists and removes a curated set of Windows optional features and capabilities.
/// The DISM cmdlets need elevation; the app runs elevated, so the calls succeed there.
/// Everything here is opt-in: nothing is touched until the user presses Remove.
/// </summary>
public sealed class OptionalFeatureService
{
    private const string FeatureScript = """
        $ErrorActionPreference = 'Stop'
        $names = @('WorkFolders-Client','FaxServicesClientPackage','Printing-XPSServices-Features','Internet-Explorer-Optional-amd64','WindowsMediaPlayer','MediaPlayback','SearchEngine-Client-Package','Microsoft-Hyper-V-All','Microsoft-Windows-Subsystem-Linux','VirtualMachinePlatform','Containers-DisposableClientVM','NetFx3','ServicesForNFS-ClientOnly')
        try {
            Get-WindowsOptionalFeature -Online | Where-Object { $names -contains $_.FeatureName } | ForEach-Object { "$($_.FeatureName)|$([string]$_.State)" }
        } catch {
            $_.Exception.Message
            exit 1
        }
        """;

    private const string CapabilityScript = """
        $ErrorActionPreference = 'Stop'
        $patterns = @('MathRecognizer*','Language.OCR*','Language.Speech*','Language.Handwriting*','Print.Fax.Scan*','XPS.Viewer*','Browser.InternetExplorer*','App.Support.QuickAssist*','App.WirelessDisplay.Connect*')
        try {
            Get-WindowsCapability -Online | Where-Object { $name = $_.Name; $patterns | Where-Object { $name -like $_ } } | ForEach-Object { "$($_.Name)|$([string]$_.State)" }
        } catch {
            $_.Exception.Message
            exit 1
        }
        """;

    /// <summary>
    /// Features Stratus may turn on. The scan lists these even when they are missing,
    /// so the user can enable them straight from the list.
    /// </summary>
    private static readonly string[] EnableableFeatures =
    [
        "Microsoft-Windows-Subsystem-Linux",
        "VirtualMachinePlatform",
        "Microsoft-Hyper-V-All",
        "Containers-DisposableClientVM",
        "NetFx3",
        "ServicesForNFS-ClientOnly",
    ];

    private static readonly Dictionary<string, string> FriendlyFeatures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WorkFolders-Client"] = "Work Folders client",
        ["FaxServicesClientPackage"] = "Fax and Scan",
        ["Printing-XPSServices-Features"] = "Microsoft XPS Document Writer",
        ["Internet-Explorer-Optional-amd64"] = "Internet Explorer 11",
        ["WindowsMediaPlayer"] = "Windows Media Player",
        ["MediaPlayback"] = "Media playback",
        ["SearchEngine-Client-Package"] = "Windows Search engine client",
        // One entry serves both directions: this feature can be removed or enabled.
        ["Microsoft-Hyper-V-All"] = "Hyper-V (removing it breaks WSL2 and Windows Sandbox; enabling it requires a restart)",
        ["Microsoft-Windows-Subsystem-Linux"] = "Windows Subsystem for Linux (WSL)",
        ["VirtualMachinePlatform"] = "Virtual Machine Platform (required by WSL2)",
        ["Containers-DisposableClientVM"] = "Windows Sandbox",
        ["NetFx3"] = "NET Framework 3.5 (.NET 3.5)",
        ["ServicesForNFS-ClientOnly"] = "NFS client",
    };

    private static readonly (string Prefix, string Label)[] FriendlyCapabilities =
    [
        ("MathRecognizer", "Math Recognizer"),
        ("Language.OCR", "OCR language pack"),
        ("Language.Speech", "Speech recognition language pack"),
        ("Language.Handwriting", "Handwriting language pack"),
        ("Print.Fax.Scan", "Print and Fax scanner"),
        ("XPS.Viewer", "XPS Viewer"),
        ("Browser.InternetExplorer", "Internet Explorer 11 capability"),
        ("App.Support.QuickAssist", "Quick Assist"),
        ("App.WirelessDisplay.Connect", "Wireless Display (Miracast)"),
    ];

    /// <summary>
    /// Returns the curated features (installed and missing, deduplicated by name)
    /// plus the installed capabilities. Never throws.
    /// </summary>
    public List<OptionalFeatureItem> Scan()
    {
        var items = new List<OptionalFeatureItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, state) in RunList(FeatureScript))
        {
            if (!seen.Add(name))
                continue;

            var installed = state.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
            if (!installed && !EnableableFeatures.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;

            items.Add(new OptionalFeatureItem(name, FriendlyName(name), "Feature", installed));
        }

        foreach (var (name, state) in RunList(CapabilityScript))
        {
            if (!state.Equals("Installed", StringComparison.OrdinalIgnoreCase))
                continue;

            items.Add(new OptionalFeatureItem(name, FriendlyName(name), "Capability", true));
        }

        return items
            .OrderBy(i => i.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Disables a feature or removes a capability. A restart may be needed for features.</summary>
    public (bool Success, string Message) Remove(OptionalFeatureItem item)
    {
        // The name goes into a single-quoted PowerShell string: double any quote it carries.
        var safeName = item.Name.Replace("'", "''", StringComparison.Ordinal);
        var script = item.Kind.Equals("Capability", StringComparison.OrdinalIgnoreCase)
            ? $"$ErrorActionPreference = 'Stop'; try {{ Remove-WindowsCapability -Online -Name '{safeName}' | Out-Null; 'ok' }} catch {{ $_.Exception.Message; exit 1 }}"
            : $"$ErrorActionPreference = 'Stop'; try {{ Disable-WindowsOptionalFeature -Online -FeatureName '{safeName}' -NoRestart | Out-Null; 'ok' }} catch {{ $_.Exception.Message; exit 1 }}";

        try
        {
            var result = ProcessRunner.PowerShell(script, 300_000);
            if (!result.Success)
            {
                var error = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
                var message = Shorten(error.Trim());
                if (message.Length == 0)
                    message = "PowerShell returned no error text (exit code " + result.ExitCode + ").";
                Log.Warn($"Removing {item.Name} failed: {message}");
                return (false, message);
            }

            Log.Info("Removed optional " + item.Kind.ToLowerInvariant() + ": " + item.Name);
            return (true, "Removed");
        }
        catch (Exception ex)
        {
            Log.Warn($"Removing {item.Name} failed: {ex.Message}");
            return (false, ex.Message);
        }
    }

    /// <summary>Enables a Windows feature. A restart is often required to finish.</summary>
    public (bool Success, string Message) Enable(OptionalFeatureItem item)
    {
        if (!item.Kind.Equals("Feature", StringComparison.OrdinalIgnoreCase))
            return (false, "Only Windows features can be enabled from this list.");

        // The name goes into a single-quoted PowerShell string: double any quote it carries.
        var safeName = item.Name.Replace("'", "''", StringComparison.Ordinal);
        var script = $"$ErrorActionPreference = 'Stop'; try {{ Enable-WindowsOptionalFeature -Online -FeatureName '{safeName}' -All -NoRestart | Out-Null; 'ok' }} catch {{ $_.Exception.Message; exit 1 }}";

        try
        {
            var result = ProcessRunner.PowerShell(script, 300_000);
            if (!result.Success)
            {
                var error = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
                var message = Shorten(error.Trim());
                if (message.Length == 0)
                    message = "PowerShell returned no error text (exit code " + result.ExitCode + ").";
                Log.Warn($"Enabling {item.Name} failed: {message}");
                return (false, message);
            }

            Log.Info("Enabled optional " + item.Kind.ToLowerInvariant() + ": " + item.Name);
            return (true, "Enabled");
        }
        catch (Exception ex)
        {
            Log.Warn($"Enabling {item.Name} failed: {ex.Message}");
            return (false, ex.Message);
        }
    }

    private static List<(string Name, string State)> RunList(string script)
    {
        var items = new List<(string Name, string State)>();

        try
        {
            var result = ProcessRunner.PowerShell(script, 180_000);
            if (!result.Success)
            {
                var error = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
                Log.Warn("Optional feature list failed: " + Shorten(error.Trim()));
                return items;
            }

            if (string.IsNullOrWhiteSpace(result.StdOut))
                return items;

            foreach (var raw in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                var separator = line.IndexOf('|');
                if (separator <= 0)
                    continue;

                items.Add((line[..separator].Trim(), line[(separator + 1)..].Trim()));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Optional feature scan failed: " + ex.Message);
        }

        return items;
    }

    private static string FriendlyName(string name)
    {
        if (FriendlyFeatures.TryGetValue(name, out var feature))
            return feature;

        foreach (var (prefix, label) in FriendlyCapabilities)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return label;
        }

        return name;
    }

    private static string Shorten(string text) =>
        text.Length <= 180 ? text : text[..177] + "...";
}

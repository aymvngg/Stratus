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
        $names = @('WorkFolders-Client','FaxServicesClientPackage','Printing-XPSServices-Features','Internet-Explorer-Optional-amd64','WindowsMediaPlayer','MediaPlayback','SearchEngine-Client-Package','Microsoft-Hyper-V-All')
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

    private static readonly Dictionary<string, string> FriendlyFeatures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WorkFolders-Client"] = "Work Folders client",
        ["FaxServicesClientPackage"] = "Fax and Scan",
        ["Printing-XPSServices-Features"] = "Microsoft XPS Document Writer",
        ["Internet-Explorer-Optional-amd64"] = "Internet Explorer 11",
        ["WindowsMediaPlayer"] = "Windows Media Player",
        ["MediaPlayback"] = "Media playback",
        ["SearchEngine-Client-Package"] = "Windows Search engine client",
        // Hyper-V is listed because it is large, but removing it takes WSL2 and Windows Sandbox with it.
        ["Microsoft-Hyper-V-All"] = "Hyper-V (removing it breaks WSL2 and Windows Sandbox)",
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

    /// <summary>Returns the installed features and capabilities from the curated list. Never throws.</summary>
    public List<OptionalFeatureItem> Scan()
    {
        var items = new List<OptionalFeatureItem>();

        foreach (var (name, state) in RunList(FeatureScript))
        {
            if (!state.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
                continue;

            items.Add(new OptionalFeatureItem(name, FriendlyName(name), "Feature", true));
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

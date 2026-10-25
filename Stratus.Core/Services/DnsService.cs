using Stratus.Core.Services;

namespace Stratus.Core.Services;

public sealed record DnsProfile(string Id, string Label, string? Primary, string? Secondary);

public sealed class DnsService
{
    public static readonly List<DnsProfile> Profiles = new()
    {
        new("auto", "Automatic (from router)", null, null),
        new("cloudflare", "Cloudflare (1.1.1.1)", "1.1.1.1", "1.0.0.1"),
        new("google", "Google (8.8.8.8)", "8.8.8.8", "8.8.4.4"),
        new("quad9", "Quad9 (9.9.9.9)", "9.9.9.9", "149.112.112.112")
    };

    public List<string> GetConnectedAdapters()
    {
        // Win32_NetworkAdapter with NetEnabled+PhysicalAdapter mirrors Get-NetAdapter's
        // "Up and HardwareInterface" filter without spawning a PowerShell process.
        var names = new List<string>();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT NetConnectionID FROM Win32_NetworkAdapter WHERE NetEnabled = TRUE AND PhysicalAdapter = TRUE");
            foreach (var adapter in searcher.Get())
            {
                if (adapter["NetConnectionID"] is string name && name.Length > 0)
                    names.Add(name);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Adapter enumeration failed: " + ex.Message);
        }
        return names;
    }

    public (bool Success, string Message) Apply(DnsProfile profile)
    {
        var adapters = GetConnectedAdapters();
        if (adapters.Count == 0)
            return (false, "No connected physical adapters found.");

        foreach (var adapter in adapters)
        {
            var name = adapter.Replace("\"", "\\\"");
            ProcessResult result;
            if (profile.Primary is null)
            {
                result = ProcessRunner.Run("netsh.exe", $"interface ip set dnsservers name=\"{name}\" source=dhcp");
            }
            else
            {
                result = ProcessRunner.Run("netsh.exe", $"interface ip set dns name=\"{name}\" static {profile.Primary}");
                if (profile.Secondary is not null)
                    ProcessRunner.Run("netsh.exe", $"interface ip add dns name=\"{name}\" {profile.Secondary} index=2");
            }

            if (!result.Success)
            {
                Log.Warn("DNS apply failed on '" + adapter + "': " + result.StdErr.Trim());
                return (false, $"netsh failed for '{adapter}': {result.StdErr.Trim()}");
            }
        }

        ProcessRunner.Run("ipconfig.exe", "/flushdns");
        Log.Info("DNS applied: " + profile.Id + " on " + adapters.Count + " adapter(s).");
        return (true, $"DNS set to {profile.Label} on {adapters.Count} adapter(s).");
    }
}

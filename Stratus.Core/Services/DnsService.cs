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

    /// <summary>
    /// Reads the statically configured DNS servers of the connected physical adapters and maps
    /// them to a known profile. Returns null when the adapters take DNS from DHCP (the normal
    /// "Automatic" case) and a synthesized "custom" profile when the servers are not a preset,
    /// so the picker shows the truth instead of staying blank after a restart.
    ///
    /// Registry, not WMI: DNSServerSearchOrder also returns servers handed out by DHCP, so it
    /// cannot tell "Automatic" from "Custom". The per-interface NameServer value is only written
    /// for static servers; DhcpNameServer holds the DHCP-provided ones.
    /// </summary>
    public DnsProfile? GetActiveProfile()
    {
        try
        {
            using var adapters = new System.Management.ManagementObjectSearcher(
                "SELECT GUID FROM Win32_NetworkAdapter WHERE NetEnabled = TRUE AND PhysicalAdapter = TRUE");

            foreach (var adapter in adapters.Get())
            {
                if (adapter["GUID"] is not string guid || guid.Length == 0)
                    continue;

                var servers = ReadStaticServers(guid);
                if (servers.Count == 0)
                    continue; // DNS comes from DHCP on this adapter: that is "Automatic".

                var known = Profiles.FirstOrDefault(p =>
                    p.Primary is not null &&
                    string.Equals(p.Primary, servers[0], StringComparison.OrdinalIgnoreCase));

                return known ?? new DnsProfile(
                    "custom",
                    $"Custom ({servers[0]})",
                    servers[0],
                    servers.Count > 1 ? servers[1] : null);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the active DNS servers: " + ex.Message);
        }

        return null;
    }

    private static List<string> ReadStaticServers(string adapterGuid)
    {
        var servers = new List<string>();
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + adapterGuid);
            if (key?.GetValue("NameServer") is string text)
            {
                foreach (var part in text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
                    servers.Add(part.Trim());
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the DNS servers for adapter {adapterGuid}: " + ex.Message);
        }

        return servers;
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
                {
                    var secondary = ProcessRunner.Run(
                        "netsh.exe", $"interface ip add dns name=\"{name}\" {profile.Secondary} index=2");
                    // A failed secondary used to be ignored, so the UI reported success while the
                    // adapter only had one resolver.
                    if (result.Success && !secondary.Success)
                        result = secondary;
                }
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

using Stratus.Core.Services;

namespace Stratus.Core.Services;

/// <summary>
/// One DNS preset. The first four members stay positional because the UI binds them;
/// the IPv6 pair is optional and applied best-effort only.
/// </summary>
public sealed record DnsProfile(
    string Id,
    string Label,
    string? Primary,
    string? Secondary,
    string? Primary6 = null,
    string? Secondary6 = null,
    bool BenchmarkEligible = true);

public sealed class DnsService
{
    public static readonly List<DnsProfile> Profiles = new()
    {
        new("auto", "Automatic (from router)", null, null),
        new("cloudflare", "Cloudflare (1.1.1.1)", "1.1.1.1", "1.0.0.1",
            "2606:4700:4700::1111", "2606:4700:4700::1001"),
        new("cloudflare-malware", "Cloudflare Malware blocking (1.1.1.2)", "1.1.1.2", "1.0.0.2",
            "2606:4700:4700::1112", "2606:4700:4700::1002"),
        new("cloudflare-family", "Cloudflare Family (1.1.1.3)", "1.1.1.3", "1.0.0.3",
            "2606:4700:4700::1113", "2606:4700:4700::1003"),
        new("google", "Google (8.8.8.8)", "8.8.8.8", "8.8.4.4",
            "2001:4860:4860::8888", "2001:4860:4860::8844"),
        new("quad9", "Quad9 (9.9.9.9)", "9.9.9.9", "149.112.112.112",
            "2620:fe::fe", "2620:fe::9"),
        new("quad9-unfiltered", "Quad9 unfiltered (9.9.9.10)", "9.9.9.10", "149.112.112.10",
            "2620:fe::10", "2620:fe::fe:10"),
        new("opendns", "OpenDNS (208.67.222.222)", "208.67.222.222", "208.67.220.220",
            "2620:119:35::35", "2620:119:53::53"),
        new("opendns-family", "OpenDNS FamilyShield (208.67.222.123)", "208.67.222.123", "208.67.220.123",
            "2620:119:35::123", "2620:119:53::123"),
        new("adguard", "AdGuard DNS (94.140.14.14)", "94.140.14.14", "94.140.15.15",
            "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff"),
        new("adguard-family", "AdGuard Family (94.140.14.15)", "94.140.14.15", "94.140.15.16",
            "2a10:50c0::bad1:ff", "2a10:50c0::bad2:ff")
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
                servers.AddRange(ReadStaticServers6(guid));
                if (servers.Count == 0)
                    continue; // DNS comes from DHCP on this adapter: that is "Automatic".

                var known = Profiles.FirstOrDefault(p =>
                    (p.Primary is not null && string.Equals(p.Primary, servers[0], StringComparison.OrdinalIgnoreCase)) ||
                    (p.Primary6 is not null && string.Equals(p.Primary6, servers[0], StringComparison.OrdinalIgnoreCase)));

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

    /// <summary>Static IPv6 servers live in the Tcpip6 branch; DHCP-provided ones never land there.</summary>
    private static List<string> ReadStaticServers6(string adapterGuid)
    {
        var servers = new List<string>();
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\" + adapterGuid);
            if (key?.GetValue("NameServer") is string text)
            {
                foreach (var part in text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
                    servers.Add(part.Trim());
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the IPv6 DNS servers for adapter {adapterGuid}: " + ex.Message);
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

            // IPv6 is best effort: many adapters have it unbound or disabled, and that must never
            // undo a working IPv4 configuration.
            if (profile.Primary is null)
            {
                var reset6 = ProcessRunner.Run("netsh.exe", $"interface ipv6 set dnsservers name=\"{name}\" source=dhcp");
                if (!reset6.Success)
                    Log.Warn("IPv6 DNS reset failed on '" + adapter + "': " + reset6.StdErr.Trim());
            }
            else if (profile.Primary6 is not null)
            {
                var primary6 = ProcessRunner.Run(
                    "netsh.exe", $"interface ipv6 set dnsservers name=\"{name}\" static {profile.Primary6}");
                if (!primary6.Success)
                {
                    Log.Warn("IPv6 DNS apply failed on '" + adapter + "': " + primary6.StdErr.Trim());
                }
                else if (profile.Secondary6 is not null)
                {
                    // The add form takes the plural "dnsservers"; "set dns" would overwrite the primary.
                    var secondary6 = ProcessRunner.Run(
                        "netsh.exe", $"interface ipv6 add dnsservers name=\"{name}\" {profile.Secondary6} index=2");
                    if (!secondary6.Success)
                        Log.Warn("IPv6 secondary DNS failed on '" + adapter + "': " + secondary6.StdErr.Trim());
                }
            }
        }

        ProcessRunner.Run("ipconfig.exe", "/flushdns");
        Log.Info("DNS applied: " + profile.Id + " on " + adapters.Count + " adapter(s).");
        return (true, $"DNS set to {profile.Label} on {adapters.Count} adapter(s).");
    }
}

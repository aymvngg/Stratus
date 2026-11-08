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

/// <summary>An adapter and the static DNS servers it has before Stratus changes anything.</summary>
internal sealed class DnsAdapterOriginal
{
    public string AdapterName { get; set; } = "";
    public List<string> Ipv4 { get; set; } = [];
    public List<string> Ipv6 { get; set; } = [];

    public DnsAdapterOriginal() { }

    public DnsAdapterOriginal(string adapterName, List<string> ipv4, List<string> ipv6)
    {
        AdapterName = adapterName;
        Ipv4 = ipv4;
        Ipv6 = ipv6;
    }
}

/// <summary>Seam over the netsh calls and adapter reads, so the apply and rollback rules are testable.</summary>
internal interface IDnsCommandRunner
{
    /// <summary>Runs a child. Null means the process could not be started at all.</summary>
    ProcessResult? Run(string fileName, string arguments);
}

internal sealed class DnsAdapterReadException : Exception
{
    public DnsAdapterReadException(string message, Exception? inner = null) : base(message, inner) { }
}

internal interface IDnsAdapterSource
{
    /// <summary>
    /// Connected adapters with their current servers. Throws <see cref="DnsAdapterReadException"/>
    /// when the machine state cannot be read: an unknown adapter must never look like "no DNS".
    /// </summary>
    List<DnsAdapterOriginal> GetConnectedAdapters();
}

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

    private readonly IDnsCommandRunner _commands;
    private readonly IDnsAdapterSource _adapters;
    private readonly IDnsOriginalsStore _originals;

    public DnsService() : this(new WindowsDnsCommandRunner(), new WindowsDnsAdapterSource(),
        new DnsOriginalsStore(DnsOriginalsStore.DefaultPath))
    { }

    internal DnsService(
        IDnsCommandRunner commands,
        IDnsAdapterSource adapters,
        IDnsOriginalsStore originals)
    {
        _commands = commands;
        _adapters = adapters;
        _originals = originals;
    }

    public List<string> GetConnectedAdapters() => ReadAdapters().Select(a => a.AdapterName).ToList();

    /// <summary>
    /// Reads the connected adapters. A failure is reported, not turned into an empty list:
    /// showing no adapters is indistinguishable from a machine with none, and applying to
    /// "all adapters" after a failed read would silently skip the ones it could not see.
    /// </summary>
    private List<DnsAdapterOriginal> ReadAdapters()
    {
        try
        {
            return _adapters.GetConnectedAdapters();
        }
        catch (Exception ex)
        {
            Log.Error("Adapter enumeration failed", ex);
            throw new DnsAdapterReadException("The network adapters could not be read: " + ex.Message, ex);
        }
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
    /// <exception cref="DnsAdapterReadException">The adapter state could not be read.</exception>
    public DnsProfile? GetActiveProfile()
    {
        foreach (var adapter in ReadAdapters())
        {
            var servers = new List<string>();
            servers.AddRange(adapter.Ipv4);
            servers.AddRange(adapter.Ipv6);
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

        return null;
    }

    public (bool Success, string Message) Apply(DnsProfile profile)
    {
        List<DnsAdapterOriginal> adapters;
        try
        {
            adapters = ReadAdapters();
        }
        catch (DnsAdapterReadException ex)
        {
            return (false, ex.Message);
        }

        if (adapters.Count == 0)
            return (false, "No connected physical adapters found.");

        using var transaction = _originals.TryBeginTransaction();
        if (transaction is null)
            return (false, "Another Stratus process is changing DNS right now. Try again in a moment.");

        Dictionary<string, DnsAdapterOriginal> saved;
        try
        {
            saved = _originals.Load();
        }
        catch (Exception ex)
        {
            // An unreadable originals file may hold the only way back. Refuse before touching
            // any adapter instead of overwriting it with a fresh set.
            Log.Error("Could not read the saved DNS servers", ex);
            return (false, ex.Message + " No adapter was changed.");
        }

        var applied = new List<DnsAdapterOriginal>();
        var newlySaved = new List<string>();

        foreach (var adapter in adapters)
        {
            var name = Escape(adapter.AdapterName);

            // A repeat apply must not replace the durable original with the servers Stratus
            // set last time. The first save is what an explicit revert returns to.
            if (!saved.TryGetValue(adapter.AdapterName, out var original))
            {
                original = adapter;
                saved[adapter.AdapterName] = original;
                newlySaved.Add(adapter.AdapterName);
                try
                {
                    // Saved before the first change: a crash during netsh must still leave a
                    // recoverable original on disk.
                    _originals.Save(saved);
                }
                catch (Exception ex)
                {
                    // Without a durable original a later rollback is impossible, so this
                    // adapter is not changed. Earlier adapters in this attempt are rolled back.
                    Log.Error($"Could not save the original DNS servers for '{adapter.AdapterName}'", ex);
                    var undone = Rollback(applied, saved, newlySaved);
                    return (false, undone
                        ? "The original DNS servers could not be saved, so no adapter was changed."
                        : "The original DNS servers could not be saved and a changed adapter could not be restored.");
                }
            }

            // Registered before the command runs: a child that hangs or returns nothing still
            // has this attempt's starting state ready for the rollback.
            applied.Add(adapter);
            var result = ApplyIpv4(name, profile);
            if (result is null || !result.Success)
            {
                var reason = result is null
                    ? "the netsh process could not be started"
                    : result.StdErr.Trim();
                Log.Warn("DNS apply failed on '" + adapter.AdapterName + "': " + reason);
                var undone = Rollback(applied, saved, newlySaved);
                return (false, undone
                    ? $"netsh failed for '{adapter.AdapterName}': {reason} The previous DNS servers were restored."
                    : $"netsh failed for '{adapter.AdapterName}': {reason} Some adapters could not be restored.");
            }

            // IPv6 is best effort: many adapters have it unbound or disabled, and that must never
            // undo a working IPv4 configuration.
            ApplyIpv6(name, profile);
        }

        SafeRun("ipconfig.exe", "/flushdns");
        Log.Info("DNS applied: " + profile.Id + " on " + adapters.Count + " adapter(s).");
        return (true, $"DNS set to {profile.Label} on {adapters.Count} adapter(s).");
    }

    /// <summary>
    /// Runs a child and never lets a start failure escape: a throw here used to skip the
    /// rollback and leave half the adapters changed.
    /// </summary>
    private ProcessResult? SafeRun(string fileName, string arguments)
    {
        try
        {
            return _commands.Run(fileName, arguments);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not run {fileName} {arguments}", ex);
            return null;
        }
    }

    /// <summary>
    /// Restores every adapter Stratus saved and clears the saved entries only for the adapters
    /// that were restored. A failed restore keeps its original so a later revert can retry.
    /// </summary>
    public (bool Success, string Message) Revert()
    {
        using var transaction = _originals.TryBeginTransaction();
        if (transaction is null)
            return (false, "Another Stratus process is changing DNS right now. Try again in a moment.");

        Dictionary<string, DnsAdapterOriginal> saved;
        try
        {
            saved = _originals.Load();
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the saved DNS servers", ex);
            return (false, ex.Message);
        }

        if (saved.Count == 0)
            return (false, "No saved DNS servers to restore.");

        var failed = new List<string>();
        foreach (var (adapter, original) in saved)
        {
            if (!RestoreOriginal(original))
                failed.Add(adapter);
        }

        try
        {
            // Keep only the adapters that were not restored, so a retry still has their original.
            var remaining = new Dictionary<string, DnsAdapterOriginal>(StringComparer.OrdinalIgnoreCase);
            foreach (var adapter in failed)
                remaining[adapter] = saved[adapter];
            _originals.Save(remaining);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not update the saved DNS servers after a restore: " + ex.Message);
        }

        if (failed.Count > 0)
        {
            Log.Warn($"DNS revert left {failed.Count} adapter(s) unchanged: {string.Join(", ", failed)}");
            return (false, $"Could not restore DNS on {string.Join(", ", failed)}. The saved servers were kept.");
        }

        SafeRun("ipconfig.exe", "/flushdns");
        return (true, "DNS servers restored to their previous values.");
    }

    private ProcessResult? ApplyIpv4(string name, DnsProfile profile)
    {
        if (profile.Primary is null)
            return SafeRun("netsh.exe", $"interface ip set dnsservers name=\"{name}\" source=dhcp");

        var result = SafeRun("netsh.exe", $"interface ip set dns name=\"{name}\" static {profile.Primary}");
        if (result is { Success: true } && profile.Secondary is not null)
        {
            var secondary = SafeRun(
                "netsh.exe", $"interface ip add dns name=\"{name}\" {profile.Secondary} index=2");
            if (secondary is null || !secondary.Success)
                return secondary;
        }

        return result;
    }

    private void ApplyIpv6(string name, DnsProfile profile)
    {
        if (profile.Primary is null)
        {
            var reset6 = SafeRun("netsh.exe", $"interface ipv6 set dnsservers name=\"{name}\" source=dhcp");
            if (reset6 is null || !reset6.Success)
                Log.Warn("IPv6 DNS reset failed on '" + name + "': " + Detail(reset6));
            return;
        }

        if (profile.Primary6 is null)
            return;

        var primary6 = SafeRun(
            "netsh.exe", $"interface ipv6 set dnsservers name=\"{name}\" static {profile.Primary6}");
        if (primary6 is null || !primary6.Success)
        {
            Log.Warn("IPv6 DNS apply failed on '" + name + "': " + Detail(primary6));
            return;
        }

        if (profile.Secondary6 is null)
            return;

        // The add form takes the plural "dnsservers"; "set dns" would overwrite the primary.
        var secondary6 = SafeRun(
            "netsh.exe", $"interface ipv6 add dnsservers name=\"{name}\" {profile.Secondary6} index=2");
        if (secondary6 is null || !secondary6.Success)
            Log.Warn("IPv6 secondary DNS failed on '" + name + "': " + Detail(secondary6));
    }

    private static string Detail(ProcessResult? result) =>
        result is null ? "the netsh process could not be started" : result.StdErr.Trim();

    /// <summary>
    /// Restores the adapters changed in this attempt to the servers they had at its start.
    /// An adapter whose first original was only saved by this attempt drops that entry once
    /// the restore worked, so a later revert does not return to an attempt that never landed.
    /// </summary>
    private bool Rollback(
        List<DnsAdapterOriginal> applied,
        Dictionary<string, DnsAdapterOriginal> saved,
        List<string> newlySaved)
    {
        var restored = true;
        foreach (var original in applied)
        {
            if (!RestoreOriginal(original))
                restored = false;
        }

        if (!restored)
        {
            Log.Error("DNS rollback did not restore every adapter; the saved originals were kept.");
            return false;
        }

        if (newlySaved.Count > 0)
        {
            try
            {
                foreach (var adapter in newlySaved)
                    saved.Remove(adapter);
                _originals.Save(saved);
            }
            catch (Exception ex)
            {
                // Keeping a stale original is safe: a revert would just reapply a value the
                // machine already has.
                Log.Warn("Could not drop the DNS originals saved by a failed attempt: " + ex.Message);
            }
        }

        if (applied.Count > 0)
            SafeRun("ipconfig.exe", "/flushdns");

        return true;
    }

    private bool RestoreOriginal(DnsAdapterOriginal original)
    {
        var name = Escape(original.AdapterName);
        var ipv4 = RestoreFamily(name, "ip", "dns", original.Ipv4);
        var ipv6 = RestoreFamily(name, "ipv6", "dnsservers", original.Ipv6);
        return ipv4 && ipv6;
    }

    private bool RestoreFamily(string name, string family, string noun, List<string> servers)
    {
        if (servers.Count == 0)
        {
            var dhcp = SafeRun("netsh.exe", $"interface {family} set {noun} name=\"{name}\" source=dhcp");
            return dhcp is { Success: true };
        }

        var first = SafeRun(
            "netsh.exe", $"interface {family} set {noun} name=\"{name}\" static {servers[0]}");
        if (first is null || !first.Success)
        {
            Log.Warn($"Could not restore the DNS servers on '{name}': " + Detail(first));
            return false;
        }

        for (var i = 1; i < servers.Count; i++)
        {
            var add = SafeRun(
                "netsh.exe", $"interface {family} add {noun} name=\"{name}\" {servers[i]} index={i + 1}");
            if (add is null || !add.Success)
            {
                Log.Warn($"Could not restore DNS server {servers[i]} on '{name}': " + Detail(add));
                return false;
            }
        }

        return true;
    }

    private static string Escape(string adapter) => adapter.Replace("\"", "\\\"");

    private sealed class WindowsDnsCommandRunner : IDnsCommandRunner
    {
        public ProcessResult Run(string fileName, string arguments) => ProcessRunner.Run(fileName, arguments);
    }

    private sealed class WindowsDnsAdapterSource : IDnsAdapterSource
    {
        public List<DnsAdapterOriginal> GetConnectedAdapters()
        {
            // Win32_NetworkAdapter with NetEnabled+PhysicalAdapter mirrors Get-NetAdapter's
            // "Up and HardwareInterface" filter without spawning a PowerShell process.
            var adapters = new List<DnsAdapterOriginal>();
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT GUID, NetConnectionID FROM Win32_NetworkAdapter " +
                    "WHERE NetEnabled = TRUE AND PhysicalAdapter = TRUE");
                foreach (var adapter in searcher.Get())
                {
                    if (adapter["NetConnectionID"] is not string name || name.Length == 0)
                        continue;

                    // A physical adapter without a GUID cannot have its servers read. Reporting
                    // it as "no DNS" would hide a real adapter from the apply, so this fails.
                    if (adapter["GUID"] is not string guid || guid.Length == 0)
                        throw new DnsAdapterReadException(
                            $"The adapter '{name}' has no interface identifier, so its DNS servers cannot be read.");

                    adapters.Add(new DnsAdapterOriginal(
                        name,
                        ReadStaticServers(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + guid),
                        ReadStaticServers(@"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\" + guid)));
                }
            }
            catch (DnsAdapterReadException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // An unreadable adapter list must never look like "this machine has none":
                // the caller would then claim success while every adapter kept its old DNS.
                Log.Error("Adapter enumeration failed", ex);
                throw new DnsAdapterReadException("The network adapters could not be read: " + ex.Message, ex);
            }

            return adapters;
        }

        /// <summary>
        /// Static servers from one interface branch. A missing key or value is genuinely
        /// "DHCP on this adapter"; an access or read failure is not, and it throws.
        /// </summary>
        private static List<string> ReadStaticServers(string subKey)
        {
            var servers = new List<string>();
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKey);
                if (key?.GetValue("NameServer") is string text)
                {
                    foreach (var part in text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
                        servers.Add(part.Trim());
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Could not read the DNS servers from {subKey}", ex);
                throw new DnsAdapterReadException(
                    "The current DNS servers could not be read, so nothing was changed. " + ex.Message, ex);
            }

            return servers;
        }
    }
}

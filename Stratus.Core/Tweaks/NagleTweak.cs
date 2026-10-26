using Microsoft.Win32;
using Stratus.Core.Models;
using Stratus.Core.Services;

namespace Stratus.Core.Tweaks;

/// <summary>
/// Disables Nagle's algorithm on every physical network interface
/// (TcpAckFrequency=1 + TCPNoDelay=1) for lower latency in online games.
/// </summary>
public static class NagleTweak
{
    private const string InterfacesRoot = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    public const string TweakId = "net-nagle-off";

    private static List<string> GetEligibleInterfaces(RegistryService registry)
    {
        var result = new List<string>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(InterfacesRoot);
            if (key is null)
                return result;

            foreach (var sub in key.GetSubKeyNames())
            {
                var path = $@"{InterfacesRoot}\{sub}";
                var ip = registry.Read(RegistryHive.LocalMachine, path, "IPAddress");
                var dhcp = registry.Read(RegistryHive.LocalMachine, path, "DhcpIPAddress");

                var hasStatic = ip.Exists && ip.Value is string[] arr && arr.Any(s => !string.IsNullOrEmpty(s) && s != "0.0.0.0");
                var hasDhcp = dhcp.Exists && dhcp.Value is string s2 && !string.IsNullOrEmpty(s2) && s2 != "0.0.0.0";

                if (hasStatic || hasDhcp)
                {
                    // Skip loopback.
                    if (ip.Value is string[] a2 && a2.Contains("127.0.0.1"))
                        continue;
                    result.Add(path);
                }
            }
        }
        catch { /* best effort */ }

        return result;
    }

    public static bool IsApplied(RegistryService registry)
    {
        var ifaces = GetEligibleInterfaces(registry);
        return ifaces.Count > 0 && ifaces.All(p =>
            registry.ValueEquals(RegistryHive.LocalMachine, p, "TcpAckFrequency", RegistryValueKind.DWord, 1) &&
            registry.ValueEquals(RegistryHive.LocalMachine, p, "TCPNoDelay", RegistryValueKind.DWord, 1));
    }

    public static TweakResult Apply(RegistryService registry)
    {
        try
        {
            var ifaces = GetEligibleInterfaces(registry);
            if (ifaces.Count == 0)
                return TweakResult.Fail("No active network interfaces found.");

            foreach (var path in ifaces)
            {
                WriteIfChanged(registry, path, "TcpAckFrequency");
                WriteIfChanged(registry, path, "TCPNoDelay");
            }
            return TweakResult.Ok;
        }
        catch (Exception ex)
        {
            return TweakResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// First writer wins: when a value is already at the target (a previous run of this tweak,
    /// or a default that equals the target) it is left untouched, so re-applying never replaces
    /// the stored backup of the original state with already-tweaked values.
    /// </summary>
    private static void WriteIfChanged(RegistryService registry, string path, string name)
    {
        var current = registry.Read(RegistryHive.LocalMachine, path, name);
        if (current.Exists && RegistryService.ValuesEqual(RegistryValueKind.DWord, current.Value, 1))
            return;

        registry.WriteValue(RegistryHive.LocalMachine, path, name, RegistryValueKind.DWord, 1, TweakId);
    }

    public static TweakResult Revert(RegistryService registry)
    {
        try
        {
            var restored = registry.RestoreAll(TweakId);
            return restored
                ? TweakResult.Ok
                : TweakResult.Fail("Some Nagle registry values could not be restored. See the Stratus log for details.");
        }
        catch (Exception ex)
        {
            return TweakResult.Fail(ex.Message);
        }
    }

    public static Tweak Create(RegistryService registry) => new DelegateTweak(
        TweakId,
        "Disable Nagle's algorithm",
        "Sets TcpAckFrequency=1 and TCPNoDelay=1 on all physical network interfaces. Reduces small-packet latency in online games. Negligible impact on throughput.",
        TweakCategory.Network,
        () => IsApplied(registry),
        () => Apply(registry),
        () => Revert(registry))
    {
        Weight = 3,
        RequiresReboot = true
    };
}

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

        // Enumeration and value failures propagate: a probe that cannot read a value is
        // unknown state, not "not applied". Apply refuses on the same reads.
        using var key = Registry.LocalMachine.OpenSubKey(InterfacesRoot);
        if (key is null)
            return result;

        foreach (var sub in key.GetSubKeyNames())
        {
            var path = $@"{InterfacesRoot}\{sub}";
            var ip = registry.ReadRequired(RegistryHive.LocalMachine, path, "IPAddress");
            var dhcp = registry.ReadRequired(RegistryHive.LocalMachine, path, "DhcpIPAddress");

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

        return result;
    }

    public static bool IsApplied(RegistryService registry)
    {
        var ifaces = GetEligibleInterfaces(registry);
        return ifaces.Count > 0 && ifaces.All(p =>
            ValueIs(registry, p, "TcpAckFrequency", 1) &&
            ValueIs(registry, p, "TCPNoDelay", 1));
    }

    /// <summary>False when the value is absent or differs; a failed read throws as unknown.</summary>
    private static bool ValueIs(RegistryService registry, string path, string name, long expected)
    {
        var read = registry.ReadRequired(RegistryHive.LocalMachine, path, name);
        return read.Exists && read.Kind == RegistryValueKind.DWord
            && Convert.ToInt64(read.Value, System.Globalization.CultureInfo.InvariantCulture) == expected;
    }

    /// <summary>
    /// Writes every interface inside one attempt. A failure on the fifth value puts the first four
    /// back, so the machine is never left with Nagle disabled on only some interfaces.
    /// </summary>
    public static TweakResult Apply(RegistryService registry)
    {
        var ifaces = GetEligibleInterfaces(registry);
        if (ifaces.Count == 0)
            return TweakResult.Fail("No active network interfaces found.");

        var attempt = registry.BeginAttempt(TweakId);
        try
        {
            foreach (var path in ifaces)
            {
                attempt.Write(RegistryHive.LocalMachine, path, "TcpAckFrequency", RegistryValueKind.DWord, 1);
                attempt.Write(RegistryHive.LocalMachine, path, "TCPNoDelay", RegistryValueKind.DWord, 1);
            }

            attempt.Commit();
            return TweakResult.Ok;
        }
        catch (Exception ex)
        {
            // The attempt refuses to save an original it could not read, so an interface with an
            // unreadable value stops the whole change and the earlier interfaces are rolled back.
            var rolledBack = attempt.Rollback();
            Log.Warn($"Applying Nagle failed: {ex.Message}");
            return TweakResult.Fail(rolledBack
                ? ex.Message
                : ex.Message + " The half-finished interface changes could not be rolled back; see the Stratus log.");
        }
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
            Log.Error("Reverting Nagle failed", ex);
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

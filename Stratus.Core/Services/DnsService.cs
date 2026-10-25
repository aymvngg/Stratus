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
        var result = ProcessRunner.PowerShell(
            "Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and $_.HardwareInterface } | Select-Object -ExpandProperty Name", 30_000);

        return result.StdOut
            .Split('\n')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
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

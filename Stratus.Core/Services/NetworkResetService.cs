namespace Stratus.Core.Services;

/// <summary>
/// Repairs the TCP/IP stack: winsock catalogue reset, IPv4/IPv6 stack reset and a DNS
/// cache flush. All of it is Microsoft's own tooling, no third-party binaries.
/// A restart is required for the stack resets to take effect.
/// </summary>
public static class NetworkResetService
{
    public static (bool Ok, string Message) Reset()
    {
        var steps = new (string Name, string File, string Args)[]
        {
            ("winsock", "netsh.exe", "winsock reset"),
            ("TCP/IP stack", "netsh.exe", "int ip reset"),
            ("DNS cache", "ipconfig.exe", "/flushdns"),
            ("DNS registration", "ipconfig.exe", "/registerdns"),
        };

        var done = new List<string>();
        var failed = new List<string>();

        foreach (var (name, file, args) in steps)
        {
            var result = ProcessRunner.Run(file, args, 60_000);
            if (result.Success)
            {
                done.Add(name);
            }
            else
            {
                failed.Add(name);
                Log.Warn($"Network reset step '{name}' failed (exit {result.ExitCode}): {result.StdErr}");
            }
        }

        Log.Info($"Network reset: done [{string.Join(", ", done)}], failed [{string.Join(", ", failed)}]");

        if (failed.Count > 0)
            return (false, $"Partial reset. Failed: {string.Join(", ", failed)}. Completed: {string.Join(", ", done)}.");

        return (true,
            "Network stack reset complete: winsock, TCP/IP, DNS cache and DNS registration. Restart Windows to finish applying it.");
    }
}

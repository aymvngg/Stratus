using System.Diagnostics;
using System.Text;

namespace Stratus.Core.Services;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

public static class ProcessRunner
{
    public static ProcessResult Run(string fileName, string arguments, int timeoutMs = 20_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new ProcessResult(-1, stdout, "Timed out");
        }

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    public static ProcessResult PowerShell(string script, int timeoutMs = 60_000) =>
        Run("powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"",
            timeoutMs);
}

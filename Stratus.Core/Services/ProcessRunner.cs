using System.Diagnostics;
using System.Text;

namespace Stratus.Core.Services;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

public static class ProcessRunner
{
    /// <param name="encoding">
    /// Console encoding of the child. sfc.exe writes UTF-16LE when its output is redirected,
    /// so callers pass Encoding.Unicode for it; most tools are fine with the default.
    /// </param>
    public static ProcessResult Run(
        string fileName,
        string arguments,
        int timeoutMs = 20_000,
        Encoding? encoding = null)
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

        if (encoding is not null)
        {
            psi.StandardOutputEncoding = encoding;
            psi.StandardErrorEncoding = encoding;
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Drain both pipes at the same time: reading stdout to the end first can deadlock when
        // the child fills the stderr buffer while we are blocked on stdout.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new ProcessResult(-1, ReadOrEmpty(stdoutTask), "Timed out");
        }

        Task.WaitAll(stdoutTask, stderrTask);
        return new ProcessResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    public static ProcessResult PowerShell(string script, int timeoutMs = 60_000)
    {
        // EncodedCommand is immune to shell quoting: the script arrives verbatim.
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return Run("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
            timeoutMs);
    }

    private static string ReadOrEmpty(Task<string> task) =>
        task.Wait(TimeSpan.FromSeconds(2)) ? task.Result : "";
}

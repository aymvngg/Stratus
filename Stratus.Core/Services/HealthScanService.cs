using System.Text.RegularExpressions;

namespace Stratus.Core.Services;

/// <summary>Outcome of one Windows health scan (SFC or DISM), with a short human summary.</summary>
public sealed record HealthScanResult(
    string Title,
    bool Success,
    int ExitCode,
    string Summary,
    string Detail,
    TimeSpan Duration);

/// <summary>
/// Runs the built-in Windows repair tools and turns their noisy console output into
/// one readable line plus the raw detail. Marker matching covers English and French
/// Windows, since SFC and DISM print localized text.
/// </summary>
public static class HealthScanService
{
    private static readonly (string Marker, string Summary, bool Healthy)[] Markers =
    [
        ("did not find any integrity violations", "No integrity violations found.", true),
        ("n'a trouvé aucune violation d'intégrité", "No integrity violations found.", true),
        ("no component store corruption detected", "No component store corruption detected.", true),
        ("aucune corruption du magasin de composants", "No component store corruption detected.", true),
        ("successfully repaired", "Corrupt files were found and repaired.", true),
        ("réparé", "Corrupt files were found and repaired.", true),
        ("unable to fix", "Corruption found, but Windows could not repair it.", false),
        ("impossible de réparer", "Corruption found, but Windows could not repair it.", false),
        ("the operation completed successfully", "Operation completed successfully.", true),
        ("l'opération a réussi", "Operation completed successfully.", true),
        ("error", "The tool reported an error.", false),
        ("erreur", "The tool reported an error.", false),
    ];

    /// <summary>SFC verifies protected system files and repairs them from the component store.</summary>
    public static HealthScanResult RunSfc() =>
        // sfc.exe emits UTF-16LE when its output is redirected; decoding it as ANSI turns the
        // text into NUL-interleaved garbage and no summary marker ever matches.
        Execute("System file check (SFC)", "sfc.exe", "/scannow", 40 * 60_000, System.Text.Encoding.Unicode);

    /// <summary>DISM ScanHealth checks the component store without changing anything.</summary>
    public static HealthScanResult RunDismScan() =>
        Execute("Component store scan (DISM)", "dism.exe", "/Online /Cleanup-Image /ScanHealth", 40 * 60_000);

    /// <summary>DISM RestoreHealth repairs the component store itself.</summary>
    public static HealthScanResult RunDismRestore() =>
        Execute("Component store repair (DISM)", "dism.exe", "/Online /Cleanup-Image /RestoreHealth", 60 * 60_000);

    private static HealthScanResult Execute(
        string title,
        string file,
        string arguments,
        int timeoutMs,
        System.Text.Encoding? encoding = null)
    {
        var started = DateTime.UtcNow;
        ProcessResult result;
        try
        {
            result = ProcessRunner.Run(file, arguments, timeoutMs, encoding);
        }
        catch (Exception ex)
        {
            Log.Error($"{title} could not start", ex);
            return new HealthScanResult(title, false, -1, $"Could not start {file}: {ex.Message}", "", TimeSpan.Zero);
        }

        var duration = DateTime.UtcNow - started;
        var raw = result.StdOut + Environment.NewLine + result.StdErr;
        var detail = Normalize(raw);

        Log.Info($"{title}: exit {result.ExitCode} in {duration.TotalMinutes:F1} min");

        // The UI only shows a truncated excerpt, so keep the whole output in the log.
        if (detail.Length > 0)
            Log.Info($"{title} output:{Environment.NewLine}{detail}");

        return new HealthScanResult(title, result.Success, result.ExitCode, Summarize(detail, result.ExitCode), detail, duration);
    }

    /// <summary>Drops ANSI escapes, progress bars and blank lines so the detail is readable.</summary>
    internal static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var text = Regex.Replace(raw, @"\x1B\[[0-9;?]*[a-zA-Z]", "");
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('\uFEFF');

        var lines = text.Split('\n')
            .Select(l => l.TrimEnd())
            .Select(l => Regex.Replace(l, @"\s{2,}", " "))
            .Where(l => l.Length > 0)
            .Where(l => !Regex.IsMatch(l, @"^[\s\.\|\-\\/]*$"))
            .Where(l => !Regex.IsMatch(l, @"^\d{1,3}([.,]\d+)?%"))
            .ToList();

        // DISM repeats a progress line dozens of times; collapse consecutive duplicates.
        var deduped = new List<string>();
        foreach (var line in lines)
        {
            if (deduped.Count > 0 && deduped[^1] == line)
                continue;
            deduped.Add(line);
        }

        return string.Join(Environment.NewLine, deduped);
    }

    internal static string Summarize(string detail, int exitCode)
    {
        var lower = detail.ToLowerInvariant();
        foreach (var (marker, summary, healthy) in Markers)
        {
            if (!lower.Contains(marker))
                continue;
            if (healthy && exitCode != 0)
                continue;
            return summary;
        }

        return exitCode == 0
            ? "Completed. Windows reported no problems."
            : $"The tool exited with code {exitCode}. Open the detail below for the exact lines.";
    }
}

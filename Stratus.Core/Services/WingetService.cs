using System.Text.Json;

namespace Stratus.Core.Services;

/// <summary>Outcome of one winget operation, with a message ready for the UI.</summary>
public sealed record AppOperationResult(bool Success, string Message);

/// <summary>
/// Installs, upgrades and removes apps through the winget CLI.
///
/// winget prints localized text, so nothing here parses its human output for state.
/// The installed set comes from "winget export" JSON, which uses stable identifiers.
/// </summary>
public sealed class WingetService
{
    private const int QueryTimeoutMs = 120_000;
    private const int InstallTimeoutMs = 900_000;
    private const int UninstallTimeoutMs = 300_000;

    /// <summary>winget version text, or null when winget is not installed.</summary>
    public string? Version()
    {
        try
        {
            var result = ProcessRunner.Run("winget.exe", "--version", 20_000);
            return result.Success ? result.StdOut.Trim() : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not run winget: " + ex.Message);
            return null;
        }
    }

    public bool IsAvailable() => Version() is { Length: > 0 };

    /// <summary>Package identifiers installed on this machine, matched against the catalog later.</summary>
    public Task<HashSet<string>> GetInstalledIdsAsync() => Task.Run(ExportInstalledIds);

    private HashSet<string> ExportInstalledIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(Path.GetTempPath(), "stratus-winget-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var result = ProcessRunner.Run(
                "winget.exe",
                $"export -o \"{path}\" --accept-source-agreements",
                QueryTimeoutMs);

            if (!result.Success || !File.Exists(path))
            {
                Log.Warn("Could not export the installed app list: " + FirstLine(result.StdErr, result.StdOut));
                return ids;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("Sources", out var sources)
                || sources.ValueKind != JsonValueKind.Array)
            {
                return ids;
            }

            foreach (var source in sources.EnumerateArray())
            {
                if (!source.TryGetProperty("Packages", out var packages)
                    || packages.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var package in packages.EnumerateArray())
                {
                    if (package.TryGetProperty("PackageIdentifier", out var id)
                        && id.GetString() is { Length: > 0 } value)
                    {
                        ids.Add(value);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the installed app list: " + ex.Message);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }

        return ids;
    }

    public Task<AppOperationResult> InstallAsync(AppEntry app) => Task.Run(() =>
    {
        var result = ProcessRunner.Run(
            "winget.exe",
            $"install --id \"{app.Id}\" -e --source winget --accept-package-agreements --accept-source-agreements --disable-interactivity",
            InstallTimeoutMs);
        return Interpret(app, result, "installed", "installed");
    });

    public Task<AppOperationResult> UpgradeAsync(AppEntry app) => Task.Run(() =>
    {
        var result = ProcessRunner.Run(
            "winget.exe",
            $"upgrade --id \"{app.Id}\" -e --source winget --accept-package-agreements --accept-source-agreements --disable-interactivity",
            InstallTimeoutMs);
        return Interpret(app, result, "upgraded", "upgraded");
    });

    public Task<AppOperationResult> UninstallAsync(AppEntry app) => Task.Run(() =>
    {
        var result = ProcessRunner.Run(
            "winget.exe",
            $"uninstall --id \"{app.Id}\" -e --accept-source-agreements --disable-interactivity",
            UninstallTimeoutMs);
        return Interpret(app, result, "removed", "removed");
    });

    private static AppOperationResult Interpret(
        AppEntry app,
        ProcessResult result,
        string pastVerb,
        string logVerb)
    {
        if (result.Success)
        {
            Log.Info($"winget {logVerb}: {app.Id}");
            return new AppOperationResult(true, $"{app.Name} {pastVerb}.");
        }

        var output = (result.StdErr + "\n" + result.StdOut).Trim();

        // winget treats "already installed" and "not installed" as errors. They are not.
        if (output.Contains("already installed", StringComparison.OrdinalIgnoreCase))
            return new AppOperationResult(true, $"{app.Name} is already installed.");

        if (output.Contains("No installed package found", StringComparison.OrdinalIgnoreCase)
            || output.Contains("not installed", StringComparison.OrdinalIgnoreCase))
            return new AppOperationResult(true, $"{app.Name} is not installed.");

        // winget also exits non-zero when there is simply nothing to upgrade. That is the
        // normal outcome for most apps in "upgrade installed" and must not read as a failure.
        if (output.Contains("No applicable upgrade", StringComparison.OrdinalIgnoreCase)
            || output.Contains("No newer package", StringComparison.OrdinalIgnoreCase)
            || output.Contains("already up to date", StringComparison.OrdinalIgnoreCase))
            return new AppOperationResult(true, $"{app.Name} is already up to date.");

        var error = FirstLine(result.StdErr, result.StdOut);
        if (error.Length == 0)
            error = "winget exited with code " + result.ExitCode + ".";

        Log.Warn($"winget {logVerb} failed for {app.Id}: {error}");
        return new AppOperationResult(false, $"{app.Name} could not be {pastVerb}: {error}");
    }

    /// <summary>First non-empty line, trimmed and capped so a UI message stays readable.</summary>
    private static string FirstLine(string first, string second)
    {
        foreach (var text in new[] { first, second })
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var line = text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0);

            if (line is null)
                continue;

            return line.Length > 220 ? line[..220] + "..." : line;
        }

        return "";
    }
}

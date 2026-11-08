using System.Text.Json;

namespace Stratus.Core.Services;

/// <summary>Outcome of one winget operation, with a message ready for the UI.</summary>
public sealed record AppOperationResult(bool Success, string Message);

/// <summary>
/// The installed set could not be read. Callers must treat this as unknown state, never as
/// "nothing is installed": an empty set would make every catalog app look absent.
/// </summary>
public sealed class WingetReadException : Exception
{
    public WingetReadException(string message) : base(message) { }
}

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

    private readonly Func<string, ProcessResult> _runExport;

    public WingetService() : this(DefaultExport) { }

    /// <summary>Test seam: replaces the winget process so every export outcome can be checked.</summary>
    internal WingetService(Func<string, ProcessResult> runExport) => _runExport = runExport;

    private static ProcessResult DefaultExport(string path) => ProcessRunner.Run(
        "winget.exe",
        $"export -o \"{path}\" --accept-source-agreements",
        QueryTimeoutMs);

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

    /// <summary>
    /// Package identifiers installed on this machine, matched against the catalog later.
    /// Throws <see cref="WingetReadException"/> when winget cannot produce a readable export,
    /// so callers never mistake a failed read for an empty machine.
    /// </summary>
    public Task<HashSet<string>> GetInstalledIdsAsync() => Task.Run(ExportInstalledIds);

    private HashSet<string> ExportInstalledIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(Path.GetTempPath(), "stratus-winget-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var result = _runExport(path);

            if (!result.Success)
            {
                var detail = FirstLine(result.StdErr, result.StdOut);
                var message = "winget export failed" + (detail.Length == 0 ? "." : ": " + detail);
                Log.Warn("Could not export the installed app list: " + message);
                throw new WingetReadException(message);
            }

            if (!File.Exists(path))
            {
                var message = "winget reported success but wrote no export file.";
                Log.Warn("Could not export the installed app list: " + message);
                throw new WingetReadException(message);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("Sources", out var sources)
                || sources.ValueKind != JsonValueKind.Array)
            {
                var message = "The winget export has no Sources list.";
                Log.Warn("Could not read the installed app list: " + message);
                throw new WingetReadException(message);
            }

            // winget requires Packages on every source and PackageIdentifier on every package.
            // A missing one is a truncated or foreign file; skipping it would drop installed
            // apps without a trace.
            var sourceIndex = 0;
            foreach (var source in sources.EnumerateArray())
            {
                if (source.ValueKind != JsonValueKind.Object)
                    throw new WingetReadException($"Source {sourceIndex} of the winget export is not an object.");

                if (!source.TryGetProperty("Packages", out var packages))
                    throw new WingetReadException($"Source {sourceIndex} of the winget export has no Packages list.");

                if (packages.ValueKind != JsonValueKind.Array)
                    throw new WingetReadException($"Source {sourceIndex} of the winget export has a Packages entry that is not a list.");

                var packageIndex = 0;
                foreach (var package in packages.EnumerateArray())
                {
                    if (package.ValueKind != JsonValueKind.Object)
                        throw new WingetReadException(
                            $"Package {packageIndex} of source {sourceIndex} is not an object.");

                    if (!package.TryGetProperty("PackageIdentifier", out var id))
                        throw new WingetReadException(
                            $"Package {packageIndex} of source {sourceIndex} has no PackageIdentifier.");

                    if (id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 } value
                        || string.IsNullOrWhiteSpace(value))
                    {
                        throw new WingetReadException(
                            $"Package {packageIndex} of source {sourceIndex} has a blank PackageIdentifier.");
                    }

                    ids.Add(value);
                    packageIndex++;
                }

                sourceIndex++;
            }

            return ids;
        }
        catch (WingetReadException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            Log.Warn("Could not read the installed app list: " + ex.Message);
            throw new WingetReadException("The winget export could not be parsed: " + ex.Message);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the installed app list: " + ex.Message);
            throw new WingetReadException("The winget export could not be read: " + ex.Message);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
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

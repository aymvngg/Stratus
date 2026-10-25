using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Stratus.Core.Services;

/// <summary>One release published on GitHub, resolved to the x64 package asset.</summary>
public sealed record UpdateInfo(string Tag, string Version, string Notes, string DownloadUrl, long SizeBytes)
{
    public string SizeText => SizeBytes <= 0 ? "" : $"{SizeBytes / 1024.0 / 1024.0:F0} MB";
}

/// <summary>
/// Checks GitHub for a newer release, stages it under ProgramData and hands the swap to a
/// detached script. The app itself never replaces files it is running from: the script waits
/// for this process to exit, renames the old folder aside, copies the staged build in and
/// starts it again. The renamed folder is removed on the next launch.
/// </summary>
public static class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/aymvngg/Stratus/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>Version of the running build. Local dev builds report 0.0.0 (CI stamps the tag).</summary>
    public static string CurrentVersion { get; } = ReadCurrentVersion();

    public static bool IsDevBuild => CurrentVersion == "0.0.0";

    public static string StagingRoot => Path.Combine(AppPaths.ProgramData, "updates");

    public static string InstallDirectory =>
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static string StagedDirectory(string version) => Path.Combine(StagingRoot, version);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Stratus-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static string ReadCurrentVersion()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly;
            var text = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                       ?? assembly.GetName().Version?.ToString()
                       ?? "0.0.0";

            var plus = text.IndexOf('+');
            if (plus > 0)
                text = text[..plus];
            var dash = text.IndexOf('-');
            if (dash > 0)
                text = text[..dash];

            return string.IsNullOrWhiteSpace(text) ? "0.0.0" : text.Trim();
        }
        catch
        {
            return "0.0.0";
        }
    }

    /// <summary>Parses "v1.4.0", "1.4.0.0" or "1.4" into a comparable version.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var cleaned = text.Trim().TrimStart('v', 'V');
        var plus = cleaned.IndexOf('+');
        if (plus > 0)
            cleaned = cleaned[..plus];
        var dash = cleaned.IndexOf('-');
        if (dash > 0)
            cleaned = cleaned[..dash];

        var parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        var numbers = new int[3];
        for (var i = 0; i < parts.Length && i < 3; i++)
        {
            if (!int.TryParse(parts[i], out numbers[i]))
                return null;
        }

        return new Version(numbers[0], numbers[1], numbers[2]);
    }

    public static bool IsNewer(string version) =>
        ParseVersion(version) is { } candidate &&
        ParseVersion(CurrentVersion) is { } current &&
        candidate > current;

    /// <summary>Reads the latest release. Returns null info plus a message when nothing usable was found.</summary>
    public static async Task<(UpdateInfo? Info, string? Error)> CheckAsync(CancellationToken token = default)
    {
        try
        {
            using var response = await Http.GetAsync(LatestReleaseUrl, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (null, $"GitHub returned {(int)response.StatusCode}.");

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            var root = document.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString() ?? ""
                : "";
            if (string.IsNullOrWhiteSpace(tag))
                return (null, "The latest release has no tag.");

            var notes = root.TryGetProperty("body", out var bodyElement)
                ? bodyElement.GetString() ?? ""
                : "";

            string? downloadUrl = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameElement)
                        ? nameElement.GetString() ?? ""
                        : "";
                    if (!name.StartsWith("Stratus-v", StringComparison.OrdinalIgnoreCase) ||
                        !name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                        continue;

                    downloadUrl = asset.TryGetProperty("browser_download_url", out var urlElement)
                        ? urlElement.GetString()
                        : null;
                    if (asset.TryGetProperty("size", out var sizeElement))
                        size = sizeElement.GetInt64();
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(downloadUrl))
                return (null, "The latest release has no x64 package.");

            var version = tag.TrimStart('v', 'V');
            return (new UpdateInfo(tag, version, notes, downloadUrl, size), null);
        }
        catch (Exception ex)
        {
            Log.Warn("Update check failed: " + ex.Message);
            return (null, ex.Message);
        }
    }

    /// <summary>Downloads and extracts a release into its own staging folder.</summary>
    public static async Task<(bool Ok, string? Error)> DownloadAsync(
        UpdateInfo info, IProgress<double>? progress = null, CancellationToken token = default)
    {
        var target = StagedDirectory(info.Version);
        try
        {
            if (Directory.Exists(target))
                Directory.Delete(target, true);
            Directory.CreateDirectory(target);

            var zipPath = Path.Combine(StagingRoot, $"Stratus-{info.Version}.zip");
            using (var response = await Http.GetAsync(
                       info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? info.SizeBytes;

                await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var file = File.Create(zipPath);

                var buffer = new byte[81920];
                long read = 0;
                int chunk;
                while ((chunk = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, chunk), token).ConfigureAwait(false);
                    read += chunk;
                    if (total > 0)
                        progress?.Report(Math.Clamp(read / (double)total, 0, 1));
                }
            }

            ZipFile.ExtractToDirectory(zipPath, target, true);
            TryDelete(zipPath);

            if (FindPayloadRoot(target) is null)
            {
                TryDeleteDirectory(target);
                return (false, "The downloaded package does not contain Stratus.exe.");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            Log.Error("Update download failed", ex);
            TryDeleteDirectory(target);
            return (false, ex.Message);
        }
    }

    /// <summary>True when a full download for this version is already staged on disk.</summary>
    public static bool IsStaged(string version) => FindPayloadRoot(StagedDirectory(version)) is not null;

    /// <summary>
    /// Starts a detached script that waits for Stratus to exit, moves the current install aside,
    /// copies the staged build in and relaunches it. Returns false when the script could not start.
    /// </summary>
    public static bool ApplyAndRestart(UpdateInfo info, out string error)
    {
        error = "";

        var payload = FindPayloadRoot(StagedDirectory(info.Version));
        if (payload is null)
        {
            error = "The staged package is missing Stratus.exe.";
            return false;
        }

        var install = InstallDirectory;
        var parent = Path.GetDirectoryName(install);
        if (string.IsNullOrEmpty(parent))
        {
            error = "Could not resolve the install folder.";
            return false;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var old = install + ".old-" + stamp;
        var script = Path.Combine(Path.GetTempPath(), $"stratus-update-{stamp}.cmd");
        var pid = Environment.ProcessId;

        var lines = new[]
        {
            "@echo off",
            "setlocal",
            ":wait",
            $"tasklist /FI \"PID eq {pid}\" /NH 2>nul | find \"{pid}\" >nul",
            "if not errorlevel 1 (",
            "  timeout /t 1 /nobreak >nul",
            "  goto wait",
            ")",
            "timeout /t 2 /nobreak >nul",
            $"ren \"{install}\" \"{old}\" >nul 2>&1",
            $"robocopy \"{payload}\" \"{install}\" /E /NFL /NDL /NJH /NJS /R:1 /W:1 >nul",
            $"start \"\" \"{install}\\Stratus.exe\"",
            $"rd /s /q \"{payload}\" >nul 2>&1",
            "exit /b 0",
        };

        try
        {
            File.WriteAllLines(script, lines);
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{script}\"\"")
            {
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
            });
            Log.Info($"Update to {info.Version} handed to {script}.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not start the update script", ex);
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Removes leftover "*.old-*" folders from earlier updates and stale staging folders.</summary>
    public static void CleanupOld()
    {
        try
        {
            var install = InstallDirectory;
            var parent = Path.GetDirectoryName(install);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                var prefix = Path.GetFileName(install) + ".old-";
                foreach (var dir in Directory.EnumerateDirectories(parent, prefix + "*"))
                    TryDeleteDirectory(dir);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not clean old install folders: " + ex.Message);
        }

        try
        {
            if (!Directory.Exists(StagingRoot))
                return;

            foreach (var dir in Directory.EnumerateDirectories(StagingRoot))
            {
                // Keep whatever is fully staged and newer than us; drop partial or old downloads.
                var name = Path.GetFileName(dir);
                if (ParseVersion(name) is { } version && ParseVersion(CurrentVersion) is { } current && version > current)
                    continue;
                TryDeleteDirectory(dir);
            }

            foreach (var file in Directory.EnumerateFiles(StagingRoot, "*.zip"))
                TryDelete(file);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not clean the update cache: " + ex.Message);
        }
    }

    /// <summary>Folder that actually contains Stratus.exe, looking one level down for nested archives.</summary>
    private static string? FindPayloadRoot(string root)
    {
        try
        {
            if (!Directory.Exists(root))
                return null;
            if (File.Exists(Path.Combine(root, "Stratus.exe")))
                return root;

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (File.Exists(Path.Combine(dir, "Stratus.exe")))
                    return dir;
            }
        }
        catch
        {
            // Unreadable staging folder is treated as "not staged".
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Leftovers are retried on the next launch.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
            // Locked or in use: retried on the next launch.
        }
    }
}

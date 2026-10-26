using System.Reflection;
using System.Net.Http;
using System.Text.Json;
using Velopack;
using Velopack.Sources;

namespace Stratus.Core.Services;

/// <summary>Latest GitHub release, as shown on the Tools page.</summary>
public sealed record UpdateCheck(string Tag, string Version, string Notes);

/// <summary>
/// Update flow built on Velopack. Installers produced by "vpk pack" contain an Update.exe
/// that owns the install folder; Stratus only checks, downloads and asks Velopack to apply.
/// Checks hit the GitHub API once for the human-readable tag and release notes, while the
/// actual package metadata comes from releases.win-x64.json attached to the same release.
/// Builds that are not Velopack-installed (dev runs, portable zip copies) cannot self-update.
/// </summary>
public static class UpdateService
{
    private const string RepoUrl = "https://github.com/aymvngg/Stratus";
    private const string LatestReleaseUrl = "https://api.github.com/repos/aymvngg/Stratus/releases/latest";

    private static readonly HttpClient Http = CreateClient();
    private static UpdateInfo? _downloaded;

    /// <summary>Version of the running build. Local dev builds report 0.0.0 (CI stamps the tag).</summary>
    public static string CurrentVersion { get; } = ReadCurrentVersion();

    /// <summary>True when running from a Velopack install (Update.exe next to the app).</summary>
    public static bool IsVelopackInstalled => Safe(() => CreateManager()?.IsInstalled ?? false, false);

    /// <summary>True when this build cannot update itself: version not stamped, or not an installed copy.</summary>
    public static bool IsDevBuild => CurrentVersion == "0.0.0" || !IsVelopackInstalled;

    /// <summary>Human explanation for why this build cannot self-update.</summary>
    public static string SelfUpdateBlockReason => CurrentVersion == "0.0.0"
        ? "Development builds cannot update themselves: publish a release to test the updater."
        : "Portable copies cannot update themselves: run the latest Setup.exe once to switch to the installed app.";

    /// <summary>An update that was downloaded but not applied yet (app was closed instead of restarting).</summary>
    public static VelopackAsset? PendingRestart => Safe(() => CreateManager()?.UpdatePendingRestart, null);

    public static UpdateManager? CreateManager()
    {
        try
        {
            return new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not create the update manager: " + ex.Message);
            return null;
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
            // Negative parts parse fine but Version's constructor rejects them.
            if (!int.TryParse(parts[i], out numbers[i]) || numbers[i] < 0)
                return null;
        }

        return new Version(numbers[0], numbers[1], numbers[2]);
    }

    public static bool IsNewer(string version) =>
        ParseVersion(version) is { } candidate &&
        ParseVersion(CurrentVersion) is { } current &&
        candidate > current;

    /// <summary>Reads the latest release tag and notes. Returns null info plus a message when nothing usable was found.</summary>
    public static async Task<(UpdateCheck? Info, string? Error)> CheckAsync(CancellationToken token = default)
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

            return (new UpdateCheck(tag, tag.TrimStart('v', 'V'), notes), null);
        }
        catch (Exception ex)
        {
            Log.Warn("Update check failed: " + ex.Message);
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Downloads the newest Velopack release (delta when possible, full package otherwise).
    /// Returns false with a message when nothing newer exists or the download failed.
    /// </summary>
    public static async Task<(bool Ok, UpdateInfo? Update, string? Error)> DownloadAsync(
        IProgress<double>? progress = null, CancellationToken token = default)
    {
        try
        {
            var mgr = CreateManager();
            if (mgr is null || !mgr.IsInstalled)
                return (false, null, SelfUpdateBlockReason);

            var update = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
                return (false, null, "No Velopack update is available.");

            await mgr.DownloadUpdatesAsync(
                update,
                p => progress?.Report(Math.Clamp(p / 100.0, 0, 1)),
                cancelToken: token).ConfigureAwait(false);

            _downloaded = update;
            return (true, update, null);
        }
        catch (Exception ex)
        {
            Log.Error("Update download failed", ex);
            return (false, null, ex.Message);
        }
    }

    /// <summary>
    /// Hands the swap to Velopack's Update.exe: it applies the downloaded (or pending) package
    /// and starts the new build after this process exits. Returns false when that could not start.
    /// </summary>
    public static bool ApplyAndRestart(out string error)
    {
        error = "";
        try
        {
            var mgr = CreateManager();
            if (mgr is null || !mgr.IsInstalled)
            {
                error = SelfUpdateBlockReason;
                return false;
            }

            // The button only offers a restart after a download in this session or a pending
            // asset from an earlier run, so no network round trip is needed (and none on the UI thread).
            UpdateInfo? update = _downloaded;
            if (update is null && mgr.UpdatePendingRestart is { } pending)
                update = new UpdateInfo(pending, isDowngrade: false);

            if (update is null)
            {
                error = "No downloaded update was found: check for updates first.";
                return false;
            }

            mgr.ApplyUpdatesAndRestart(update);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not apply the update", ex);
            error = ex.Message;
            return false;
        }
    }

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

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }
}

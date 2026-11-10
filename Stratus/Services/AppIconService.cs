using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stratus.Core.Services;

namespace Stratus.Services;

/// <summary>Fetches app icons from the winget-pkgs repository with a disk cache under
/// ProgramData. Icons are only downloaded and shown, never executed. The unauthenticated
/// GitHub API is heavily rate limited, so misses are remembered and a rate limited
/// session stops asking entirely.</summary>
internal static class AppIconService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly TimeSpan MissRetryAfter = TimeSpan.FromDays(7);
    private static bool _stopForSession;

    private static string CacheDir => Path.Combine(AppPaths.ProgramData, "icons");

    private sealed record DirEntry([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("type")] string Type);

    /// <summary>Returns the cached icon file path, downloading it first if needed.
    /// Null means "no icon known": the UI falls back to a letter badge.</summary>
    public static async Task<string?> GetIconPathAsync(string appId)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var safe = Sanitize(appId);
            var cached = Path.Combine(CacheDir, safe + ".png");
            if (File.Exists(cached))
                return cached;

            var miss = Path.Combine(CacheDir, safe + ".miss");
            if (File.Exists(miss) && DateTime.UtcNow - File.GetLastWriteTimeUtc(miss) < MissRetryAfter)
                return null;

            if (_stopForSession)
                return null;

            var iconUrl = await FindIconUrlAsync(appId);
            if (string.IsNullOrEmpty(iconUrl))
            {
                MarkMissed(miss);
                return null;
            }

            var bytes = await Http.GetByteArrayAsync(iconUrl);
            var temp = cached + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, cached, overwrite: true);
            return cached;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            _stopForSession = true;
            Log.Warn("Icon download hit the GitHub rate limit; icon fetching is paused for this session.");
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Icon download failed for {appId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>One API call lists version folders, then the default-locale manifest is read
    /// from raw.githubusercontent.com, which is not rate limited the same way.</summary>
    private static async Task<string?> FindIconUrlAsync(string appId)
    {
        var parts = appId.Split('.', 2);
        if (parts.Length != 2 || parts[0].Length == 0)
            return null;

        var publisher = parts[0];
        var app = parts[1];
        var letter = char.ToLowerInvariant(publisher[0]).ToString();

        var listUrl = "https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/" +
            Uri.EscapeDataString(letter) + "/" + Uri.EscapeDataString(publisher) + "/" + Uri.EscapeDataString(app);
        using var request = new HttpRequestMessage(HttpMethod.Get, listUrl);
        request.Headers.UserAgent.ParseAdd("Stratus");
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                _stopForSession = true;
                Log.Warn("Icon download hit the GitHub rate limit; icon fetching is paused for this session.");
            }
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        var entries = JsonSerializer.Deserialize<List<DirEntry>>(json);
        var version = entries?
            .Where(e => e.Type == "dir")
            .Select(e => e.Name)
            .OrderByDescending(VersionKey, Comparer<long[]>.Create(CompareVersionKeys))
            .FirstOrDefault();

        if (version is null)
            return null;

        var manifestUrl = "https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/" +
            Uri.EscapeDataString(letter) + "/" + Uri.EscapeDataString(publisher) + "/" + Uri.EscapeDataString(app) + "/" +
            Uri.EscapeDataString(version) + "/" +
            Uri.EscapeDataString(publisher) + "." + Uri.EscapeDataString(app) + ".locale.en-US.yaml";
        var yaml = await Http.GetStringAsync(manifestUrl);

        foreach (var line in yaml.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("IconUrl:", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["IconUrl:".Length..].Trim();
                return value.Length > 0 ? value : null;
            }
        }

        return null;
    }

    private static void MarkMissed(string missPath)
    {
        try
        {
            File.WriteAllText(missPath, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not write the icon miss marker: " + ex.Message);
        }
    }

    private static long[] VersionKey(string version)
    {
        var numeric = version.Split('-')[0];
        var parts = numeric.Split('.');
        var key = new long[Math.Max(parts.Length, 4)];
        for (var i = 0; i < parts.Length && i < key.Length; i++)
            long.TryParse(parts[i], out key[i]);
        return key;
    }

    private static int CompareVersionKeys(long[]? left, long[]? right)
    {
        if (left is null || right is null)
            return (left is null, right is null) switch
            {
                (true, true) => 0,
                (true, false) => -1,
                _ => 1,
            };

        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var a = i < left.Length ? left[i] : 0;
            var b = i < right.Length ? right[i] : 0;
            if (a != b)
                return a.CompareTo(b);
        }

        return 0;
    }

    private static string Sanitize(string id)
    {
        var chars = id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' || c == '.' ? c : '_').ToArray();
        return new string(chars);
    }
}

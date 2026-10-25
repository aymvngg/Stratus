using Stratus.Core.Services;

namespace Stratus.Core.Services;

/// <summary>
/// A cleanup location. Exactly one of the three matching modes applies:
/// whole tree (default), CacheDirNames (delete contents of matching subdirs only),
/// or FileGlobs (delete matching files in the root dir only).
/// </summary>
public sealed record CleanupTarget(
    string Id,
    string Name,
    string Path,
    bool DefaultChecked,
    string[]? StopServices = null,
    string[]? CacheDirNames = null,
    string[]? FileGlobs = null,
    bool RemoveRoot = false)
{
    public long SizeBytes { get; set; }
    public int FileCount { get; set; }
    public bool Exists { get; set; }
}

public sealed record CleanResult(long FreedBytes, int DeletedFiles, int FailedFiles);

/// <summary>
/// Best-effort cleaner: temp files, shader and browser caches, thumbnail cache,
/// WER reports, Delivery Optimization and Windows Update leftovers, old logs,
/// prefetch and previous Windows installations.
/// </summary>
public sealed class CleanupService
{
    private const string BrowserCacheDirNames = "Cache|Code Cache|GPUCache|Media Cache|ShaderCache|GrShaderCache";

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string WindowsRoot => Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? @"C:\";

    private static readonly List<CleanupTarget> Targets = new()
    {
        new("user-temp", "User temp files", Path.GetTempPath(), DefaultChecked: true),
        new("windows-temp", "Windows temp", Path.Combine(WindowsRoot, "Windows", "Temp"), true),
        new("thumbnails", "Thumbnail and icon cache",
            Path.Combine(LocalAppData, @"Microsoft\Windows\Explorer"), true,
            FileGlobs: new[] { "thumbcache_*.db", "iconcache_*.db" }),
        new("d3ds-cache", "DirectX shader cache", Path.Combine(LocalAppData, "D3DSCache"), true),
        new("nv-dxcache", "NVIDIA shader caches", Path.Combine(LocalAppData, "NVIDIA"), true),
        new("amd-cache", "AMD shader caches", Path.Combine(LocalAppData, "AMD"), true),
        new("edge-cache", "Microsoft Edge caches",
            Path.Combine(LocalAppData, @"Microsoft\Edge\User Data"), true,
            CacheDirNames: BrowserCacheDirNames.Split('|')),
        new("chrome-cache", "Google Chrome caches",
            Path.Combine(LocalAppData, @"Google\Chrome\User Data"), true,
            CacheDirNames: BrowserCacheDirNames.Split('|')),
        new("brave-cache", "Brave caches",
            Path.Combine(LocalAppData, @"BraveSoftware\Brave-Browser\User Data"), true,
            CacheDirNames: BrowserCacheDirNames.Split('|')),
        new("firefox-cache", "Firefox caches",
            Path.Combine(LocalAppData, @"Mozilla\Firefox\Profiles"), true,
            CacheDirNames: new[] { "cache2", "startupCache", "shader-cache" }),
        new("do-cache", "Delivery Optimization cache",
            Path.Combine(WindowsRoot, @"Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"), false,
            StopServices: new[] { "dosvc" }),
        new("wu-download", "Windows Update leftovers",
            Path.Combine(WindowsRoot, @"Windows\SoftwareDistribution\Download"), false,
            StopServices: new[] { "wuauserv" }),
        new("cbs-logs", "Old CBS/DISM logs", Path.Combine(WindowsRoot, @"Windows\Logs"), false),
        new("prefetch", "Prefetch", Path.Combine(WindowsRoot, @"Windows\Prefetch"), false),
        new("windows-old", "Previous Windows installation (Windows.old)",
            Path.Combine(WindowsRoot, "Windows.old"), false,
            RemoveRoot: true),
    };

    public List<CleanupTarget> Scan()
    {
        foreach (var t in Targets)
        {
            t.Exists = Directory.Exists(t.Path);
            if (!t.Exists)
            {
                t.SizeBytes = 0;
                t.FileCount = 0;
                continue;
            }

            try
            {
                (t.SizeBytes, t.FileCount) = t.CacheDirNames is not null
                    ? MeasureCacheDirs(t.Path, t.CacheDirNames)
                    : t.FileGlobs is not null
                        ? MeasureFiles(t.Path, t.FileGlobs)
                        : MeasureTree(t.Path);
            }
            catch
            {
                t.SizeBytes = 0;
                t.FileCount = 0;
            }
        }
        return Targets;
    }

    public CleanResult Clean(IEnumerable<CleanupTarget> selected, bool includeRecycleBin)
    {
        long freed = 0;
        int deleted = 0, failed = 0;

        foreach (var target in selected)
        {
            if (!Directory.Exists(target.Path))
                continue;

            var running = StopServices(target.StopServices);
            try
            {
                (long f, int d, int fail) result;
                if (target.CacheDirNames is not null)
                {
                    result = (0, 0, 0);
                    foreach (var dir in MatchedCacheDirs(target.Path, target.CacheDirNames))
                        result = Add(result, CleanDirectory(dir));
                }
                else if (target.FileGlobs is not null)
                {
                    result = CleanFiles(target.Path, target.FileGlobs);
                }
                else
                {
                    result = CleanDirectory(target.Path);
                    if (target.RemoveRoot)
                    {
                        try { Directory.Delete(target.Path, recursive: true); } catch { /* left in use */ }
                    }
                }
                (freed, deleted, failed) = Tally((freed, deleted, failed), result);
            }
            finally
            {
                StartServices(running);
            }
        }

        if (includeRecycleBin)
        {
            ProcessRunner.PowerShell("Clear-RecycleBin -Force -ErrorAction SilentlyContinue", 60_000);
        }

        Log.Info($"Cleanup freed {freed} bytes ({deleted} files deleted, {failed} in use and skipped)"
            + (includeRecycleBin ? ", recycle bin emptied" : "") + ".");
        return new CleanResult(freed, deleted, failed);
    }

    private static (long, int) MeasureTree(string path)
    {
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
        long size = 0;
        var count = 0;
        foreach (var f in Directory.EnumerateFiles(path, "*", opts))
        {
            try
            {
                size += new FileInfo(f).Length;
                count++;
            }
            catch { /* file vanished */ }
        }
        return (size, count);
    }

    private static (long, int) MeasureCacheDirs(string root, string[] names)
    {
        long size = 0;
        var count = 0;
        foreach (var dir in MatchedCacheDirs(root, names))
        {
            var (s, c) = MeasureTree(dir);
            size += s;
            count += c;
        }
        return (size, count);
    }

    private static (long, int) MeasureFiles(string root, string[] globs)
    {
        long size = 0;
        var count = 0;
        foreach (var glob in globs)
        {
            foreach (var file in Directory.EnumerateFiles(root, glob, new EnumerationOptions { IgnoreInaccessible = true }))
            {
                try
                {
                    size += new FileInfo(file).Length;
                    count++;
                }
                catch { /* file vanished */ }
            }
        }
        return (size, count);
    }

    private static IEnumerable<string> MatchedCacheDirs(string root, string[] names)
    {
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
        foreach (var dir in Directory.EnumerateDirectories(root, "*", opts))
        {
            if (names.Contains(Path.GetFileName(dir)))
                yield return dir;
        }
    }

    /// <summary>Stops services that lock the files, returning the ones that were actually running.</summary>
    private static List<string> StopServices(string[]? services)
    {
        var running = new List<string>();
        if (services is null)
            return running;

        foreach (var service in services)
        {
            try
            {
                // ServiceController reads the status in-process: no PowerShell spawn per service.
                using var controller = new System.ServiceProcess.ServiceController(service);
                if (controller.Status != System.ServiceProcess.ServiceControllerStatus.Running)
                    continue;
            }
            catch
            {
                continue; // not installed or not readable
            }

            if (ProcessRunner.Run("net.exe", $"stop {service}", 60_000).Success)
                running.Add(service);
        }
        return running;
    }

    private static void StartServices(List<string> services)
    {
        foreach (var service in services)
            ProcessRunner.Run("net.exe", $"start {service}", 60_000);
    }

    private static (long, int, int) Tally((long freed, int deleted, int failed) acc, (long freed, int deleted, int failed) add)
        => (acc.freed + add.freed, acc.deleted + add.deleted, acc.failed + add.failed);

    private static (long, int, int) Add((long freed, int deleted, int failed) a, (long freed, int deleted, int failed) b)
        => (a.freed + b.freed, a.deleted + b.deleted, a.failed + b.failed);

    /// <summary>Deletes everything under a directory (the directory itself is kept).</summary>
    private static (long freed, int deleted, int failed) CleanDirectory(string path)
    {
        long freed = 0;
        int deleted = 0, failed = 0;

        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
        foreach (var file in Directory.EnumerateFiles(path, "*", opts))
        {
            try
            {
                var len = new FileInfo(file).Length;
                File.Delete(file);
                freed += len;
                deleted++;
            }
            catch { failed++; }
        }

        // Remove now-empty subdirectories (bottom-up), keep the root itself.
        foreach (var dir in Directory.EnumerateDirectories(path, "*", opts).OrderByDescending(d => d.Length))
        {
            try { Directory.Delete(dir, recursive: false); } catch { /* not empty or locked */ }
        }

        return (freed, deleted, failed);
    }

    private static (long freed, int deleted, int failed) CleanFiles(string root, string[] globs)
    {
        long freed = 0;
        int deleted = 0, failed = 0;

        foreach (var glob in globs)
        {
            foreach (var file in Directory.EnumerateFiles(root, glob, new EnumerationOptions { IgnoreInaccessible = true }))
            {
                try
                {
                    var len = new FileInfo(file).Length;
                    File.Delete(file);
                    freed += len;
                    deleted++;
                }
                catch { failed++; }
            }
        }
        return (freed, deleted, failed);
    }
}

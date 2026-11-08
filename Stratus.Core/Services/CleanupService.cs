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

public sealed record CleanResult(
    long FreedBytes,
    int DeletedFiles,
    int FailedFiles,
    IReadOnlyList<string>? RestartFailures = null,
    bool RecycleBinFailed = false,
    int FailedTargets = 0);

/// <summary>Minimal service control seam so the cleanup restart path is testable without a service.</summary>
internal interface ICleanupServiceControl
{
    bool IsRunning(string name);
    bool Stop(string name);
    bool Start(string name);
}

internal sealed class WindowsCleanupServiceControl : ICleanupServiceControl
{
    public bool IsRunning(string name)
    {
        try
        {
            // ServiceController reads the status in-process: no PowerShell spawn per service.
            using var controller = new System.ServiceProcess.ServiceController(name);
            return controller.Status == System.ServiceProcess.ServiceControllerStatus.Running;
        }
        catch
        {
            return false; // not installed or not readable
        }
    }

    public bool Stop(string name) => ProcessRunner.Run("net.exe", $"stop {name}", 60_000).Success;

    public bool Start(string name)
    {
        try
        {
            using var controller = new System.ServiceProcess.ServiceController(name);
            if (controller.Status is System.ServiceProcess.ServiceControllerStatus.Running
                or System.ServiceProcess.ServiceControllerStatus.StartPending)
            {
                return true;
            }
        }
        catch
        {
            return false;
        }

        return ProcessRunner.Run("net.exe", $"start {name}", 60_000).Success;
    }
}

/// <summary>
/// Best-effort cleaner: temp files, shader and browser caches, thumbnail cache,
/// WER reports, Delivery Optimization and Windows Update leftovers, old logs,
/// prefetch and previous Windows installations.
/// </summary>
public sealed class CleanupService
{
    private const string BrowserCacheDirNames = "Cache|Code Cache|GPUCache|Media Cache|ShaderCache|GrShaderCache";

    private static readonly EnumerationOptions WalkOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false
    };

    private readonly ICleanupServiceControl _services;
    private readonly Func<string, int, ProcessResult> _powerShell;
    private readonly Func<CleanupTarget, Exception?>? _fault;

    public CleanupService() : this(new WindowsCleanupServiceControl(), ProcessRunner.PowerShell) { }

    internal CleanupService(ICleanupServiceControl services) :
        this(services, ProcessRunner.PowerShell) { }

    internal CleanupService(
        ICleanupServiceControl services,
        Func<string, int, ProcessResult> powerShell,
        Func<CleanupTarget, Exception?>? fault = null)
    {
        _services = services;
        _powerShell = powerShell;
        _fault = fault;
    }

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
        new("nv-dxcache", "NVIDIA driver caches",
            Path.Combine(LocalAppData, "NVIDIA"), true,
            CacheDirNames: new[] { "DXCache", "GLCache", "NV_Cache", "LocalCache" }),
        new("amd-cache", "AMD driver caches",
            Path.Combine(LocalAppData, "AMD"), true,
            CacheDirNames: new[] { "DXCache", "GLCache", "VkCache" }),
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
        new("crash-dumps", "Application crash dumps",
            Path.Combine(LocalAppData, "CrashDumps"), false),
        new("wer-reports", "Error reporting queue",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Microsoft\Windows\WER"), false),
        new("minidumps", "Kernel minidumps", Path.Combine(WindowsRoot, @"Windows\Minidump"), false),
        new("live-kernel-reports", "Live kernel reports",
            Path.Combine(WindowsRoot, @"Windows\LiveKernelReports"), false),
        new("windows-old", "Previous Windows installation (Windows.old)",
            Path.Combine(WindowsRoot, "Windows.old"), false,
            RemoveRoot: true),
    };

    public List<CleanupTarget> Scan()
    {
        // Work on per-scan copies: the catalog is shared static state and a second scan
        // (or a clean running in parallel) must never mutate rows someone else is reading.
        var targets = Targets.Select(t => t with { }).ToList();
        foreach (var t in targets)
        {
            try
            {
                Inspect(t);
            }
            catch
            {
                t.SizeBytes = 0;
                t.FileCount = 0;
            }
        }
        return targets;
    }

    /// <summary>Measures one target in place. Exposed for tests so they can use a temp root.</summary>
    internal static void Inspect(CleanupTarget target)
    {
        target.Exists = Directory.Exists(target.Path);
        target.SizeBytes = 0;
        target.FileCount = 0;
        if (!target.Exists || IsLink(target.Path))
            return;

        (target.SizeBytes, target.FileCount) = target.CacheDirNames is not null
            ? MeasureCacheDirs(target.Path, target.CacheDirNames)
            : target.FileGlobs is not null
                ? MeasureFiles(target.Path, target.FileGlobs)
                : MeasureTree(target.Path);
    }

    public CleanResult Clean(IEnumerable<CleanupTarget> selected, bool includeRecycleBin)
    {
        long freed = 0;
        int deleted = 0, failed = 0, failedTargets = 0;
        var restartFailures = new List<string>();

        foreach (var target in selected)
        {
            if (!Directory.Exists(target.Path))
                continue;

            if (IsLink(target.Path))
            {
                Log.Warn($"Cleanup skipped linked target '{target.Path}'.");
                continue;
            }

            // The restart owner is registered inside StopServices as each stop is requested,
            // and the call itself sits inside the try: a stop that throws mid-way, or a clean
            // that throws, still restarts every service whose stop was requested.
            var running = new List<string>();
            try
            {
                StopServices(target.StopServices, running);

                // Test seam for a target whose clean throws mid-way: a root that vanishes between
                // the exists check and enumeration cannot be staged deterministically. Production
                // passes no hook, so this never fires there.
                if (_fault is not null && _fault(target) is { } injected)
                    throw injected;

                (long f, int d, int fail) result;
                if (target.CacheDirNames is not null)
                {
                    result = (0, 0, 0);
                    foreach (var dir in MatchedCacheDirs(target.Path, target.CacheDirNames))
                        result = Add(result, CleanDirectory(dir.FullName));
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
                        // Non-recursive: the safe walk already removed the contents, and a
                        // recursive delete here could follow a link that appeared mid-clean.
                        try { Directory.Delete(target.Path, recursive: false); } catch { /* left in use */ }
                    }
                }
                (freed, deleted, failed) = Tally((freed, deleted, failed), result);
            }
            catch (Exception ex)
            {
                // One broken target must not abort the remaining ones or lose the totals so far.
                failedTargets++;
                Log.Error($"Cleanup of '{target.Path}' failed", ex);
            }
            finally
            {
                restartFailures.AddRange(StartServices(running));
            }
        }

        var recycleBinFailed = false;
        if (includeRecycleBin)
        {
            // The bin is emptied before this call returns, so a failed runner still leaves a
            // partial result that must be reported, not silently folded into a clean success.
            try
            {
                var runner = _powerShell("Clear-RecycleBin -Force -ErrorAction SilentlyContinue", 60_000);
                recycleBinFailed = runner is null || !runner.Success;
            }
            catch (Exception ex)
            {
                recycleBinFailed = true;
                Log.Error("Emptying the recycle bin failed", ex);
            }

            if (recycleBinFailed)
                Log.Warn("The recycle bin could not be emptied; the rest of the cleanup succeeded.");
        }

        Log.Info($"Cleanup freed {freed} bytes ({deleted} files deleted, {failed} in use and skipped)"
            + (failedTargets > 0 ? $", {failedTargets} target(s) failed" : "")
            + (restartFailures.Count > 0 ? $", could not restart [{string.Join(", ", restartFailures)}]" : "")
            + (recycleBinFailed ? ", the recycle bin could not be emptied" : ".")
            + (includeRecycleBin && !recycleBinFailed ? ", recycle bin emptied" : ""));
        return new CleanResult(freed, deleted, failed, restartFailures, recycleBinFailed, failedTargets);
    }

    private static (long, int) MeasureTree(string path)
    {
        long size = 0;
        var count = 0;
        foreach (var f in WalkFiles(path))
        {
            try
            {
                size += f.Length;
                count++;
            }
            catch { /* file vanished mid scan */ }
        }
        return (size, count);
    }

    private static (long, int) MeasureCacheDirs(string root, string[] names)
    {
        long size = 0;
        var count = 0;
        foreach (var dir in MatchedCacheDirs(root, names))
        {
            var (s, c) = MeasureTree(dir.FullName);
            size += s;
            count += c;
        }
        return (size, count);
    }

    private static (long, int) MeasureFiles(string root, string[] globs)
    {
        var size = 0L;
        var count = 0;
        var dir = new DirectoryInfo(root);
        foreach (var glob in globs)
        {
            foreach (var file in dir.EnumerateFiles(glob, new EnumerationOptions { IgnoreInaccessible = true }))
            {
                if (IsLink(file))
                    continue;

                try
                {
                    size += file.Length;
                    count++;
                }
                catch { /* file vanished mid scan */ }
            }
        }
        return (size, count);
    }

    private static IEnumerable<DirectoryInfo> MatchedCacheDirs(string root, string[] names)
    {
        foreach (var entry in Walk(root))
        {
            if (entry is DirectoryInfo dir && names.Contains(dir.Name, StringComparer.OrdinalIgnoreCase))
                yield return dir;
        }
    }

    /// <summary>
    /// Depth-first walk that never descends into a junction or other reparse point.
    /// Windows marks junctions, symlinks and mount points with ReparsePoint; following one
    /// would let a cache path delete files in an unrelated, possibly linked, location.
    /// </summary>
    private static IEnumerable<FileSystemInfo> Walk(string root)
    {
        if (IsLink(root))
            yield break;

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(current).GetFileSystemInfos("*", WalkOptions);
            }
            catch
            {
                continue; // unreadable folder: skip it, keep the rest
            }

            foreach (var entry in entries)
            {
                if (IsLink(entry))
                    continue;

                if (entry is DirectoryInfo dir)
                    pending.Push(dir.FullName);

                yield return entry;
            }
        }
    }

    private static IEnumerable<FileInfo> WalkFiles(string root)
    {
        foreach (var entry in Walk(root))
            if (entry is FileInfo file)
                yield return file;
    }

    private static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true; // unreadable attributes: treat as unsafe and skip
        }
    }

    private static bool IsLink(FileSystemInfo entry)
    {
        try
        {
            return (entry.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Requests a stop for each service and records it in <paramref name="running"/> before
    /// the request, so a stop that throws or hangs still has a restart owner. One service
    /// failing never blocks the others.
    /// </summary>
    private void StopServices(string[]? services, List<string> running)
    {
        if (services is null)
            return;

        foreach (var service in services)
        {
            try
            {
                if (!_services.IsRunning(service))
                    continue;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read the state of service {service}: {ex.Message}");
                continue;
            }

            // Responsibility is claimed before the request: the service may stop even when
            // the call throws or returns false.
            running.Add(service);
            try
            {
                if (!_services.Stop(service))
                    Log.Warn($"Could not stop service {service}; it is restarted anyway.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Stopping service {service} failed: {ex.Message}");
            }
        }
    }

    private List<string> StartServices(List<string> services)
    {
        var failed = new List<string>();
        foreach (var service in services)
        {
            try
            {
                if (!_services.Start(service))
                {
                    failed.Add(service);
                    Log.Warn($"Could not restart service {service}.");
                }
            }
            catch (Exception ex)
            {
                failed.Add(service);
                Log.Warn($"Restarting service {service} failed: {ex.Message}");
            }
        }
        return failed;
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

        // A bad target definition must never turn into "delete the whole disk": refuse empty
        // paths, drive roots and linked roots before recursing.
        if (string.IsNullOrWhiteSpace(path) || IsRootPath(path) || IsLink(path))
        {
            Log.Warn($"Cleanup skipped an unsafe target path: '{path}'.");
            return (0, 0, 0);
        }

        var subfolders = new List<DirectoryInfo>();
        foreach (var entry in Walk(path))
        {
            if (entry is DirectoryInfo dir)
            {
                subfolders.Add(dir);
                continue;
            }

            try
            {
                var len = ((FileInfo)entry).Length;
                File.Delete(entry.FullName);
                freed += len;
                deleted++;
            }
            catch { failed++; }
        }

        // Remove now-empty subdirectories (bottom-up), keep the root itself. Order by depth,
        // not string length: a shallow folder can have a longer path than a deeper one.
        foreach (var dir in subfolders
                     .OrderByDescending(d => d.FullName.Count(c => c == Path.DirectorySeparatorChar))
                     .ThenByDescending(d => d.FullName.Length))
        {
            try { dir.Delete(recursive: false); } catch { /* not empty or locked */ }
        }

        return (freed, deleted, failed);
    }

    /// <summary>True for "C:\" style paths, which must never be cleaned recursively.</summary>
    private static bool IsRootPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
                return true;

            return string.Equals(
                full.TrimEnd(Path.DirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true; // Unparseable path: treat as unsafe and skip.
        }
    }

    private static (long freed, int deleted, int failed) CleanFiles(string root, string[] globs)
    {
        long freed = 0;
        int deleted = 0, failed = 0;

        if (IsLink(root))
        {
            Log.Warn($"Cleanup skipped linked target '{root}'.");
            return (0, 0, 0);
        }

        foreach (var glob in globs)
        {
            foreach (var file in new DirectoryInfo(root).EnumerateFiles(glob, new EnumerationOptions { IgnoreInaccessible = true }))
            {
                // Same rule as the scan: linked entries are neither counted nor deleted, so the
                // reported size always matches what the clean actually removed.
                if (IsLink(file))
                    continue;

                try
                {
                    var len = file.Length;
                    File.Delete(file.FullName);
                    freed += len;
                    deleted++;
                }
                catch { failed++; }
            }
        }
        return (freed, deleted, failed);
    }
}

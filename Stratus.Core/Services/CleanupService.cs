using Stratus.Core.Services;

namespace Stratus.Core.Services;

public sealed record CleanupTarget(string Id, string Name, string Path, bool DefaultChecked, bool NeedsWuBounce)
{
    public long SizeBytes { get; set; }
    public int FileCount { get; set; }
    public bool Exists { get; set; }
}

public sealed record CleanResult(long FreedBytes, int DeletedFiles, int FailedFiles);

/// <summary>Best-effort cleaner: user/system temp, shader caches, WER, WU leftovers, old logs, prefetch.</summary>
public sealed class CleanupService
{
    private static readonly List<CleanupTarget> Targets = new()
    {
        new("user-temp", "User temp files", Path.GetTempPath(), DefaultChecked: true, NeedsWuBounce: false),
        new("windows-temp", "Windows temp", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), true, false),
        new("d3ds-cache", "DirectX shader cache", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D3DSCache"), true, false),
        new("nv-dxcache", "NVIDIA shader caches", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVIDIA"), true, false),
        new("amd-cache", "AMD shader caches", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AMD"), true, false),
        new("wer", "Error reports (WER)", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER"), true, false),
        new("wu-download", "Windows Update leftovers", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"SoftwareDistribution\Download"), false, true),
        new("cbs-logs", "Old CBS/DISM logs", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs"), false, false),
        new("prefetch", "Prefetch", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch"), false, false),
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
                var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
                long size = 0;
                var count = 0;
                foreach (var f in Directory.EnumerateFiles(t.Path, "*", opts))
                {
                    try
                    {
                        size += new FileInfo(f).Length;
                        count++;
                    }
                    catch { /* file vanished */ }
                }
                t.SizeBytes = size;
                t.FileCount = count;
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
            if (target.Id == "wu-download")
            {
                (freed, deleted, failed) = Tally((freed, deleted, failed), CleanWu(target));
                continue;
            }

            if (!Directory.Exists(target.Path))
                continue;

            (freed, deleted, failed) = Tally((freed, deleted, failed), CleanDirectory(target.Path));
        }

        if (includeRecycleBin)
        {
            ProcessRunner.PowerShell("Clear-RecycleBin -Force -ErrorAction SilentlyContinue", 60_000);
        }

        return new CleanResult(freed, deleted, failed);
    }

    private static (long, int, int) Tally((long freed, int deleted, int failed) acc, (long freed, int deleted, int failed) add)
        => (acc.freed + add.freed, acc.deleted + add.deleted, acc.failed + add.failed);

    /// <summary>Deletes everything under a directory (the directory itself is kept when it's a well-known system folder).</summary>
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

    private static (long freed, int deleted, int failed) CleanWu(CleanupTarget target)
    {
        if (!Directory.Exists(target.Path))
            return (0, 0, 0);

        ProcessRunner.Run("net.exe", "stop wuauserv", 30_000);
        try
        {
            return CleanDirectory(target.Path);
        }
        finally
        {
            ProcessRunner.Run("net.exe", "start wuauserv", 30_000);
        }
    }
}

using System.Diagnostics;
using Stratus.Core.Services;
using Xunit;

namespace Stratus.Tests;

/// <summary>Isolated temp tree for cleanup tests. Never points at a real user folder.</summary>
public sealed class TempTree : IDisposable
{
    public string Root { get; } = Path.Combine(
        Path.GetTempPath(), "stratus-cleanup-" + Guid.NewGuid().ToString("N"));

    public TempTree() => Directory.CreateDirectory(Root);

    public string Dir(params string[] parts)
    {
        var path = parts.Aggregate(Root, Path.Combine);
        Directory.CreateDirectory(path);
        return path;
    }

    public string File(string relative)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, "stratus-test");
        return path;
    }

    /// <summary>Creates a real NTFS junction. Junctions need no elevation, unlike symlinks.</summary>
    public string Junction(string linkRelative, string target)
    {
        var link = Path.Combine(Root, linkRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);

        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(start)!;
        process.WaitForExit(10_000);
        Assert.True(Directory.Exists(link), "The test junction could not be created: " + process.StandardError.ReadToEnd());
        return link;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}

public sealed class CleanupSafetyTests : IDisposable
{
    private readonly TempTree _tree = new();

    [Fact]
    public void Clean_DoesNotDeleteThroughJunction()
    {
        var outside = _tree.Dir("outside");
        var sentinel = _tree.File("outside/sentinel.txt");
        var target = _tree.Dir("target");
        _tree.File("target/ordinary.txt");
        _tree.Junction("target/nested/link", outside);

        var service = new CleanupService();
        var result = service.Clean([new CleanupTarget("test", "Test", target, true)], includeRecycleBin: false);

        Assert.True(System.IO.File.Exists(sentinel), "A file behind a junction was deleted");
        Assert.False(System.IO.File.Exists(Path.Combine(target, "ordinary.txt")));
        Assert.Equal(1, result.DeletedFiles);
    }

    [Fact]
    public void Scan_DoesNotCountFilesBehindJunction()
    {
        var outside = _tree.Dir("outside");
        _tree.File("outside/sentinel.txt");
        var target = _tree.Dir("target");
        _tree.File("target/ordinary.txt");
        _tree.Junction("target/link", outside);

        var item = new CleanupTarget("test", "Test", target, true);
        CleanupService.Inspect(item);

        Assert.True(item.Exists);
        Assert.Equal(1, item.FileCount);
    }

    [Fact]
    public void Clean_SkipsLinkedRoot()
    {
        var outside = _tree.Dir("outside");
        var sentinel = _tree.File("outside/sentinel.txt");
        var link = _tree.Junction("link", outside);

        var result = new CleanupService().Clean(
            [new CleanupTarget("test", "Test", link, true)], includeRecycleBin: false);

        Assert.True(System.IO.File.Exists(sentinel), "A linked root was traversed");
        Assert.Equal(0, result.DeletedFiles);
    }

    [Fact]
    public void Scan_ReportsLinkedRootAsNotCleanable()
    {
        var outside = _tree.Dir("outside");
        _tree.File("outside/sentinel.txt");
        var link = _tree.Junction("link", outside);

        var item = new CleanupTarget("test", "Test", link, true);
        CleanupService.Inspect(item);

        Assert.Equal(0, item.FileCount);
        Assert.Equal(0, item.SizeBytes);
    }

    [Fact]
    public void Clean_SkipsNestedJunctionInCacheDirScan()
    {
        var outside = _tree.Dir("outside");
        var sentinel = _tree.File("outside/sentinel.txt");
        var root = _tree.Dir("root");
        var cache = _tree.Dir("root/Cache");
        _tree.File("root/Cache/entry.bin");
        _tree.Junction("root/Cache/link", outside);

        var item = new CleanupTarget("test", "Test", root, true, CacheDirNames: ["Cache"]);
        CleanupService.Inspect(item);
        Assert.Equal(1, item.FileCount);

        var result = new CleanupService().Clean([item], includeRecycleBin: false);

        Assert.True(System.IO.File.Exists(sentinel));
        Assert.Equal(1, result.DeletedFiles);
    }

    public void Dispose() => _tree.Dispose();
}

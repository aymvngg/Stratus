using Stratus.Core.Services;
using Xunit;

namespace Stratus.Tests;

public class CommandLineTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\unins.exe\" /S", "C:\\Program Files\\App\\unins.exe", "/S")]
    [InlineData("C:\\Windows\\System32\\cmd.exe", "C:\\Windows\\System32\\cmd.exe", "")]
    [InlineData("msiexec /x {123}", "msiexec", "/x {123}")]
    public void Split_HandlesQuotedAndFallbackCases(string command, string expectedExe, string expectedArgs)
    {
        var (exe, args) = CommandLine.Split(command);
        Assert.Equal(expectedExe, exe);
        Assert.Equal(expectedArgs, args);
    }

    [Fact]
    public void Split_EmptyCommand_ReturnsEmptyParts()
    {
        var (exe, args) = CommandLine.Split("   ");
        Assert.Equal("", exe);
        Assert.Equal("", args);
    }

    [Fact]
    public void Split_UnquotedExistingFileWithArgs_ResolvesFullExe()
    {
        using var dir = TempDirectory.Create();
        var exe = Path.Combine(dir.Path, "stratus test exe.exe");
        File.WriteAllText(exe, "x");

        var (resolved, args) = CommandLine.Split(exe + " --quiet");

        Assert.Equal(exe, resolved);
        Assert.Equal("--quiet", args);
    }

    [Fact]
    public void Split_UnterminatedQuote_UsesWholeString()
    {
        var (exe, args) = CommandLine.Split("\"C:\\Missing Path\\app.exe");
        Assert.Equal("C:\\Missing Path\\app.exe", exe);
        Assert.Equal("", args);
    }

    [Fact]
    public void ResolveExistingPath_ReturnsNullForMissingFile()
    {
        Assert.Null(CommandLine.ResolveExistingPath("C:\\Definitely\\Not\\Here.exe"));
    }

    [Fact]
    public void ResolveExistingPath_ResolvesExistingFile()
    {
        using var dir = TempDirectory.Create();
        var exe = Path.Combine(dir.Path, "stratus resolve.exe");
        File.WriteAllText(exe, "x");

        Assert.Equal(exe, CommandLine.ResolveExistingPath("\"" + exe + "\" --run"));
    }

    [Fact]
    public void ResolveExistingPath_EmptyInput_ReturnsNull()
    {
        Assert.Null(CommandLine.ResolveExistingPath("  "));
    }
}

/// <summary>
/// A fresh folder per test. A fixed name under the shared temp folder lets a leftover file or
/// a parallel run decide the result of the test.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    private TempDirectory(string path) => Path = path;

    public string Path { get; }

    public static TempDirectory Create()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "stratus-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TempDirectory(path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}

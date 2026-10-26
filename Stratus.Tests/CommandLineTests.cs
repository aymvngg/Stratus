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
        var exe = Path.Combine(Path.GetTempPath(), "stratus test exe.exe");
        File.WriteAllText(exe, "x");
        try
        {
            var (resolved, args) = CommandLine.Split(exe + " --quiet");
            Assert.Equal(exe, resolved);
            Assert.Equal("--quiet", args);
        }
        finally
        {
            File.Delete(exe);
        }
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
        var exe = Path.Combine(Path.GetTempPath(), "stratus resolve.exe");
        File.WriteAllText(exe, "x");
        try
        {
            Assert.Equal(exe, CommandLine.ResolveExistingPath("\"" + exe + "\" --run"));
        }
        finally
        {
            File.Delete(exe);
        }
    }

    [Fact]
    public void ResolveExistingPath_EmptyInput_ReturnsNull()
    {
        Assert.Null(CommandLine.ResolveExistingPath("  "));
    }
}

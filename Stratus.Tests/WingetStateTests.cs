using Stratus.Core.Services;
using Xunit;

namespace Stratus.Tests;

/// <summary>
/// A failed winget export must be an error, not an empty installed set. Reading it as empty
/// made every catalog app look absent, so a profile saved in that state was silently
/// incomplete and an import would have reinstalled apps that were already there.
///
/// The export process is replaced through the internal test seam, so these tests never run
/// winget and never touch the machine.
/// </summary>
public class WingetStateTests
{
    // winget writes "Sources" and "Packages"; the reader matches that casing exactly and
    // reports a different shape instead of silently returning an empty set.
    private const string ValidExport = """
        {
          "Sources": [
            {
              "Packages": [
                { "PackageIdentifier": "Mozilla.Firefox" },
                { "PackageIdentifier": "7zip.7zip" }
              ]
            }
          ]
        }
        """;

    private static WingetService ServiceThatWrites(string? json, int exitCode = 0,
        string stdOut = "", string stdErr = "") => new(path =>
    {
        if (json is not null)
            File.WriteAllText(path, json);
        return new ProcessResult(exitCode, stdOut, stdErr);
    });

    [Fact]
    public async Task GetInstalledIds_ReadsIdentifiersFromTheExport()
    {
        var ids = await ServiceThatWrites(ValidExport).GetInstalledIdsAsync();

        Assert.Equal(2, ids.Count);
        Assert.Contains("Mozilla.Firefox", ids);
        Assert.Contains("7zip.7zip", ids);
    }

    [Fact]
    public async Task GetInstalledIds_IsCaseInsensitive()
    {
        var ids = await ServiceThatWrites(ValidExport).GetInstalledIdsAsync();

        Assert.Contains("mozilla.firefox", ids);
    }

    [Fact]
    public async Task GetInstalledIds_FailedExit_Throws()
    {
        var service = ServiceThatWrites(ValidExport, exitCode: 1, stdErr: "winget is broken");

        var ex = await Assert.ThrowsAsync<WingetReadException>(() => service.GetInstalledIdsAsync());

        Assert.Contains("winget export failed", ex.Message);
        Assert.Contains("winget is broken", ex.Message);
    }

    [Fact]
    public async Task GetInstalledIds_SuccessWithoutFile_Throws()
    {
        var service = ServiceThatWrites(json: null);

        var ex = await Assert.ThrowsAsync<WingetReadException>(() => service.GetInstalledIdsAsync());

        Assert.Contains("wrote no export file", ex.Message);
    }

    [Fact]
    public async Task GetInstalledIds_MalformedJson_Throws()
    {
        var service = ServiceThatWrites("{ not json");

        var ex = await Assert.ThrowsAsync<WingetReadException>(() => service.GetInstalledIdsAsync());

        Assert.Contains("could not be parsed", ex.Message);
    }

    [Fact]
    public async Task GetInstalledIds_ProcessCannotStart_IsAReadErrorNotAParseError()
    {
        // The message a user sees must match what failed: a missing winget is not bad JSON.
        var service = new WingetService(_ => throw new InvalidOperationException("winget.exe not found"));

        var ex = await Assert.ThrowsAsync<WingetReadException>(() => service.GetInstalledIdsAsync());

        Assert.Contains("could not be read", ex.Message);
        Assert.Contains("winget.exe not found", ex.Message);
        Assert.DoesNotContain("parsed", ex.Message);
    }

    [Fact]
    public async Task GetInstalledIds_MissingSources_Throws()
    {
        var service = ServiceThatWrites("""{"CreationDate":"2026-01-01"}""");

        var ex = await Assert.ThrowsAsync<WingetReadException>(() => service.GetInstalledIdsAsync());

        Assert.Contains("no Sources list", ex.Message);
    }

    [Fact]
    public async Task GetInstalledIds_SourcesNotAnArray_Throws()
    {
        var service = ServiceThatWrites("""{"Sources":"none"}""");

        var ex = await Assert.ThrowsAsync<WingetReadException>(() => service.GetInstalledIdsAsync());

        Assert.Contains("no Sources list", ex.Message);
    }

    [Fact]
    public async Task GetInstalledIds_PackagesNotAnArray_Throws()
    {
        var service = ServiceThatWrites("""{"Sources":[{"Packages":"none"}]}""");

        var ex = await Assert.ThrowsAsync<WingetReadException>(() => service.GetInstalledIdsAsync());

        Assert.Contains("not a list", ex.Message);
    }

    [Fact]
    public async Task GetInstalledIds_EmptyExport_ReturnsEmptySet()
    {
        // winget writes no source entry when there is nothing to export, and an empty
        // Packages list drops nothing, so both are a real answer rather than a failure.
        var emptySources = await ServiceThatWrites("""{"Sources":[]}""").GetInstalledIdsAsync();
        var emptyPackages = await ServiceThatWrites("""{"Sources":[{"Packages":[]}]}""").GetInstalledIdsAsync();

        Assert.Empty(emptySources);
        Assert.Empty(emptyPackages);
    }

    [Fact]
    public async Task GetInstalledIds_PackageWithoutIdentifier_Throws()
    {
        // PackageIdentifier is required by the winget schema; skipping the entry would
        // drop an installed app without a trace and make it look absent.
        var service = ServiceThatWrites("""
            {"Sources":[{"Packages":[
              {"PackageIdentifier":"Git.Git"},
              {}
            ]}]}
            """);

        var ex = await Assert.ThrowsAsync<WingetReadException>(() => service.GetInstalledIdsAsync());

        Assert.Contains("has no PackageIdentifier", ex.Message);
    }

    [Theory]
    [InlineData("""{"Sources":[{"Packages":[{"PackageIdentifier":""}]}]}""")]
    [InlineData("""{"Sources":[{"Packages":[{"PackageIdentifier":"  "}]}]}""")]
    [InlineData("""{"Sources":[{"Packages":[{"PackageIdentifier":null}]}]}""")]
    [InlineData("""{"Sources":[{"Packages":[{"PackageIdentifier":5}]}]}""")]
    public async Task GetInstalledIds_BlankOrNonStringIdentifier_Throws(string json)
    {
        var ex = await Assert.ThrowsAsync<WingetReadException>(
            () => ServiceThatWrites(json).GetInstalledIdsAsync());

        Assert.Contains("blank PackageIdentifier", ex.Message);
    }

    [Fact]
    public async Task GetInstalledIds_SourceWithoutPackages_Throws()
    {
        // Packages is required on every source. A missing key is a truncated or foreign
        // file, not a source that happens to hold nothing.
        var service = ServiceThatWrites("""{"Sources":[{"Name":"winget"}]}""");

        var ex = await Assert.ThrowsAsync<WingetReadException>(() => service.GetInstalledIdsAsync());

        Assert.Contains("has no Packages list", ex.Message);
    }

    [Fact]
    public async Task GetInstalledIds_NonObjectSourceOrPackage_Throws()
    {
        var badSource = ServiceThatWrites("""{"Sources":["winget"]}""");
        var badPackage = ServiceThatWrites("""{"Sources":[{"Packages":["Git.Git"]}]}""");

        await Assert.ThrowsAsync<WingetReadException>(() => badSource.GetInstalledIdsAsync());
        await Assert.ThrowsAsync<WingetReadException>(() => badPackage.GetInstalledIdsAsync());
    }

    [Fact]
    public async Task GetInstalledIds_HandlesSeveralSources()
    {
        var service = ServiceThatWrites("""
            {"Sources":[
              {"Packages":[{"PackageIdentifier":"Git.Git"}]},
              {"Packages":[{"PackageIdentifier":"VideoLAN.VLC"}]},
              {"Packages":[]}
            ]}
            """);

        var ids = await service.GetInstalledIdsAsync();

        Assert.Equal(2, ids.Count);
    }

    [Fact]
    public async Task GetInstalledIds_SchemaFromWingetDocs_IsAccepted()
    {
        // Shape taken from https://aka.ms/winget-packages.schema.2.0.json, minor fields omitted.
        var service = ServiceThatWrites("""
            {
              "WinGetVersion": "1.9.25200",
              "CreationDate": "2026-11-08T16:36:06.239-00:00",
              "Sources": [
                {
                  "SourceDetails": {
                    "Argument": "https://cdn.winget.microsoft.com/cache",
                    "Identifier": "Microsoft.Winget.Source_8wekyb3d8bbwe",
                    "Name": "winget",
                    "Type": "Microsoft.PreIndexed.Package"
                  },
                  "Packages": [
                    { "PackageIdentifier": "7zip.7zip" },
                    { "PackageIdentifier": "Git.Git", "Version": "2.47.0" }
                  ]
                }
              ]
            }
            """);

        var ids = await service.GetInstalledIdsAsync();

        Assert.Equal(2, ids.Count);
    }

    [Fact]
    public async Task GetInstalledIds_LeavesNoExportFileBehind()
    {
        var path = "";
        var service = new WingetService(p =>
        {
            path = p;
            File.WriteAllText(p, ValidExport);
            return new ProcessResult(0, "", "");
        });

        await service.GetInstalledIdsAsync();

        Assert.False(File.Exists(path));
    }
}

using System.Text.Json;
using Stratus.Core.Models;
using Stratus.Core.Services;
using Stratus.Core.Tweaks;
using Xunit;

namespace Stratus.Tests;

public class DocGeneratorTests : IDisposable
{
    private readonly TempFile _backupFile = new();
    private readonly TempFile _settingsFile = new();

    private List<Tweak> BuildCatalog() => TweakCatalog.Build(
        new RegistryService(new BackupStore(_backupFile.Path)),
        new PowerPlanService(),
        new SettingsStore(_settingsFile.Path),
        new BackupStore(_backupFile.Path));

    [Fact]
    public void Markdown_HasHeadingForEveryTweak()
    {
        var catalog = BuildCatalog();
        var markdown = DocGenerator.BuildMarkdown(catalog);

        foreach (var tweak in catalog)
            Assert.Contains($"### {tweak.Id}", markdown);
    }

    [Fact]
    public void Markdown_StartsWithTitleAndHasCategoryHeadings()
    {
        var catalog = BuildCatalog();
        var markdown = DocGenerator.BuildMarkdown(catalog);

        Assert.StartsWith("# Tweak reference", markdown);
        foreach (var category in catalog.Select(t => t.Category).Distinct())
            Assert.Contains($"\n## {category}\n", markdown);
    }

    [Fact]
    public void Markdown_EndsWithSingleNewline()
    {
        var markdown = DocGenerator.BuildMarkdown(BuildCatalog());

        Assert.EndsWith("\n", markdown);
        Assert.DoesNotContain("\r", markdown);
        Assert.False(markdown.EndsWith("\n\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Markdown_MarksRebootTweaks()
    {
        var catalog = BuildCatalog();
        var markdown = DocGenerator.BuildMarkdown(catalog);

        var rebootTweaks = catalog.Where(t => t.RequiresReboot).ToList();
        Assert.NotEmpty(rebootTweaks);
        foreach (var tweak in rebootTweaks)
            Assert.Contains($"(Risk: {tweak.Risk}, weight {tweak.Weight}) (needs a restart)", markdown);
    }

    [Fact]
    public void Markdown_IsAscii()
    {
        var markdown = DocGenerator.BuildMarkdown(BuildCatalog());

        Assert.All(markdown, c => Assert.True(c < 128, $"Non-ASCII character U+{(int)c:X4}"));
    }

    [Fact]
    public void Json_HasOneEntryPerTweak()
    {
        var catalog = BuildCatalog();
        var json = DocGenerator.BuildJson(catalog);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(catalog.Count, root.GetArrayLength());

        foreach (var entry in root.EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("id").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("name").GetString()));
            Assert.Equal(JsonValueKind.String, entry.GetProperty("description").ValueKind);
            Assert.Equal(JsonValueKind.String, entry.GetProperty("category").ValueKind);
            Assert.Equal(JsonValueKind.String, entry.GetProperty("risk").ValueKind);
            Assert.Equal(JsonValueKind.Number, entry.GetProperty("weight").ValueKind);
            Assert.True(entry.GetProperty("requiresReboot").ValueKind is JsonValueKind.True or JsonValueKind.False);
        }
    }

    [Fact]
    public void Json_CategoriesAndRisksUseEnumNames()
    {
        var catalog = BuildCatalog();
        var json = DocGenerator.BuildJson(catalog);

        using var document = JsonDocument.Parse(json);
        var entries = document.RootElement.EnumerateArray().ToList();

        foreach (var (entry, tweak) in entries.Zip(catalog.OrderBy(t => (int)t.Category)
                     .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(t => t.Id, StringComparer.OrdinalIgnoreCase)))
        {
            Assert.Equal(tweak.Category.ToString(), entry.GetProperty("category").GetString());
            Assert.Equal(tweak.Risk.ToString(), entry.GetProperty("risk").GetString());
            Assert.Equal(tweak.Id, entry.GetProperty("id").GetString());
        }
    }

    [Fact]
    public void BothOutputs_AreDeterministic()
    {
        var catalog = BuildCatalog();

        Assert.Equal(DocGenerator.BuildMarkdown(catalog), DocGenerator.BuildMarkdown(catalog));
        Assert.Equal(DocGenerator.BuildJson(catalog), DocGenerator.BuildJson(catalog));
    }

    public void Dispose()
    {
        _backupFile.Dispose();
        _settingsFile.Dispose();
    }
}

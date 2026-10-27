using Stratus.Core.Services;
using Xunit;

namespace Stratus.Tests;

public class AppCatalogTests
{
    private static readonly string[] KnownCategories =
    [
        "Browsers", "Communication", "Development", "Media", "Productivity", "Utilities", "Gaming"
    ];

    [Fact]
    public void Catalog_HasNoDuplicateIds()
    {
        var duplicates = AppCatalog.Apps
            .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Catalog_EntriesAreComplete()
    {
        foreach (var app in AppCatalog.Apps)
        {
            Assert.False(string.IsNullOrWhiteSpace(app.Id), "an entry has no winget id");
            Assert.False(string.IsNullOrWhiteSpace(app.Name), app.Id + " has no name");
            Assert.False(string.IsNullOrWhiteSpace(app.Description), app.Id + " has no description");
            Assert.Contains(app.Category, KnownCategories);
        }
    }

    [Fact]
    public void Catalog_IdsLookLikeWingetIdentifiers()
    {
        foreach (var app in AppCatalog.Apps)
        {
            // winget identifiers are Publisher.Product with no spaces.
            Assert.Contains('.', app.Id);
            Assert.DoesNotContain(' ', app.Id);
            Assert.Equal(app.Id.Trim(), app.Id);
        }
    }

    [Fact]
    public void Catalog_HasRecommendedEntries()
    {
        Assert.True(AppCatalog.Apps.Count(a => a.Recommended) >= 10);
    }

    [Fact]
    public void Catalog_EveryCategoryHasApps()
    {
        foreach (var category in KnownCategories)
            Assert.Contains(AppCatalog.Apps, a => a.Category == category);
    }
}

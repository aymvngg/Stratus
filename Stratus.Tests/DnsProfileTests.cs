using Stratus.Core.Services;
using Xunit;

namespace Stratus.Tests;

public class DnsProfileTests
{
    [Fact]
    public void Profiles_HaveUniqueIdsAndLabels()
    {
        var ids = DnsService.Profiles.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var labels = DnsService.Profiles.Select(p => p.Label).ToList();
        Assert.Equal(labels.Count, labels.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Profiles_AlwaysIncludeAutomatic()
    {
        Assert.Contains(DnsService.Profiles, p => p.Id == "auto" && p.Primary is null);
    }

    [Fact]
    public void Profiles_StaticEntriesHaveAddresses()
    {
        foreach (var profile in DnsService.Profiles.Where(p => p.Id != "auto"))
        {
            Assert.False(string.IsNullOrWhiteSpace(profile.Primary), $"{profile.Id} has no primary address");
            Assert.True(System.Net.IPAddress.TryParse(profile.Primary, out _),
                $"{profile.Id} primary address is not an IP address");

            if (profile.Secondary is not null)
                Assert.True(System.Net.IPAddress.TryParse(profile.Secondary, out _),
                    $"{profile.Id} secondary address is not an IP address");
        }
    }

    [Fact]
    public void Profiles_IPv6PairIsComplete()
    {
        foreach (var profile in DnsService.Profiles.Where(p => p.Primary6 is not null))
        {
            Assert.True(System.Net.IPAddress.TryParse(profile.Primary6, out _),
                $"{profile.Id} primary IPv6 address is not an IP address");
            Assert.False(string.IsNullOrWhiteSpace(profile.Secondary6),
                $"{profile.Id} has a primary IPv6 address but no secondary");
            Assert.True(System.Net.IPAddress.TryParse(profile.Secondary6, out _),
                $"{profile.Id} secondary IPv6 address is not an IP address");
        }
    }
}

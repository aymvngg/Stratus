using Stratus.Core.Models;
using Stratus.Core.Services;
using Xunit;

namespace Stratus.Tests;

public class ReportStateTests
{
    private static readonly DateTime Generated = new(2026, 1, 2, 3, 4, 5);

    [Fact]
    public void Render_ListsUnreadableTweaksWithTheirErrors()
    {
        var markdown = Render(
            new FakeTweak("applied-tweak", applied: () => true),
            new FakeTweak("pending-tweak", risk: RiskLevel.Safe),
            new FakeTweak("unreadable-tweak", applied: () => throw new InvalidOperationException("Read denied")),
            new FakeTweak("unsupported-tweak", applies: () => false));

        Assert.Contains("## Could not read", markdown);
        Assert.Contains("unreadable-tweak (Gaming): Could not read this tweak's state: Read denied", markdown);
        Assert.Contains("- Not applicable on this machine: 1", markdown);
        Assert.Contains("- applied-tweak (Gaming, Recommended)", markdown);
        Assert.Contains("- pending-tweak (Gaming, Safe)", markdown);
    }

    [Fact]
    public void Render_UnreadableTweakIsNotOfferedAsPending()
    {
        var markdown = Render(new FakeTweak(
            "unreadable-tweak", risk: RiskLevel.Safe,
            applied: () => throw new InvalidOperationException("Read denied")));

        Assert.DoesNotContain("- unreadable-tweak (Gaming, Safe)", markdown);
    }

    [Fact]
    public void Render_FailedApplicabilityIsNotCountedAsNotApplicable()
    {
        var markdown = Render(
            new FakeTweak("unsupported-tweak", applies: () => false),
            new FakeTweak("unreadable-tweak", applies: () => throw new InvalidOperationException("Probe failed")));

        Assert.Contains("- Not applicable on this machine: 1", markdown);
        Assert.Contains("Probe failed", markdown);
    }

    [Fact]
    public void Render_UnknownStatesPreventTheAllAppliedClaim()
    {
        var markdown = Render(
            new FakeTweak("applied-tweak", applied: () => true),
            new FakeTweak("unreadable-tweak", applied: () => throw new InvalidOperationException("Read denied")));

        Assert.DoesNotContain("Every safe and recommended tweak is applied", markdown);
    }

    [Fact]
    public void Render_AllKnownAppliedKeepsTheCompleteClaim()
    {
        var markdown = Render(new FakeTweak("applied-tweak", applied: () => true));

        Assert.Contains("Every safe and recommended tweak is applied", markdown);
        Assert.DoesNotContain("## Could not read", markdown);
    }

    [Fact]
    public void Render_UnavailableScoreShowsNoNumericClaim()
    {
        var markdown = Render(new FakeTweak(
            "unreadable-tweak", applied: () => throw new InvalidOperationException("Read denied")));

        Assert.Contains("- Score: **--/100** (Unavailable)", markdown);
    }

    private static string Render(params FakeTweak[] tweaks)
    {
        var report = new ScoreService().Compute(tweaks, _ => { });
        return ReportService.Render(
            report,
            new SystemSummary("Test CPU", "Test GPU", "16 GB", "Windows 11", "22631", true),
            rebootPending: false,
            generatedAt: Generated);
    }
}

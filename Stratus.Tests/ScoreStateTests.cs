using Stratus.Core.Models;
using Stratus.Core.Services;
using Xunit;

namespace Stratus.Tests;

public class ScoreStateTests
{
    [Fact]
    public void Compute_FailedReadDoesNotPresentACompleteScore()
    {
        var warnings = new List<string>();
        var report = new ScoreService().Compute(
            new[] { new FakeTweak("unreadable", applied: () => throw new InvalidOperationException("Read denied")) },
            warnings.Add);

        Assert.Equal("Unavailable", report.StatusText);
        Assert.Equal(1, report.UnknownCount);
        Assert.Equal(0, report.TotalCount);
        Assert.Contains("Read denied", Assert.Single(report.States).ReadError);
        Assert.Contains(warnings, w => w.Contains("unreadable"));
    }

    [Fact]
    public void Compute_FailedApplicabilityIsUnknownRatherThanUnsupported()
    {
        var warnings = new List<string>();
        var tweak = new FakeTweak("unreadable", applies: () => throw new InvalidOperationException("Probe failed"));
        var report = new ScoreService().Compute(new[] { tweak }, warnings.Add);

        Assert.Equal("Unavailable", report.StatusText);
        Assert.Contains("Probe failed", Assert.Single(report.States).ReadError);
        Assert.Contains(warnings, w => w.Contains("unreadable"));
    }

    [Fact]
    public void Compute_MixedReadResultsKeepKnownScoreButMarkItIncomplete()
    {
        var report = new ScoreService().Compute(
            new Tweak[]
            {
                new FakeTweak("unreadable", applied: () => throw new InvalidOperationException("Read denied")),
                new FakeTweak("applied", applied: () => true),
            },
            _ => { });

        Assert.Equal("Incomplete", report.StatusText);
        Assert.Equal(100, report.Score);
        Assert.Equal(1, report.TotalCount);
        Assert.Equal(1, report.AppliedCount);
        Assert.Equal(1, report.UnknownCount);
    }

    [Fact]
    public void Compute_UnsupportedTweakDoesNotReadAppliedState()
    {
        var tweak = new FakeTweak("unsupported", applies: () => false);

        var report = new ScoreService().Compute(new[] { tweak });

        Assert.Equal(0, report.UnknownCount);
        Assert.Equal(0, report.TotalCount);
        var state = Assert.Single(report.States);
        Assert.False(state.IsApplicable);
        Assert.Null(state.ReadError);
    }

    [Fact]
    public void Compute_KnownAppliedStateKeepsTheCompleteScore()
    {
        var report = new ScoreService().Compute(new[] { new FakeTweak("applied", applied: () => true) });

        Assert.Equal(100, report.Score);
        Assert.Equal("Excellent", report.StatusText);
        Assert.Equal(0, report.UnknownCount);
        Assert.True(Assert.Single(report.States).IsApplied);
    }

    [Fact]
    public void Compute_NoApplicableTweakLeavesTheScoreUnavailable()
    {
        var report = new ScoreService().Compute(new[]
        {
            new FakeTweak("unsupported-a", applies: () => false),
            new FakeTweak("unsupported-b", applies: () => false),
        });

        Assert.Equal("Unavailable", report.StatusText);
        Assert.Equal("--", report.ScoreText);
        Assert.Equal(0, report.TotalCount);
        Assert.Equal(0, report.UnknownCount);
    }

    [Fact]
    public void Compute_EmptyCatalogIsUnavailableToo()
    {
        var report = new ScoreService().Compute(Array.Empty<Tweak>());

        Assert.Equal("Unavailable", report.StatusText);
        Assert.Equal("--", report.ScoreText);
    }

    [Fact]
    public void Compute_PartialReadKeepsANumericScore()
    {
        var report = new ScoreService().Compute(
            new Tweak[]
            {
                new FakeTweak("readable", applied: () => true),
                new FakeTweak("unreadable", applied: () => throw new InvalidOperationException("Read denied")),
            },
            _ => { });

        Assert.Equal("100", report.ScoreText);
        Assert.Equal("Incomplete", report.StatusText);
    }
}

/// <summary>Configurable test double shared by the score and report tests.</summary>
internal sealed class FakeTweak : Tweak
{
    private readonly Func<bool> _applied;

    public FakeTweak(string id, Func<bool>? applies = null, Func<bool>? applied = null,
        RiskLevel risk = RiskLevel.Recommended, int weight = 1)
        : base(id, id, "Test", TweakCategory.Gaming)
    {
        Risk = risk;
        Weight = weight;
        Applicability = applies;
        _applied = applied ?? (() => false);
    }

    public override bool IsApplied => _applied();
    public override TweakResult Apply() => throw new NotSupportedException();
    public override TweakResult Revert() => throw new NotSupportedException();
}

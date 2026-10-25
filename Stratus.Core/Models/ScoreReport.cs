namespace Stratus.Core.Models;

public sealed record CategoryScore(TweakCategory Category, int Score, int AppliedWeight, int TotalWeight)
{
    public int AppliedCount { get; init; }
    public int TotalCount { get; init; }
}

/// <summary>Per-tweak live state, evaluated exactly once per engine snapshot.</summary>
public sealed record TweakState(Tweak Tweak, bool IsApplicable, bool IsApplied);

public sealed record ScoreReport(int Score, int TotalWeight, int AppliedWeight, IReadOnlyList<CategoryScore> Categories)
{
    public int AppliedCount { get; init; }
    public int TotalCount { get; init; }

    /// <summary>Snapshot of every tweak's applicability/applied state behind this report.</summary>
    public IReadOnlyList<TweakState> States { get; init; } = Array.Empty<TweakState>();

    public string StatusText => Score switch
    {
        >= 85 => "Excellent",
        >= 70 => "Good",
        >= 50 => "Fair",
        >= 25 => "Needs work",
        _ => "Not optimized"
    };
}

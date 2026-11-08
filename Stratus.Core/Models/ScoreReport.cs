namespace Stratus.Core.Models;

public sealed record CategoryScore(TweakCategory Category, int Score, int AppliedWeight, int TotalWeight)
{
    public int AppliedCount { get; init; }
    public int TotalCount { get; init; }
}

/// <summary>Per-tweak live state, evaluated exactly once per engine snapshot.</summary>
public sealed record TweakState(Tweak Tweak, bool IsApplicable, bool IsApplied, string? ReadError = null);

public sealed record ScoreReport(int Score, int TotalWeight, int AppliedWeight, IReadOnlyList<CategoryScore> Categories)
{
    public int AppliedCount { get; init; }
    public int TotalCount { get; init; }

    /// <summary>Snapshot of every tweak's applicability/applied state behind this report.</summary>
    public IReadOnlyList<TweakState> States { get; init; } = Array.Empty<TweakState>();

    public int UnknownCount => States.Count(state => state.ReadError is not null);

    /// <summary>"--" when no applicable tweak state could be read; the numeric score otherwise.</summary>
    public string ScoreText => TotalCount == 0 ? "--" : Score.ToString();

    public string StatusText => TotalCount == 0
        ? "Unavailable"
        : UnknownCount > 0
            ? "Incomplete"
            : Score switch
    {
        >= 85 => "Excellent",
        >= 70 => "Good",
        >= 50 => "Fair",
        >= 25 => "Needs work",
        _ => "Not optimized"
    };
}

namespace Stratus.Core.Models;

public sealed record CategoryScore(TweakCategory Category, int Score, int AppliedWeight, int TotalWeight)
{
    public int AppliedCount { get; init; }
    public int TotalCount { get; init; }
}

public sealed record ScoreReport(int Score, int TotalWeight, int AppliedWeight, IReadOnlyList<CategoryScore> Categories)
{
    public int AppliedCount { get; init; }
    public int TotalCount { get; init; }

    public string StatusText => Score switch
    {
        >= 85 => "Excellent",
        >= 70 => "Good",
        >= 50 => "Fair",
        >= 25 => "Needs work",
        _ => "Not optimized"
    };
}

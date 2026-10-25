using Stratus.Core.Models;

namespace Stratus.Core.Services;

/// <summary>
/// Score = sum of weights of applied tweaks / sum of weights of all applicable tweaks, x100.
/// Computed from live system state, so already-tweaked machines score high immediately.
/// </summary>
public sealed class ScoreService
{
    public ScoreReport Compute(IReadOnlyList<Tweak> tweaks)
    {
        var applicable = tweaks.Where(t => t.IsApplicable).ToList();

        var categories = applicable
            .GroupBy(t => t.Category)
            .Select(g =>
            {
                var total = g.Sum(t => t.Weight);
                var applied = g.Where(t => SafeIsApplied(t)).Sum(t => t.Weight);
                return new CategoryScore(
                    g.Key,
                    total == 0 ? 100 : (int)Math.Round(applied * 100.0 / total),
                    applied,
                    total)
                {
                    AppliedCount = g.Count(t => SafeIsApplied(t)),
                    TotalCount = g.Count()
                };
            })
            .OrderBy(c => (int)c.Category)
            .ToList();

        var totalWeight = applicable.Sum(t => t.Weight);
        var appliedWeight = applicable.Where(t => SafeIsApplied(t)).Sum(t => t.Weight);

        return new ScoreReport(
            totalWeight == 0 ? 100 : (int)Math.Round(appliedWeight * 100.0 / totalWeight),
            totalWeight,
            appliedWeight,
            categories)
        {
            AppliedCount = applicable.Count(t => SafeIsApplied(t)),
            TotalCount = applicable.Count
        };
    }

    private static bool SafeIsApplied(Tweak t)
    {
        try { return t.IsApplied; }
        catch { return false; }
    }
}

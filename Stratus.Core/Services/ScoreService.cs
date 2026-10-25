using Stratus.Core.Models;

namespace Stratus.Core.Services;

/// <summary>
/// Score = sum of weights of applied tweaks / sum of weights of all applicable tweaks, x100.
/// Computed from live system state, so already-tweaked machines score high immediately.
/// Every tweak's registry state is read exactly once per Compute; all aggregates and the
/// per-tweak States snapshot are derived from that single pass.
/// </summary>
public sealed class ScoreService
{
    public ScoreReport Compute(IReadOnlyList<Tweak> tweaks)
    {
        var states = new List<TweakState>(tweaks.Count);
        foreach (var tweak in tweaks)
        {
            bool applicable = false, applied = false;
            try { applicable = tweak.IsApplicable; } catch { }
            if (applicable)
            {
                try { applied = tweak.IsApplied; } catch { }
            }
            states.Add(new TweakState(tweak, applicable, applied));
        }

        var applicableStates = states.Where(s => s.IsApplicable).ToList();

        var categories = applicableStates
            .GroupBy(s => s.Tweak.Category)
            .Select(g =>
            {
                var total = g.Sum(s => s.Tweak.Weight);
                var applied = g.Where(s => s.IsApplied).Sum(s => s.Tweak.Weight);
                return new CategoryScore(
                    g.Key,
                    total == 0 ? 100 : (int)Math.Round(applied * 100.0 / total),
                    applied,
                    total)
                {
                    AppliedCount = g.Count(s => s.IsApplied),
                    TotalCount = g.Count()
                };
            })
            .OrderBy(c => (int)c.Category)
            .ToList();

        var totalWeight = applicableStates.Sum(s => s.Tweak.Weight);
        var appliedWeight = applicableStates.Where(s => s.IsApplied).Sum(s => s.Tweak.Weight);

        return new ScoreReport(
            totalWeight == 0 ? 100 : (int)Math.Round(appliedWeight * 100.0 / totalWeight),
            totalWeight,
            appliedWeight,
            categories)
        {
            AppliedCount = applicableStates.Count(s => s.IsApplied),
            TotalCount = applicableStates.Count,
            States = states
        };
    }
}

using Stratus.Core.Models;

namespace Stratus.Core.Services;

/// <summary>
/// Unreadable tweaks are excluded from the score and retained as unknown in the snapshot,
/// so a failed probe cannot be presented as a complete score or a pending change.
/// </summary>
public sealed class ScoreService
{
    public ScoreReport Compute(IReadOnlyList<Tweak> tweaks) => Compute(tweaks, Log.Warn);

    /// <summary>
    /// Optional warning sink lets tests capture read failures locally instead of writing
    /// to the product log. Pass null to silence, or use the single-argument overload.
    /// </summary>
    public ScoreReport Compute(IReadOnlyList<Tweak> tweaks, Action<string>? warn)
    {
        var states = new List<TweakState>(tweaks.Count);
        foreach (var tweak in tweaks)
        {
            bool applicable = false, applied = false;
            string? readError = null;
            try { applicable = tweak.IsApplicable; }
            catch (Exception ex)
            {
                readError = "Could not check whether this tweak applies here: " + ex.Message;
                warn?.Invoke($"Could not check whether {tweak.Id} applies here: {ex.Message}");
            }
            if (applicable)
            {
                try { applied = tweak.IsApplied; }
                catch (Exception ex)
                {
                    readError = "Could not read this tweak's state: " + ex.Message;
                    warn?.Invoke($"Could not read the state of {tweak.Id}: {ex.Message}");
                }
            }
            states.Add(new TweakState(tweak, applicable, applied, readError));
        }

        var applicableStates = states.Where(s => s.IsApplicable && s.ReadError is null).ToList();

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

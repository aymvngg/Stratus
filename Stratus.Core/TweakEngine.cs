using Stratus.Core.Models;
using Stratus.Core.Services;

namespace Stratus.Core;

/// <summary>Façade over the catalog: safe apply/revert, live rescoring, change notifications.</summary>
public sealed class TweakEngine
{
    private readonly ScoreService _score = new();

    public TweakEngine(IReadOnlyList<Tweak> tweaks) => Tweaks = tweaks;

    public IReadOnlyList<Tweak> Tweaks { get; }

    /// <summary>Raised on the background thread after any successful apply/revert.</summary>
    public event Action? StateChanged;

    public ScoreReport Rescore() => _score.Compute(Tweaks);

    public Tweak? Find(string id) => Tweaks.FirstOrDefault(t => t.Id == id);

    public Task<TweakResult> ApplyAsync(Tweak tweak) => Task.Run(() =>
    {
        var result = tweak.Apply();
        if (result.Success)
            StateChanged?.Invoke();
        return result;
    });

    public Task<TweakResult> RevertAsync(Tweak tweak) => Task.Run(() =>
    {
        var result = tweak.Revert();
        if (result.Success)
            StateChanged?.Invoke();
        return result;
    });

    /// <summary>Applies every applicable, not-yet-applied tweak at or below the given risk level. Returns per-tweak results.</summary>
    public async Task<Dictionary<string, TweakResult>> ApplyBulkAsync(RiskLevel maxRisk)
    {
        var results = new Dictionary<string, TweakResult>();
        foreach (var tweak in Tweaks.Where(t => t.IsApplicable && t.Risk <= maxRisk))
        {
            bool applied;
            try { applied = tweak.IsApplied; } catch { applied = false; }
            if (applied)
                continue;

            results[tweak.Id] = await ApplyAsync(tweak);
        }
        return results;
    }

    /// <summary>Applies the given tweaks by id (skips unknown, not-applicable and already-applied). Returns per-tweak results for those attempted.</summary>
    public async Task<Dictionary<string, TweakResult>> ApplyIdsAsync(IEnumerable<string> ids)
    {
        var results = new Dictionary<string, TweakResult>();
        foreach (var id in ids)
        {
            var tweak = Find(id);
            if (tweak is null || !tweak.IsApplicable)
                continue;

            bool applied;
            try { applied = tweak.IsApplied; } catch { applied = false; }
            if (applied)
                continue;

            results[id] = await ApplyAsync(tweak);
        }
        return results;
    }
}

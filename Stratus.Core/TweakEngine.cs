using Stratus.Core.Models;
using Stratus.Core.Services;

namespace Stratus.Core;

/// <summary>Façade over the catalog: safe apply/revert, live rescoring, change notifications.</summary>
public sealed class TweakEngine
{
    private readonly ScoreService _score = new();
    private readonly HistoryStore? _history;

    public TweakEngine(IReadOnlyList<Tweak> tweaks, HistoryStore? history = null)
    {
        Tweaks = tweaks;
        _history = history;
    }

    public IReadOnlyList<Tweak> Tweaks { get; }

    /// <summary>Raised on the background thread after any successful apply/revert (once per batch).</summary>
    public event Action? StateChanged;

    private int _bulkDepth;
    private bool _stateDirty;

    /// <summary>Wrap a run of apply/reverts so listeners rescore once at the end instead of per tweak.</summary>
    public IDisposable BeginBulk() => new BulkScope(this);

    private sealed class BulkScope : IDisposable
    {
        private readonly TweakEngine _engine;

        public BulkScope(TweakEngine engine)
        {
            _engine = engine;
            Interlocked.Increment(ref _engine._bulkDepth);
        }

        public void Dispose()
        {
            if (Interlocked.Decrement(ref _engine._bulkDepth) == 0 && _engine._stateDirty)
            {
                _engine._stateDirty = false;
                _engine.StateChanged?.Invoke();
            }
        }
    }

    private void RaiseStateChanged()
    {
        if (Volatile.Read(ref _bulkDepth) > 0)
            _stateDirty = true;
        else
            StateChanged?.Invoke();
    }

    public ScoreReport Rescore() => _score.Compute(Tweaks);

    public Tweak? Find(string id) => Tweaks.FirstOrDefault(t => t.Id == id);

    /// <summary>Appends to the change timeline. Never allowed to break a real change.</summary>
    private void Record(Tweak tweak, string action, TweakResult result)
    {
        try
        {
            _history?.Append(new HistoryEntry
            {
                TweakId = tweak.Id,
                TweakName = tweak.Name,
                Action = action,
                Success = result.Success,
                Error = result.Error
            });
        }
        catch { /* timeline is informational only */ }
    }

    /// <summary>Appends to the log. Kept next to Record so every engine change is visible in both places.</summary>
    private static void LogResult(string action, Tweak tweak, TweakResult result)
    {
        if (result.Success)
            Log.Info(action + " " + tweak.Id + ": ok");
        else
            Log.Warn(action + " " + tweak.Id + " failed: " + result.Error);
    }

    public Task<TweakResult> ApplyAsync(Tweak tweak) => Task.Run(() =>
    {
        var result = tweak.Apply();
        Record(tweak, "Applied", result);
        LogResult("Applied", tweak, result);
        if (result.Success)
            RaiseStateChanged();
        return result;
    });

    public Task<TweakResult> RevertAsync(Tweak tweak) => Task.Run(() =>
    {
        var result = tweak.Revert();
        Record(tweak, "Reverted", result);
        LogResult("Reverted", tweak, result);
        if (result.Success)
            RaiseStateChanged();
        return result;
    });

    /// <summary>Applies every applicable, not-yet-applied tweak at or below the given risk level. Returns per-tweak results.</summary>
    public async Task<Dictionary<string, TweakResult>> ApplyBulkAsync(RiskLevel maxRisk)
    {
        var results = new Dictionary<string, TweakResult>();
        using var _bulk = BeginBulk();
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
        using var _bulk = BeginBulk();
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

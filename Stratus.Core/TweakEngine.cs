using Stratus.Core.Models;
using Stratus.Core.Services;

namespace Stratus.Core;

/// <summary>Façade over the catalog: safe apply/revert, live rescoring, change notifications.</summary>
public sealed class TweakEngine
{
    private readonly ScoreService _score = new();
    private readonly HistoryStore? _history;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

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
                _engine.Announce();
            }
        }
    }

    private void Announce()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            // A listener that throws (a page closing, a rescore racing disposal) must not
            // turn a completed change into a failed one.
            Log.Error("A change listener failed after a tweak change", ex);
        }
    }

    /// <summary>Marks the engine dirty; a bulk scope raises the event once when it ends.</summary>
    private void RaiseStateChanged()
    {
        if (Volatile.Read(ref _bulkDepth) > 0)
            _stateDirty = true;
        else
            Announce();
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
        catch (Exception ex)
        {
            Log.Warn("The change timeline could not record " + tweak.Id + ": " + ex.Message);
        }
    }

    private static void LogResult(string action, Tweak tweak, TweakResult result)
    {
        if (result.Success)
            Log.Info(action + " " + tweak.Id + ": ok");
        else
            Log.Warn(action + " " + tweak.Id + " failed: " + result.Error);
    }

    /// <summary>
    /// Runs one apply/revert with the engine-wide gate held, so two conflicting operations
    /// (for example two entries that touch the same registry value) cannot interleave.
    /// The change event is raised after the gate is released, so a listener never runs
    /// an engine operation while the lock is held.
    /// </summary>
    private async Task<TweakResult> RunExclusiveAsync(Tweak tweak, bool apply)
    {
        TweakResult result;
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            result = await RunAsync(tweak, apply).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }

        if (result.Success)
            RaiseStateChanged();

        return result;
    }

    private Task<TweakResult> RunAsync(Tweak tweak, bool apply) => Task.Run(() =>
    {
        TweakResult result;
        try
        {
            result = apply ? tweak.Apply() : tweak.Revert();
        }
        catch (Exception ex)
        {
            Log.Error((apply ? "Applying " : "Reverting ") + tweak.Id + " threw", ex);
            result = TweakResult.Fail(ex.Message);
        }

        Record(tweak, apply ? "Applied" : "Reverted", result);
        LogResult(apply ? "Applied" : "Reverted", tweak, result);
        return result;
    });

    public Task<TweakResult> ApplyAsync(Tweak tweak) => RunExclusiveAsync(tweak, apply: true);

    public Task<TweakResult> RevertAsync(Tweak tweak) => RunExclusiveAsync(tweak, apply: false);

    /// <summary>Applies every applicable, not-yet-applied tweak at or below the given risk level. Returns per-tweak results.</summary>
    public async Task<Dictionary<string, TweakResult>> ApplyBulkAsync(RiskLevel maxRisk)
    {
        var results = new Dictionary<string, TweakResult>();
        using var _bulk = BeginBulk();
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var tweak in Tweaks.Where(t => t.IsApplicable && t.Risk <= maxRisk))
            {
                bool applied;
                try { applied = tweak.IsApplied; } catch { applied = false; }
                if (applied)
                    continue;

                var result = await RunAsync(tweak, apply: true).ConfigureAwait(false);
                results[tweak.Id] = result;
                if (result.Success)
                    RaiseStateChanged();
            }
        }
        finally
        {
            _operationGate.Release();
        }
        return results;
    }

    /// <summary>Applies the given tweaks by id (skips unknown, not-applicable and already-applied). Returns per-tweak results for those attempted.</summary>
    public async Task<Dictionary<string, TweakResult>> ApplyIdsAsync(IEnumerable<string> ids)
    {
        var results = new Dictionary<string, TweakResult>();
        using var _bulk = BeginBulk();
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var id in ids)
            {
                var tweak = Find(id);
                if (tweak is null || !tweak.IsApplicable)
                    continue;

                bool applied;
                try { applied = tweak.IsApplied; } catch { applied = false; }
                if (applied)
                    continue;

                var result = await RunAsync(tweak, apply: true).ConfigureAwait(false);
                results[id] = result;
                if (result.Success)
                    RaiseStateChanged();
            }
        }
        finally
        {
            _operationGate.Release();
        }
        return results;
    }
}

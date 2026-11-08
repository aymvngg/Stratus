using Stratus.Core;
using Stratus.Core.Models;
using Xunit;

namespace Stratus.Tests;

/// <summary>
/// The engine contract the bulk UI outcomes depend on: results name only the tweaks that
/// were attempted, so a UI message can report honestly what ran. An id that is already
/// applied or no longer applicable when the run starts is skipped silently. No engine code
/// is changed here; these tests pin the existing behavior.
/// </summary>
public class EngineBulkResultTests
{
    private int _stateChangedCount;

    private TweakEngine Engine(params Tweak[] tweaks)
    {
        var engine = new TweakEngine(tweaks);
        engine.StateChanged += () => Interlocked.Increment(ref _stateChangedCount);
        return engine;
    }

    [Fact]
    public async Task ApplyIds_AttemptsOnlyPendingIds()
    {
        var applied = new BulkTweak("already-on");
        applied.Apply();
        var pending = new BulkTweak("pending");
        var engine = Engine(applied, pending);

        var results = await engine.ApplyIdsAsync(["already-on", "pending", "unknown-id"]);

        Assert.Equal(["pending"], results.Keys);
        Assert.Equal(1, pending.Applies);
        Assert.Equal(1, applied.Applies);
    }

    [Fact]
    public async Task ApplyIds_AllSkipped_ReturnsEmptyResults()
    {
        var applied = new BulkTweak("already-on");
        applied.Apply();
        var engine = Engine(applied);

        var results = await engine.ApplyIdsAsync(["already-on"]);

        Assert.Empty(results);
    }

    [Fact]
    public async Task ApplyIds_DuplicateIdIsAttemptedOnce()
    {
        var pending = new BulkTweak("once-only");
        var engine = Engine(pending);

        var results = await engine.ApplyIdsAsync(["once-only", "once-only"]);

        Assert.Equal(["once-only"], results.Keys);
        Assert.Equal(1, pending.Applies);
    }

    [Fact]
    public async Task ApplyIds_BulkRaiseOneStateChangedForTheWholeRun()
    {
        var engine = Engine(new BulkTweak("a"), new BulkTweak("b"), new BulkTweak("c"));

        var results = await engine.ApplyIdsAsync(["a", "b", "c"]);

        Assert.Equal(3, results.Count);
        Assert.Equal(1, _stateChangedCount);
    }

    [Fact]
    public async Task ApplyIds_NoSuccessRaisesNoStateChanged()
    {
        var engine = Engine(new BulkTweak("broken", fail: true));

        var results = await engine.ApplyIdsAsync(["broken"]);

        Assert.Single(results);
        Assert.False(results["broken"].Success);
        Assert.Equal(0, _stateChangedCount);
    }

    [Fact]
    public async Task SingleApply_RaisesStateChangedOnceOnSuccess()
    {
        var engine = Engine(new BulkTweak("single"));

        var result = await engine.ApplyAsync(engine.Find("single")!);

        Assert.True(result.Success);
        Assert.Equal(1, _stateChangedCount);
    }
}

/// <summary>Stateful apply double: IsApplied flips on a successful apply, so skip behavior is real.</summary>
internal sealed class BulkTweak : Tweak
{
    private readonly bool _fail;
    private bool _applied;

    public BulkTweak(string id, bool fail = false)
        : base(id, id, "Test tweak", TweakCategory.Gaming)
    {
        _fail = fail;
    }

    public int Applies { get; private set; }

    public override bool IsApplied => _applied;

    public override TweakResult Apply()
    {
        Applies++;
        if (_fail)
            return TweakResult.Fail("injected failure");

        _applied = true;
        return TweakResult.Ok;
    }

    public override TweakResult Revert()
    {
        _applied = false;
        return TweakResult.Ok;
    }
}

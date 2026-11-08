using Stratus.Core.Coordination;
using Xunit;

namespace Stratus.Tests;

public sealed class UiCoordinationTests
{
    [Fact]
    public void A_snapshot_stays_current_until_a_later_change()
    {
        var revision = new ChangeRevision();
        var snapshot = revision.Current;

        Assert.True(revision.IsCurrent(snapshot));

        revision.Advance();

        Assert.False(revision.IsCurrent(snapshot));
        Assert.True(revision.IsCurrent(revision.Current));
    }

    [Fact]
    public void Every_advance_moves_the_revision_forward()
    {
        var revision = new ChangeRevision();

        var first = revision.Advance();
        var second = revision.Advance();

        Assert.True(second > first);
        Assert.Equal(second, revision.Current);
    }

    [Fact]
    public void Concurrent_advances_are_not_lost()
    {
        var revision = new ChangeRevision();

        Parallel.For(0, 1_000, _ => revision.Advance());

        Assert.Equal(1_000, revision.Current);
    }

    [Fact]
    public void Only_one_operation_can_hold_the_gate()
    {
        var gate = new OperationGate();

        Assert.True(gate.TryEnter());
        Assert.True(gate.IsActive);
        Assert.False(gate.TryEnter());

        gate.Exit();

        Assert.False(gate.IsActive);
        Assert.True(gate.TryEnter());
    }

    [Fact]
    public void A_missing_cleanup_selection_keeps_defaults()
    {
        Assert.Null(CleanupSelection.Parse(null));
    }

    [Fact]
    public void An_explicit_empty_cleanup_selection_stays_empty()
    {
        var selection = CleanupSelection.Parse("");
        var blank = CleanupSelection.Parse("  ,  ");

        Assert.NotNull(selection);
        Assert.Empty(selection);
        Assert.NotNull(blank);
        Assert.Empty(blank);
    }

    [Fact]
    public void Cleanup_ids_round_trip_through_the_setting()
    {
        var selection = CleanupSelection.Parse(" user-temp , windows-temp ,,");

        Assert.NotNull(selection);
        Assert.Equal(2, selection.Count);
        Assert.Contains("user-temp", selection);
        Assert.Contains("windows-temp", selection);
        Assert.Equal("user-temp,windows-temp", CleanupSelection.Format(selection));
    }

    [Fact]
    public void A_clean_run_is_a_success()
    {
        Assert.Equal(OutcomeSeverity.Success, CleanupSummary.SeverityFor(null));
        Assert.Equal(OutcomeSeverity.Success, CleanupSummary.SeverityFor(Array.Empty<string>()));
        Assert.Equal("", CleanupSummary.RestartFailureText(null));
        Assert.Equal("", CleanupSummary.RestartFailureText(Array.Empty<string>()));
    }

    [Fact]
    public void A_service_that_did_not_restart_makes_the_clean_a_warning()
    {
        var failures = new[] { "dosvc" };

        Assert.Equal(OutcomeSeverity.Warning, CleanupSummary.SeverityFor(failures));
        var text = CleanupSummary.RestartFailureText(failures);
        Assert.Contains("dosvc", text);
        Assert.Contains("did not restart", text);
    }

    [Fact]
    public void Every_failed_service_is_named()
    {
        var text = CleanupSummary.RestartFailureText(new[] { "dosvc", "wuauserv" });

        Assert.Contains("dosvc", text);
        Assert.Contains("wuauserv", text);
    }

    [Fact]
    public void A_recycle_bin_or_target_failure_makes_the_clean_a_warning()
    {
        Assert.Equal(OutcomeSeverity.Warning, CleanupSummary.SeverityFor(null, recycleBinFailed: true, failedTargets: 0));
        Assert.Equal(OutcomeSeverity.Warning, CleanupSummary.SeverityFor(null, recycleBinFailed: false, failedTargets: 1));
        Assert.Equal(OutcomeSeverity.Warning, CleanupSummary.SeverityFor(new[] { "dosvc" }, recycleBinFailed: false, failedTargets: 0));
        Assert.Equal(OutcomeSeverity.Success, CleanupSummary.SeverityFor(null, recycleBinFailed: false, failedTargets: 0));
    }

    [Fact]
    public void Partial_outcome_text_names_what_failed()
    {
        Assert.Contains("recycle bin", CleanupSummary.RecycleBinFailedText(true), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", CleanupSummary.RecycleBinFailedText(false));

        Assert.Contains("1", CleanupSummary.FailedTargetsText(1));
        Assert.Contains("2", CleanupSummary.FailedTargetsText(2));
        Assert.Equal("", CleanupSummary.FailedTargetsText(0));
    }
}

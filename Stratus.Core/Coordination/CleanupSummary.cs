namespace Stratus.Core.Coordination;

/// <summary>
/// Turns the cleanup result flags into the UI outcome. Kept here so the rule is testable:
/// a cleanup that could not restart a service, could not empty the recycle bin, or could not
/// clean a selected target is a partial result, never a silent success.
/// </summary>
public static class CleanupSummary
{
    public static OutcomeSeverity SeverityFor(IReadOnlyList<string>? restartFailures) =>
        SeverityFor(restartFailures, recycleBinFailed: false, failedTargets: 0);

    public static OutcomeSeverity SeverityFor(
        IReadOnlyList<string>? restartFailures, bool recycleBinFailed, int failedTargets) =>
        restartFailures is { Count: > 0 } || recycleBinFailed || failedTargets > 0
            ? OutcomeSeverity.Warning
            : OutcomeSeverity.Success;

    /// <summary>Sentence naming the services that did not restart, or empty when all came back.</summary>
    public static string RestartFailureText(IReadOnlyList<string>? restartFailures) =>
        restartFailures is { Count: > 0 }
            ? $" These services did not restart: {string.Join(", ", restartFailures)}. Start them from Services."
            : "";

    /// <summary>Sentence for a recycle bin that could not be emptied, or empty when it was.</summary>
    public static string RecycleBinFailedText(bool recycleBinFailed) =>
        recycleBinFailed ? " The recycle bin could not be emptied." : "";

    /// <summary>Sentence naming how many selected targets could not be cleaned, or empty when none.</summary>
    public static string FailedTargetsText(int failedTargets) =>
        failedTargets <= 0 ? ""
        : failedTargets == 1 ? " 1 target could not be cleaned."
        : $" {failedTargets} targets could not be cleaned.";
}

namespace Stratus.Core.Coordination;

/// <summary>
/// Severity of a user-visible result, independent of WinUI. The UI maps this to
/// InfoBarSeverity so status does not depend on parsing English message prefixes.
/// </summary>
public enum OutcomeSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

/// <summary>One finished UI action: the message to show and how it went.</summary>
public readonly record struct OperationOutcome(string Message, OutcomeSeverity Severity)
{
    public static OperationOutcome Info(string message) => new(message, OutcomeSeverity.Informational);
    public static OperationOutcome Success(string message) => new(message, OutcomeSeverity.Success);
    public static OperationOutcome Warning(string message) => new(message, OutcomeSeverity.Warning);
    public static OperationOutcome Error(string message) => new(message, OutcomeSeverity.Error);
}

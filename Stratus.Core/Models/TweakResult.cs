namespace Stratus.Core.Models;

public sealed record TweakResult(bool Success, string? Error = null)
{
    public static TweakResult Ok { get; } = new(true);
    public static TweakResult Fail(string error) => new(false, error);
}

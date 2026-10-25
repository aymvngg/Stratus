namespace Stratus.Core.Models;

/// <summary>One applied or reverted change, kept as an append-only timeline so the user can undo.</summary>
public sealed class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string TweakId { get; set; } = "";
    public string TweakName { get; set; } = "";
    public string Action { get; set; } = "";
    public bool Success { get; set; }
    public string? Error { get; set; }
}

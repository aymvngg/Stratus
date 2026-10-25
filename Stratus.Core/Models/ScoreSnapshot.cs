namespace Stratus.Core.Models;

/// <summary>One point on the Dashboard score graph. Points are stored oldest first.</summary>
public sealed record ScoreSnapshot(DateTime TimestampUtc, int Score);

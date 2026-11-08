namespace Stratus.Core.Coordination;

/// <summary>
/// Monotonic revision of a UI-owned change. Scans capture the revision when they start
/// and discard their snapshot when a newer change landed while they were reading.
/// </summary>
public sealed class ChangeRevision
{
    private long _current;

    public long Current => Interlocked.Read(ref _current);

    /// <summary>Marks a change and returns the new revision.</summary>
    public long Advance() => Interlocked.Increment(ref _current);

    public bool IsCurrent(long snapshot) => Volatile.Read(ref _current) == snapshot;
}

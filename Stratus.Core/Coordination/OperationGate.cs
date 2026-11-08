namespace Stratus.Core.Coordination;

/// <summary>
/// Single-owner gate for operations that must not overlap. Used where a second
/// operation would otherwise clear the busy state of the first one.
/// </summary>
public sealed class OperationGate
{
    private int _active;

    public bool IsActive => Volatile.Read(ref _active) != 0;

    public bool TryEnter() => Interlocked.CompareExchange(ref _active, 1, 0) == 0;

    public void Exit() => Interlocked.Exchange(ref _active, 0);
}

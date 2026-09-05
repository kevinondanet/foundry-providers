namespace InspectAzureAI.Eval.Concurrency;

/// <summary>
/// One held slot of a limiter or semaphore — the port of the <c>async with</c> exit. <see cref="Release"/> (or
/// disposing, synchronously or asynchronously) returns the slot exactly once; later calls are no-ops, so a
/// cancelled or faulted holder can release from a <c>finally</c> without double-counting.
/// </summary>
public sealed class ConcurrencyLease : IDisposable, IAsyncDisposable
{
    private Action? _release;

    internal ConcurrencyLease(Action release)
    {
        _release = release;
    }

    /// <summary>True once the slot has been returned.</summary>
    public bool Released => Volatile.Read(ref _release) is null;

    /// <summary>Returns the slot (idempotent).</summary>
    public void Release() => Interlocked.Exchange(ref _release, null)?.Invoke();

    public void Dispose() => Release();

    public ValueTask DisposeAsync()
    {
        Release();
        return ValueTask.CompletedTask;
    }
}

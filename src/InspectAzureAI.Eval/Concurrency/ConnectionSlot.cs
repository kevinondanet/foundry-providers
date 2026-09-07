namespace InspectAzureAI.Eval.Concurrency;

/// <summary>
/// Port of <c>model/_model.py</c> <c>ConnectionSlot</c>: a held connection-pool slot that can be relinquished
/// mid-call and reacquired (Python's hard-pause gate does this so a parked generate doesn't pin a slot). The
/// held flag makes the owning context's final release safe under any interleaving: a cancellation during a
/// reacquire simply leaves the slot un-held and the final release no-ops.
/// </summary>
public sealed class ConnectionSlot : IAsyncDisposable
{
    private ConcurrencyLease? _lease;

    public ConnectionSlot(IConcurrencySemaphore semaphore)
    {
        ArgumentNullException.ThrowIfNull(semaphore);
        Semaphore = semaphore;
    }

    /// <summary>The pool this slot belongs to.</summary>
    public IConcurrencySemaphore Semaphore { get; }

    /// <summary>The pool's adaptive controller, when the pool is adaptive.</summary>
    public AdaptiveConcurrencyController? Controller => Semaphore as AdaptiveConcurrencyController;

    public bool Held => Volatile.Read(ref _lease) is not null;

    /// <summary>Acquires and holds a slot for the caller to release (or dispose).</summary>
    public static async ValueTask<ConnectionSlot> HoldAsync(IConcurrencySemaphore semaphore, CancellationToken cancellationToken = default)
    {
        var slot = new ConnectionSlot(semaphore);
        await slot.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return slot;
    }

    /// <summary>Acquires the slot unless already held.</summary>
    public async ValueTask AcquireAsync(CancellationToken cancellationToken = default)
    {
        if (Held)
        {
            return;
        }

        var lease = await Semaphore.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (Interlocked.CompareExchange(ref _lease, lease, null) is not null)
        {
            lease.Release();
        }
    }

    /// <summary>Reacquires after a release (the same operation; kept for parity with Python's gate protocol).</summary>
    public ValueTask ReacquireAsync(CancellationToken cancellationToken = default) => AcquireAsync(cancellationToken);

    /// <summary>Releases the slot if held (no-op otherwise).</summary>
    public ValueTask ReleaseAsync()
    {
        Interlocked.Exchange(ref _lease, null)?.Release();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ReleaseAsync();
}

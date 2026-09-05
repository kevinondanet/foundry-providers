namespace InspectAzureAI.Eval.Concurrency;

/// <summary>
/// Port of <c>util/_concurrency.py</c> <c>ResizableLimiter</c>: a counting limiter whose <see cref="Limit"/> can
/// change live. Lowering it below the in-use count blocks new acquires until enough holders release — it never
/// preempts an in-flight holder; raising it grants waiting acquirers at once. Acquires are granted in FIFO order
/// and one caller may hold several slots (nested acquires each take one), preserving the counting semantics of
/// the semaphore this replaces. Python's <c>anyio.CapacityLimiter</c> lives on one event-loop thread; here every
/// transition is under one lock because holders run on the thread pool.
/// </summary>
public sealed class ResizableLimiter : ISampleLimiter
{
    private readonly object _sync = new();

    private readonly LinkedList<Waiter> _waiters = new();

    private int _limit;

    private int _inUse;

    public ResizableLimiter(int limit)
    {
        _limit = Validated(limit);
    }

    /// <summary>Current maximum number of concurrent holders; setting it is the live retune.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is below 1 (a zero limit would wedge every acquirer).</exception>
    public int Limit
    {
        get
        {
            lock (_sync)
            {
                return _limit;
            }
        }

        set
        {
            Validated(value);
            lock (_sync)
            {
                _limit = value;
                Grant();
            }
        }
    }

    /// <summary>Holders currently inside the limiter (may exceed <see cref="Limit"/> while a shrink drains).</summary>
    public int InUse
    {
        get
        {
            lock (_sync)
            {
                return _inUse;
            }
        }
    }

    /// <summary>Free capacity (<c>Limit - InUse</c>), clamped to >= 0 because the limit may be lowered below the in-use count.</summary>
    public int Available
    {
        get
        {
            lock (_sync)
            {
                return Math.Max(0, _limit - _inUse);
            }
        }
    }

    /// <summary>Acquirers currently blocked waiting for a slot.</summary>
    public int Waiting
    {
        get
        {
            lock (_sync)
            {
                return _waiters.Count;
            }
        }
    }

    /// <summary>Takes a slot, waiting while the limiter is full; cancelling a pending wait leaves the queue without consuming a slot.</summary>
    public ValueTask<ConcurrencyLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<ConcurrencyLease>(cancellationToken);
        }

        Waiter waiter;
        lock (_sync)
        {
            if (_waiters.Count == 0 && _inUse < _limit)
            {
                _inUse++;
                return new ValueTask<ConcurrencyLease>(NewLease());
            }

            waiter = new Waiter(this);
            waiter.Node = _waiters.AddLast(waiter);
            if (cancellationToken.CanBeCanceled)
            {
                waiter.Registration = cancellationToken.Register(static (state, token) => ((Waiter)state!).Cancel(token), waiter);
            }
        }

        return new ValueTask<ConcurrencyLease>(waiter.Source.Task);
    }

    private static int Validated(int limit) =>
        limit >= 1 ? limit : throw new ArgumentOutOfRangeException(nameof(limit), limit, $"ResizableLimiter limit must be >= 1 (got {limit})");

    private ConcurrencyLease NewLease() => new(ReleaseSlot);

    private void ReleaseSlot()
    {
        lock (_sync)
        {
            _inUse--;
            Grant();
        }
    }

    // Under the lock. Completing the source is safe here: continuations run asynchronously, never inline.
    private void Grant()
    {
        while (_inUse < _limit && _waiters.First is { } node)
        {
            _waiters.RemoveFirst();
            var waiter = node.Value;
            waiter.Node = null;
            // Unregister (not Dispose) — Dispose would wait for a cancel callback that is itself waiting for this lock.
            waiter.Registration.Unregister();
            _inUse++;
            if (!waiter.Source.TrySetResult(NewLease()))
            {
                _inUse--;
            }
        }
    }

    private void CancelWaiter(Waiter waiter, CancellationToken token)
    {
        lock (_sync)
        {
            if (waiter.Node is not { List: not null } node)
            {
                return;
            }

            _waiters.Remove(node);
            waiter.Node = null;
        }

        waiter.Source.TrySetCanceled(token);
    }

    private sealed class Waiter(ResizableLimiter owner)
    {
        public TaskCompletionSource<ConcurrencyLease> Source { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LinkedListNode<Waiter>? Node { get; set; }

        public CancellationTokenRegistration Registration { get; set; }

        public void Cancel(CancellationToken token) => owner.CancelWaiter(this, token);
    }
}

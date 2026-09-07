namespace InspectAzureAI.Eval.Concurrency;

/// <summary>
/// Port of <c>DynamicSampleLimiter</c>: a sample-concurrency limiter that follows its model's adaptive
/// controller. It starts at <c>min(start, max) + BUFFER</c> and, once the controller registered under
/// <c>key</c> exists (at construction or later, via the registry's controller-created observers), tracks
/// <c>controller.Concurrency + BUFFER</c> on every scale change. Controllers for other models (graders, sibling
/// tasks, another account) are ignored; a key that never matches leaves the limiter at its initial value.
/// <see cref="SetOverride"/> pins the capacity at an exact setpoint (scale events are ignored while pinned);
/// clearing it catches back up to the controller. Both subscriptions are process-global, so a limiter must be
/// <see cref="Dispose"/>d when its run ends (the runner does), or every later run keeps it alive and fans out to it.
/// </summary>
public sealed class DynamicSampleLimiter : ISampleLimiter, IDisposable
{
    public const int Buffer = 5;

    private readonly object _sync = new();

    private readonly string _key;

    private readonly ResizableLimiter _limiter;

    private readonly int _initial;

    private AdaptiveConcurrencyController? _controller;

    private int? _override;

    private bool _disposed;

    public DynamicSampleLimiter(AdaptiveConcurrency adaptive, string key)
    {
        ArgumentNullException.ThrowIfNull(adaptive);
        ArgumentException.ThrowIfNullOrEmpty(key);
        adaptive.Validate();
        _key = key;
        _initial = Math.Min(adaptive.Start, adaptive.Max) + Buffer;
        _limiter = new ResizableLimiter(_initial);
        var existing = Concurrency.AdaptiveControllers().FirstOrDefault(c => c.Key == key);
        if (existing is not null)
        {
            Adopt(existing);
        }

        Concurrency.AddControllerCreatedObserver(OnControllerCreated);
    }

    /// <summary>The pinned setpoint, or null when tracking the controller.</summary>
    public int? Override
    {
        get
        {
            lock (_sync)
            {
                return _override;
            }
        }
    }

    /// <summary>Current capacity (named like <see cref="ResizableLimiter.Limit"/> so both limiter shapes read uniformly).</summary>
    public int Limit => _limiter.Limit;

    public int InUse => _limiter.InUse;

    /// <summary>The adopted controller, or null if none has matched the key yet (the limiter is then parked at its initial value).</summary>
    public AdaptiveConcurrencyController? Controller
    {
        get
        {
            lock (_sync)
            {
                return _controller;
            }
        }
    }

    /// <summary>Current capacity (Python's <c>total_tokens</c>).</summary>
    public int TotalTokens => _limiter.Limit;

    public ValueTask<ConcurrencyLease> AcquireAsync(CancellationToken cancellationToken = default) => _limiter.AcquireAsync(cancellationToken);

    /// <summary>
    /// Pins the capacity at <paramref name="value"/> (lowering below in-flight blocks new acquires until holders
    /// drain), or with null resumes tracking: to the adopted controller's current limit plus the buffer, or the
    /// initial capacity when no controller has been adopted.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is below 1 (rejected without committing the pin).</exception>
    public void SetOverride(int? value)
    {
        if (value is { } pinned)
        {
            if (pinned < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), pinned, $"max_samples override must be >= 1 (got {pinned})");
            }

            lock (_sync)
            {
                _limiter.Limit = pinned;
                _override = pinned;
            }

            return;
        }

        AdaptiveConcurrencyController? controller;
        lock (_sync)
        {
            _override = null;
            controller = _controller;
            if (controller is null)
            {
                _limiter.Limit = _initial;
            }
        }

        if (controller is not null)
        {
            OnControllerChange();
        }
    }

    /// <summary>
    /// Unsubscribes from the registry's controller-created observers and from the adopted controller: a finished
    /// run's limiter is then neither kept alive by the registry nor re-adopts (and resizes for) controllers created
    /// later. Leases already held stay valid and the limiter keeps working at its last capacity. Idempotent.
    /// </summary>
    public void Dispose()
    {
        AdaptiveConcurrencyController? controller;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            controller = _controller;
        }

        Concurrency.RemoveControllerCreatedObserver(OnControllerCreated);
        controller?.RemoveObserver(OnControllerChange);
    }

    private void Adopt(AdaptiveConcurrencyController controller)
    {
        lock (_sync)
        {
            // subscribed under the lock so a Dispose racing a controller-created callback either sees the
            // adopted controller or runs after this subscription; nothing takes this lock while holding the controller's
            if (_disposed)
            {
                return;
            }

            _controller = controller;
            controller.AddObserver(OnControllerChange);
        }

        OnControllerChange();
    }

    private void OnControllerCreated(AdaptiveConcurrencyController controller)
    {
        if (controller.Key == _key)
        {
            Adopt(controller);
        }
    }

    private void OnControllerChange()
    {
        lock (_sync)
        {
            if (_controller is null || _override is not null)
            {
                return;
            }

            var target = _controller.Concurrency + Buffer;
            if (target != _limiter.Limit)
            {
                _limiter.Limit = target;
            }
        }
    }
}

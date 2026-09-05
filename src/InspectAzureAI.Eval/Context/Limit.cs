namespace InspectAzureAI.Eval.Context;

/// <summary>
/// Port of <c>util/_limit.py</c> <c>Limit</c> (with its <c>_Node</c> mixin): the base of every scoped limit.
/// A limit is created, entered with <see cref="Enter"/> (Python's <c>with</c>) and disposed to leave the scope.
/// Scopes of one kind nest into a tree whose leaf is carried by an AsyncLocal, so a scope opened in one async
/// flow is seen by its callees and awaited continuations and never by a sibling flow, and an instance may be
/// entered only once. Checks run from the root to the leaf: when several nested limits are exceeded at once
/// the outermost raises, which keeps sub-agent architectures from looping forever on an inner limit.
/// A new kind of limit (a cost limit, say) derives from this class, keeps its own
/// <see cref="LimitTree{TNode}"/>, pushes itself in <see cref="EnterCore"/> and pops in <see cref="ExitCore"/>,
/// records its usage on itself and its ancestors, and raises <see cref="LimitExceededException"/> with
/// itself as the <see cref="LimitExceededException.SourceLimit"/> after emitting a <see cref="SampleLimitEvent"/>.
/// </summary>
public abstract class Limit : IDisposable
{
    private bool _entered;

    private bool _exited;

    /// <summary>The value of the limit being applied; null represents no limit.</summary>
    public abstract double? LimitValue { get; }

    /// <summary>The current usage of the resource being limited.</summary>
    public abstract double Usage { get; }

    /// <summary>The remaining "unused" amount of the resource being limited, or null when there is no limit.</summary>
    public double? Remaining => LimitValue is { } limit ? limit - Usage : null;

    /// <summary>The enclosing scope of the same kind, or null for a root.</summary>
    public Limit? Parent { get; internal set; }

    /// <summary>True between <see cref="Enter"/> and <see cref="Dispose"/>.</summary>
    public bool Entered => _entered && !_exited;

    /// <summary>
    /// Port of <c>__enter__</c>: pushes this limit onto its tree so that it applies to the current async flow
    /// until disposed. Entering the same instance twice is an <see cref="InvalidOperationException"/>.
    /// </summary>
    public Limit Enter()
    {
        if (_entered)
        {
            throw new InvalidOperationException(
                "Each Limit may only be used once in a single 'using' block. Please create a new instance of the Limit.");
        }

        _entered = true;
        EnterCore();
        return this;
    }

    /// <summary>Port of <c>__exit__</c>: pops this limit from its tree. Idempotent; a limit never entered is a no-op.</summary>
    public void Dispose()
    {
        if (!_entered || _exited)
        {
            return;
        }

        _exited = true;
        ExitCore();
    }

    /// <summary>Port of <c>apply_limits(limits)</c> without error capture: enters every limit in order; disposing the scope leaves them in reverse order.</summary>
    public static LimitScope Apply(params Limit[] limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return new LimitScope(limits);
    }

    /// <summary>
    /// Port of <c>apply_limits(limits, catch_errors)</c> around an async body: <see cref="Apply"/> followed by
    /// <see cref="LimitScope.RunAsync"/>, returning the scope so <see cref="LimitScope.LimitError"/> can be inspected.
    /// When the body must observe the scope before it completes (an error of an outer scope passing through it, say),
    /// create the scope with <see cref="Apply"/> and call <see cref="LimitScope.RunAsync"/> on it instead.
    /// </summary>
    public static async Task<LimitScope> ApplyAsync(
        IReadOnlyList<Limit> limits,
        Func<CancellationToken, Task> body,
        bool catchErrors = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(body);
        var scope = new LimitScope(limits);
        await scope.RunAsync(body, catchErrors, cancellationToken).ConfigureAwait(false);
        return scope;
    }

    /// <summary>Pushes this limit onto its kind's tree (called once, from <see cref="Enter"/>).</summary>
    protected abstract void EnterCore();

    /// <summary>Pops this limit from its kind's tree (called once, from <see cref="Dispose"/>).</summary>
    protected abstract void ExitCore();

    /// <summary>Records the <see cref="SampleLimitEvent"/> Python emits at the point a limit trips (a no-op outside a sample).</summary>
    protected static void EmitLimitEvent(string type, double? limit, string message) =>
        SampleContext.Current?.Transcript.Add(new SampleLimitEvent(type, message, limit));
}

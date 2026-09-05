using InspectAzureAI.Eval.Context;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Agents;

/// <summary>
/// Port of the <c>limits</c> list of <c>handoff()</c>, <c>as_tool()</c> and <c>run()</c>: the
/// <c>message_limit</c>, <c>token_limit</c> and <c>time_limit</c> of <c>util/_limit.py</c> scoped to one
/// agent run (working and cost limits are not ported). A null member means unlimited.
/// </summary>
public sealed record AgentLimits(int? MessageLimit = null, int? TokenLimit = null, TimeSpan? TimeLimit = null)
{
    /// <summary>No limits (Python's empty list).</summary>
    public static AgentLimits None { get; } = new();
}

/// <summary>
/// Port of <c>apply_limits()</c> and the limit trees of <c>util/_limit.py</c>: an ambient (AsyncLocal) stack
/// of scoped limits checked cooperatively by <c>Model.GenerateAsync</c>. Token usage is recorded on every
/// enclosing scope and checked root-first, so the outermost exceeded limit wins; the message count is checked
/// against the innermost scope that has a message limit only, as in Python. The sample-level
/// <see cref="Limits"/> are checked separately by the model and are never owned by a scope.
/// </summary>
public sealed class LimitScope : IDisposable
{
    private static readonly AsyncLocal<LimitScope?> Leaf = new();

    private readonly LimitScope? _parent;

    private readonly CancellationToken _outer;

    private readonly CancellationTokenSource? _timeout;

    private readonly object _sync = new();

    private ModelUsage _usage = new();

    private bool _disposed;

    private LimitScope(AgentLimits limits, LimitScope? parent, CancellationToken outer)
    {
        Limits = limits;
        _parent = parent;
        _outer = outer;
        if (limits.TimeLimit is { } timeLimit)
        {
            _timeout = CancellationTokenSource.CreateLinkedTokenSource(outer);
            _timeout.CancelAfter(timeLimit);
            CancellationToken = _timeout.Token;
        }
        else
        {
            CancellationToken = outer;
        }
    }

    /// <summary>The innermost scope of the current async flow (null outside any scope).</summary>
    public static LimitScope? Current => Leaf.Value;

    public AgentLimits Limits { get; }

    /// <summary>The token to run the agent with: the caller's token, also cancelled when the time limit elapses.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Whether this scope's time limit elapsed (and the caller's own token did not fire).</summary>
    public bool TimedOut => _timeout is { IsCancellationRequested: true } && !_outer.IsCancellationRequested;

    /// <summary>Tokens recorded while this scope was open (including nested scopes).</summary>
    public ModelUsage Usage
    {
        get
        {
            lock (_sync)
            {
                return _usage;
            }
        }
    }

    /// <summary>
    /// Port of entering <c>apply_limits(limits)</c>: pushes a scope for the current async flow. Dispose it
    /// (in stack order) to leave. Negative limits are rejected like Python's <c>ValueError</c>.
    /// </summary>
    public static LimitScope Apply(AgentLimits? limits, CancellationToken cancellationToken = default)
    {
        var resolved = limits ?? AgentLimits.None;
        if (resolved.MessageLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), resolved.MessageLimit, "Message limit value must be a non-negative integer or None.");
        }

        if (resolved.TokenLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), resolved.TokenLimit, "Token limit value must be a non-negative integer or None.");
        }

        if (resolved.TimeLimit is { } time && time < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), resolved.TimeLimit, "Time limit value must be non-negative or None.");
        }

        var scope = new LimitScope(resolved, Leaf.Value, cancellationToken);
        Leaf.Value = scope;
        return scope;
    }

    /// <summary>Whether <paramref name="exception"/> was raised by this scope's own limits (Python's <c>source in limits</c>).</summary>
    public bool Owns(LimitExceededException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return ReferenceEquals(exception.LimitSource, this);
    }

    /// <summary>
    /// The <see cref="LimitExceededException"/> this scope is responsible for, if <paramref name="exception"/>
    /// is one: its own limit error, or a cancellation caused by its time limit. Null for anything else, including
    /// the sample's limits and other scopes' limits, which must propagate.
    /// </summary>
    public LimitExceededException? AsLimitError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            LimitExceededException limit when Owns(limit) => limit,
            OperationCanceledException when TimedOut => TimeLimitExceeded(),
            _ => null,
        };
    }

    /// <summary>Port of the <c>time_limit</c> error raised when the scope's time elapses.</summary>
    public LimitExceededException TimeLimitExceeded()
    {
        var seconds = Limits.TimeLimit?.TotalSeconds ?? 0;
        var limitStr = LimitExceededException.FormatLimit(seconds);
        return new LimitExceededException("time", limitStr, seconds, $"Time limit exceeded. limit: {limitStr} seconds") { LimitSource = this };
    }

    /// <summary>Port of <c>check_message_limit</c>: checks the innermost message limit of the current flow.</summary>
    public static void CheckMessageLimit(int count, bool raiseForEqual = true) => Leaf.Value?.CheckMessages(count, raiseForEqual);

    /// <summary>Port of <c>record_model_usage</c> + <c>check_token_limit</c> for the current flow's scopes.</summary>
    public static void RecordUsage(ModelUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var leaf = Leaf.Value;
        if (leaf is null)
        {
            return;
        }

        leaf.Record(usage);
        leaf.CheckTokens();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // Python raises when the popped node is not the leaf; here the ambient leaf is restored to this
        // scope's parent, which is the same thing whenever scopes are disposed in stack order.
        Leaf.Value = _parent;
        _timeout?.Dispose();
    }

    private void Record(ModelUsage usage)
    {
        _parent?.Record(usage);
        lock (_sync)
        {
            _usage += usage;
        }
    }

    private void CheckTokens()
    {
        _parent?.CheckTokens();
        if (Limits.TokenLimit is not { } limit)
        {
            return;
        }

        int total;
        lock (_sync)
        {
            total = _usage.TotalTokens;
        }

        if (total > limit)
        {
            var limitStr = LimitExceededException.FormatLimit(limit);
            var message = $"Token limit exceeded. value: {LimitExceededException.FormatLimit(total)}; limit: {limitStr}";
            throw new LimitExceededException("token", limitStr, total, message) { LimitSource = this };
        }
    }

    private void CheckMessages(int count, bool raiseForEqual)
    {
        var node = this;
        while (node is not null && node.Limits.MessageLimit is null)
        {
            node = node._parent;
        }

        if (node?.Limits.MessageLimit is not { } limit)
        {
            return;
        }

        if (count > limit || (raiseForEqual && count == limit))
        {
            var reachedOrExceeded = count == limit ? "reached" : "exceeded";
            var limitStr = LimitExceededException.FormatLimit(limit);
            var message = $"Message limit {reachedOrExceeded}. count: {LimitExceededException.FormatLimit(count)}; limit: {limitStr}";
            throw new LimitExceededException("message", limitStr, count, message) { LimitSource = node };
        }
    }
}

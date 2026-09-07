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
/// Port of <c>apply_limits(limits)</c> around one agent run: enters a <see cref="Context.TokenLimit"/>,
/// <see cref="Context.MessageLimit"/> and <see cref="Context.TimeLimit"/> for the members of an
/// <see cref="AgentLimits"/> that are set, on the same limit trees the sample-level limits live on, so nested
/// agent scopes and the sample's own limits are checked together by <c>Model.GenerateAsync</c>: token usage is
/// recorded on every enclosing scope and checked root-first (the outermost exceeded limit wins), the message
/// count against the innermost message limit only, as in Python. Dispose the scope (in stack order) to leave.
/// </summary>
public sealed class AgentLimitScope : IDisposable
{
    private static readonly AsyncLocal<AgentLimitScope?> Leaf = new();

    private readonly AgentLimitScope? _parent;

    private readonly CancellationToken _outer;

    private readonly CancellationTokenSource? _linked;

    private readonly Context.LimitScope _scope;

    private readonly TokenLimit? _tokens;

    private readonly TimeLimit? _time;

    private bool _disposed;

    private AgentLimitScope(AgentLimits limits, AgentLimitScope? parent, CancellationToken outer)
    {
        Limits = limits;
        _parent = parent;
        _outer = outer;
        var entered = new List<Limit>(3);
        if (limits.TokenLimit is { } tokenLimit)
        {
            _tokens = new TokenLimit(tokenLimit);
            entered.Add(_tokens);
        }

        if (limits.MessageLimit is { } messageLimit)
        {
            entered.Add(new MessageLimit(messageLimit));
        }

        if (limits.TimeLimit is { } timeLimit)
        {
            _time = new TimeLimit(timeLimit);
            entered.Add(_time);
        }

        _scope = Limit.Apply([.. entered]);
        if (_time is not null)
        {
            _linked = CancellationTokenSource.CreateLinkedTokenSource(outer, _time.Token);
            CancellationToken = _linked.Token;
        }
        else
        {
            CancellationToken = outer;
        }
    }

    /// <summary>The innermost agent scope of the current async flow (null outside any scope).</summary>
    public static AgentLimitScope? Current => Leaf.Value;

    public AgentLimits Limits { get; }

    /// <summary>The scoped limits this scope entered (the set members of <see cref="Limits"/>, in token, message, time order).</summary>
    public IReadOnlyList<Limit> AppliedLimits => _scope.Limits;

    /// <summary>The token to run the agent with: the caller's token, also cancelled when the time limit elapses.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Whether this scope's time limit elapsed (and the caller's own token did not fire).</summary>
    public bool TimedOut => _time is { Exceeded: true } && !_outer.IsCancellationRequested;

    /// <summary>Tokens recorded while this scope was open (including nested scopes); empty when the scope has no token limit.</summary>
    public ModelUsage Usage => _tokens?.RecordedUsage ?? new ModelUsage();

    /// <summary>
    /// Port of entering <c>apply_limits(limits)</c>: enters the set limits for the current async flow. Dispose
    /// the scope (in stack order) to leave. Negative limits are rejected like Python's <c>ValueError</c>
    /// (<see cref="ArgumentOutOfRangeException"/>).
    /// </summary>
    public static AgentLimitScope Apply(AgentLimits? limits, CancellationToken cancellationToken = default)
    {
        var scope = new AgentLimitScope(limits ?? AgentLimits.None, Leaf.Value, cancellationToken);
        Leaf.Value = scope;
        return scope;
    }

    /// <summary>Whether <paramref name="exception"/> was raised by one of this scope's limits (Python's <c>source in limits</c>).</summary>
    public bool Owns(LimitExceededException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.SourceLimit is { } source && _scope.Limits.Contains(source);
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

    /// <summary>
    /// Port of the <c>time_limit</c> error raised when the scope's time elapses, recording the
    /// <see cref="SampleLimitEvent"/> as <c>_TimeLimit.__exit__</c> does.
    /// </summary>
    /// <exception cref="InvalidOperationException">The scope has no time limit or it has not elapsed.</exception>
    public LimitExceededException TimeLimitExceeded()
    {
        var error = _time?.ExceededError() ?? throw new InvalidOperationException("The scope's time limit has not elapsed.");
        SampleContext.Current?.Transcript.Add(new SampleLimitEvent("time", error.Message, error.Limit));
        return error;
    }

    /// <summary>Port of <c>check_message_limit</c>: checks the innermost message limit of the current flow.</summary>
    public static void CheckMessageLimit(int count, bool raiseForEqual = true) => MessageLimit.CheckMessageLimit(count, raiseForEqual);

    /// <summary>Port of <c>record_model_usage</c> + <c>check_token_limit</c> for the current flow's token limits.</summary>
    public static void RecordUsage(ModelUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        TokenLimit.RecordModelUsage(usage);
        TokenLimit.CheckTokenLimit();
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
        _scope.Dispose();
        _linked?.Dispose();
    }
}

using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Context;

/// <summary>
/// Port of <c>token_limit()</c> / <c>_TokenLimit</c>: limits the tokens used while the scope is open (tokens
/// used before are not counted). Cooperative: <see cref="RecordModelUsage"/> and <see cref="CheckTokenLimit"/>
/// are called by <c>Model.GenerateAsync</c> after every generation. Usage is recorded on this scope and every
/// ancestor; the check runs from the root down, so the outermost exceeded limit raises.
/// </summary>
public sealed class TokenLimit : Limit
{
    internal static LimitTree<TokenLimit> Tree { get; } = new();

    private readonly object _sync = new();

    private int? _limit;

    private ModelUsage _usage = new();

    /// <summary>
    /// Creates a token limit. <paramref name="type"/> is which tokens are metered: "all" (total tokens, the
    /// default) or "output" (output tokens, which include reasoning). Python's arithmetic formulas over
    /// <c>input</c>/<c>output</c> are not ported and are rejected. A negative limit is an <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    public TokenLimit(int? limit, string type = "all")
    {
        ArgumentNullException.ThrowIfNull(type);
        Validate(limit);
        Type = type is "all" or "output"
            ? type
            : throw new ArgumentException($"token limit: unsupported type '{type}' (this port meters \"all\" or \"output\"; formulas are not supported).", nameof(type));
        _limit = limit;
    }

    /// <summary>Which tokens this limit meters: "all" or "output".</summary>
    public string Type { get; }

    /// <summary>The configured limit. Setting it does not trigger a check (which could now have been exceeded).</summary>
    public int? Limit
    {
        get => _limit;
        set
        {
            Validate(value);
            _limit = value;
        }
    }

    public override double? LimitValue => Limit;

    /// <summary>The metered token count recorded within this scope.</summary>
    public override double Usage => Meter(RecordedUsage);

    /// <summary>All usage recorded within this scope (its ancestors hold their own totals).</summary>
    public ModelUsage RecordedUsage
    {
        get
        {
            lock (_sync)
            {
                return _usage;
            }
        }
    }

    /// <summary>Records usage for this scope and its ancestors without checking.</summary>
    public void Record(ModelUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        (Parent as TokenLimit)?.Record(usage);
        lock (_sync)
        {
            _usage += usage;
        }
    }

    /// <summary>Checks this limit and its ancestors, root first; raises <see cref="LimitExceededException"/> for the first one exceeded.</summary>
    public void Check()
    {
        (Parent as TokenLimit)?.Check();
        CheckSelf();
    }

    /// <summary>The innermost token limit of the current async flow, or null.</summary>
    public static TokenLimit? Current => Tree.Leaf;

    /// <summary>Port of <c>record_model_usage</c>: records against the active token limits without checking (a no-op while suspended).</summary>
    public static void RecordModelUsage(ModelUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (Tree.IsSuspended)
        {
            return;
        }

        Tree.Leaf?.Record(usage);
    }

    /// <summary>Port of <c>check_token_limit</c>: checks every active token limit of the current flow (a no-op while suspended).</summary>
    public static void CheckTokenLimit()
    {
        if (Tree.IsSuspended)
        {
            return;
        }

        Tree.Leaf?.Check();
    }

    /// <summary>Port of <c>suspend_token_limit</c>: tokens used until the result is disposed are neither recorded nor checked, nested scopes included.</summary>
    public static IDisposable SuspendTokenLimit() => Tree.Suspended();

    /// <summary>
    /// Port of <c>token_limit_usage()</c>: the metered usage of the sample's outermost token limit, or null when no
    /// ceiling is configured or no token limit scope is active. Snapshot-aware like Python (see <see cref="SampleLimits"/>).
    /// </summary>
    public static int? TokenLimitUsage()
    {
        Limit? root = SampleLimits.SnapshotOrNull()?.Token ?? Tree.Root;
        return root is { LimitValue: not null } ? (int)root.Usage : null;
    }

    protected override void EnterCore() => Tree.Push(this);

    protected override void ExitCore() => Tree.Pop(this);

    private void CheckSelf()
    {
        if (Limit is not { } limit)
        {
            return;
        }

        var total = Meter(RecordedUsage);
        if (total <= limit)
        {
            return;
        }

        var totalStr = LimitExceededException.FormatLimit(total);
        var limitStr = LimitExceededException.FormatLimit(limit);
        var message = Type == "all"
            ? $"Token limit exceeded. value: {totalStr}; limit: {limitStr}"
            : $"Output token limit exceeded. value: {totalStr}; limit: {limitStr}";
        EmitLimitEvent("token", limit, message);
        throw new LimitExceededException("token", total, limit, message, this);
    }

    private int Meter(ModelUsage usage) => Type == "all" ? usage.TotalTokens : usage.OutputTokens;

    private static void Validate(int? value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"Token limit value must be a non-negative integer or None: {value}");
        }
    }
}

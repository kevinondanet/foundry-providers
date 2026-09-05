namespace InspectAzureAI.Eval.Context;

/// <summary>
/// Port of <c>message_limit()</c> / <c>_MessageLimit</c>: limits the number of messages in a conversation (the
/// whole conversation, not just new messages). Cooperative: <see cref="CheckMessageLimit"/> is called by
/// <c>Model.GenerateAsync</c> before each generation. Unlike the other kinds only the innermost scope is
/// checked; ancestors are not.
/// </summary>
public sealed class MessageLimit : Limit
{
    internal static LimitTree<MessageLimit> Tree { get; } = new();

    private int? _limit;

    /// <summary>Creates a message limit; a negative limit is an <see cref="ArgumentOutOfRangeException"/>.</summary>
    public MessageLimit(int? limit)
    {
        Validate(limit);
        _limit = limit;
    }

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

    /// <summary>Not supported, as in Python: query the messages of the task or agent state instead.</summary>
    public override double Usage =>
        throw new NotSupportedException("Retrieving the message count from a limit is not supported. Please query the messages property on the task or agent state instead.");

    /// <summary>
    /// Checks this limit only (not its ancestors): raises when <paramref name="count"/> exceeds it, or equals it
    /// when <paramref name="raiseForEqual"/> (a generation at the limit would be wasted).
    /// </summary>
    public void Check(int count, bool raiseForEqual)
    {
        if (Limit is not { } limit)
        {
            return;
        }

        if (count > limit || (raiseForEqual && count == limit))
        {
            var reachedOrExceeded = count == limit ? "reached" : "exceeded";
            var message = $"Message limit {reachedOrExceeded}. count: {LimitExceededException.FormatLimit(count)}; limit: {LimitExceededException.FormatLimit(limit)}";
            EmitLimitEvent("message", limit, message);
            throw new LimitExceededException("message", count, limit, message, this);
        }
    }

    /// <summary>The innermost message limit of the current async flow, or null.</summary>
    public static MessageLimit? Current => Tree.Leaf;

    /// <summary>Port of <c>check_message_limit</c>: checks the innermost active message limit against <paramref name="count"/>.</summary>
    public static void CheckMessageLimit(int count, bool raiseForEqual) => Tree.Leaf?.Check(count, raiseForEqual);

    protected override void EnterCore() => Tree.Push(this);

    protected override void ExitCore() => Tree.Pop(this);

    private static void Validate(int? value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"Message limit value must be a non-negative integer or None: {value}");
        }
    }
}

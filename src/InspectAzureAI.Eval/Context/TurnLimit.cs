namespace InspectAzureAI.Eval.Context;

/// <summary>
/// Port of <c>turn_limit()</c> / <c>_TurnLimit</c>: limits the number of turns (top-level model generations,
/// each producing one assistant message) taken while the scope is open — distinct from
/// <see cref="MessageLimit"/>, which counts every message. Cooperative: <c>Model.GenerateAsync</c> calls
/// <see cref="RecordTurn"/> once per completed generation, which records on this scope and its ancestors and
/// then checks from the root down.
/// </summary>
public sealed class TurnLimit : Limit
{
    internal static LimitTree<TurnLimit> Tree { get; } = new();

    private int? _limit;

    private int _turns;

    /// <summary>Creates a turn limit; a negative limit is an <see cref="ArgumentOutOfRangeException"/>.</summary>
    public TurnLimit(int? limit)
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

    /// <summary>Turns recorded within this scope.</summary>
    public int Turns => Volatile.Read(ref _turns);

    public override double Usage => Turns;

    /// <summary>Records a turn for this scope and its ancestors without checking.</summary>
    public void Record()
    {
        (Parent as TurnLimit)?.Record();
        Interlocked.Increment(ref _turns);
    }

    /// <summary>Checks this limit and its ancestors, root first; raises <see cref="LimitExceededException"/> for the first one exceeded.</summary>
    public void Check()
    {
        (Parent as TurnLimit)?.Check();
        CheckSelf();
    }

    /// <summary>The innermost turn limit of the current async flow, or null.</summary>
    public static TurnLimit? Current => Tree.Leaf;

    /// <summary>Port of <c>record_turn</c>: records a turn against the active turn limits and checks them (a no-op while suspended).</summary>
    public static void RecordTurn()
    {
        if (Tree.IsSuspended || Tree.Leaf is not { } leaf)
        {
            return;
        }

        leaf.Record();
        leaf.Check();
    }

    /// <summary>Port of <c>check_turn_limit</c>: checks every active turn limit of the current flow (a no-op while suspended).</summary>
    public static void CheckTurnLimit()
    {
        if (Tree.IsSuspended)
        {
            return;
        }

        Tree.Leaf?.Check();
    }

    /// <summary>Port of <c>suspend_turn_limit</c>: generations until the result is disposed are neither recorded nor checked, nested scopes included.</summary>
    public static IDisposable SuspendTurnLimit() => Tree.Suspended();

    /// <summary>Port of <c>turn_count()</c>: turns recorded against the sample's outermost turn limit, or null without an active scope. Snapshot-aware like Python.</summary>
    public static int? TurnCount()
    {
        Limit? root = SampleLimits.SnapshotOrNull()?.Turn ?? Tree.Root;
        return root is null ? null : (int)root.Usage;
    }

    protected override void EnterCore() => Tree.Push(this);

    protected override void ExitCore() => Tree.Pop(this);

    private void CheckSelf()
    {
        if (Limit is not { } limit)
        {
            return;
        }

        var turns = Turns;
        if (turns <= limit)
        {
            return;
        }

        var message = $"Turn limit exceeded. value: {LimitExceededException.FormatLimit(turns)}; limit: {LimitExceededException.FormatLimit(limit)}";
        EmitLimitEvent("turn", limit, message);
        throw new LimitExceededException("turn", turns, limit, message, this);
    }

    private static void Validate(int? value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"Turn limit value must be a non-negative integer or None: {value}");
        }
    }
}

namespace InspectAzureAI.Eval.Context;

/// <summary>
/// Port of <c>util/_limit.py</c> <c>SampleLimits</c> and <c>sample_limits()</c>: the top-level limits applied
/// to the running sample. While the solvers run these are the live root scopes; once the runner leaves them
/// (before scoring) it records a frozen snapshot so scorers still see the final limits and usage. <see cref="Cost"/>
/// is the slot for a cost limit built on <see cref="Limit"/> (null until one is wired in).
/// </summary>
public sealed record SampleLimits(Limit Token, Limit Message, Limit Turn, Limit Working, Limit Time, Limit? Cost = null)
{
    private static readonly AsyncLocal<SampleLimits?> Snapshot = new();

    /// <summary>
    /// Port of <c>sample_limits()</c>: the sample's root limits (snapshot first, then the live trees). Throws
    /// <see cref="InvalidOperationException"/> when no sample is running.
    /// </summary>
    public static SampleLimits Current()
    {
        if (Snapshot.Value is { } data)
        {
            return data;
        }

        return new SampleLimits(
            Require(TokenLimit.Tree.Root, "token"),
            Require(MessageLimit.Tree.Root, "message"),
            Require(TurnLimit.Tree.Root, "turn"),
            Require(WorkingLimit.Tree.Root, "working"),
            Require(TimeLimit.Tree.Root, "time"));
    }

    internal static SampleLimits? SnapshotOrNull() => Snapshot.Value;

    /// <summary>Port of <c>record_sample_limit_data</c>: freezes the current root limits (the message usage is supplied, as a message limit has none).</summary>
    internal static void RecordSnapshot(int messageUsage)
    {
        var current = Current();
        Snapshot.Value = new SampleLimits(
            new LimitData(current.Token),
            new LimitData(current.Message, messageUsage),
            new LimitData(current.Turn),
            new LimitData(current.Working),
            new LimitData(current.Time),
            current.Cost is { } cost ? new LimitData(cost) : null);
    }

    /// <summary>Port of <c>reset_sample_limit_data</c>: clears a snapshot left by a prior attempt of the sample.</summary>
    internal static void ResetSnapshot() => Snapshot.Value = null;

    private static T Require<T>(T? node, string name) where T : Limit =>
        node ?? throw new InvalidOperationException($"No {name} limit node found. Is there a running sample?");
}

/// <summary>Port of <c>_LimitData</c>: a limit that copies its values from another one at construction.</summary>
internal sealed class LimitData : Limit
{
    public LimitData(Limit source, double? usage = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        LimitValue = source.LimitValue;
        Usage = usage ?? source.Usage;
    }

    public override double? LimitValue { get; }

    public override double Usage { get; }

    protected override void EnterCore()
    {
    }

    protected override void ExitCore()
    {
    }
}

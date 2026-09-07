namespace InspectAzureAI.Eval.Context;

/// <summary>
/// Port of <c>working_limit()</c> / <c>_WorkingLimit</c>: limits working time, the wall-clock time elapsed
/// while the scope is open minus the waiting time reported to it (retry back-off, waiting on a shared
/// resource). Waiting time is recorded on this scope and its ancestors; the check runs from the root down.
/// The sample runner enforces the sample-level limit with a background monitor, as Python's
/// <c>monitor_working_limit()</c> does; <see cref="CheckWorkingLimit"/> is the cooperative form.
/// </summary>
public sealed class WorkingLimit : Limit
{
    internal static LimitTree<WorkingLimit> Tree { get; } = new();

    private readonly TimeProvider _time;

    private readonly object _sync = new();

    private long _startTimestamp;

    private bool _started;

    private TimeSpan? _finalElapsed;

    private TimeSpan _waiting;

    /// <summary>Creates a working limit; a negative limit is an <see cref="ArgumentOutOfRangeException"/>. <paramref name="time"/> is the clock (injectable for tests).</summary>
    public WorkingLimit(TimeSpan? limit, TimeProvider? time = null)
    {
        TimeLimit.ValidateTimeLimit("Working time", limit);
        Limit = limit;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The configured limit (null for unlimited).</summary>
    public TimeSpan? Limit { get; }

    public override double? LimitValue => Limit?.TotalSeconds;

    /// <summary>Waiting time reported within this scope.</summary>
    public TimeSpan WaitingTime
    {
        get
        {
            lock (_sync)
            {
                return _waiting;
            }
        }
    }

    /// <summary>Working seconds: elapsed since entry (frozen once left) minus <see cref="WaitingTime"/>; zero before entry.</summary>
    public override double Usage => !_started ? 0 : ((_finalElapsed ?? _time.GetElapsedTime(_startTimestamp)) - WaitingTime).TotalSeconds;

    /// <summary>Records waiting time for this scope and its ancestors.</summary>
    public void RecordWaitingTime(TimeSpan waiting)
    {
        (Parent as WorkingLimit)?.RecordWaitingTime(waiting);
        lock (_sync)
        {
            _waiting += waiting;
        }
    }

    /// <summary>Checks this limit and its ancestors, root first: the error of the first one exceeded (not thrown), or null.</summary>
    public LimitExceededException? Check()
    {
        if (Parent is WorkingLimit parent && parent.Check() is { } error)
        {
            return error;
        }

        return CheckSelf();
    }

    /// <summary>The innermost working limit of the current async flow, or null.</summary>
    public static WorkingLimit? Current => Tree.Leaf;

    /// <summary>Port of <c>record_waiting_time</c>: records against the active working limits of the current flow.</summary>
    public static void RecordActiveWaitingTime(TimeSpan waiting) => Tree.Leaf?.RecordWaitingTime(waiting);

    /// <summary>Port of <c>working_limit_exceeded</c>: the error of the first exceeded working limit of the current flow, or null.</summary>
    public static LimitExceededException? WorkingLimitExceeded() => Tree.Leaf?.Check();

    /// <summary>Port of <c>check_working_limit</c>: emits the <see cref="SampleLimitEvent"/> and throws when a working limit is exceeded.</summary>
    public static void CheckWorkingLimit()
    {
        if (WorkingLimitExceeded() is { } error)
        {
            EmitLimitEvent("working", error.Limit, error.Message);
            throw error;
        }
    }

    /// <summary>
    /// Port of <c>report_sample_waiting_time</c>: records <paramref name="waiting"/> against the active working
    /// limits and the sample's own accounting (<see cref="Limits.WaitingTime"/>, which the log's
    /// <c>working_time</c> subtracts from <c>total_time</c>).
    /// </summary>
    public static void ReportSampleWaitingTime(TimeSpan waiting)
    {
        RecordActiveWaitingTime(waiting);
        SampleContext.Current?.Limits.RecordWaitingTime(waiting);
    }

    protected override void EnterCore()
    {
        _startTimestamp = _time.GetTimestamp();
        _started = true;
        lock (_sync)
        {
            _waiting = TimeSpan.Zero;
        }

        Tree.Push(this);
    }

    protected override void ExitCore()
    {
        _finalElapsed = _time.GetElapsedTime(_startTimestamp);
        Tree.Pop(this);
    }

    private LimitExceededException? CheckSelf()
    {
        if (Limit is not { } limit)
        {
            return null;
        }

        var usage = Usage;
        if (usage <= limit.TotalSeconds)
        {
            return null;
        }

        var message = $"Working time limit exceeded. limit: {TimeLimit.FormatSeconds(limit)} seconds";
        return new LimitExceededException("working", usage, limit.TotalSeconds, message, this);
    }
}

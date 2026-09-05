using System.Globalization;

namespace InspectAzureAI.Eval.Context;

/// <summary>
/// Port of <c>time_limit()</c> / <c>_TimeLimit</c>: limits the wall-clock time that can elapse while the scope
/// is open. Python cancels the block with an anyio cancel scope and raises <see cref="LimitExceededException"/>
/// where the scope was opened; here <see cref="Token"/> fires at the deadline, the body observes it as an
/// <see cref="OperationCanceledException"/>, and <see cref="ThrowIfExceeded"/> (called by
/// <see cref="Limit.ApplyAsync"/> and the sample runner) turns that into the limit error, emitting the
/// <see cref="SampleLimitEvent"/>. The elapsed time is measured independently of the timer, so it is an approximation.
/// </summary>
public sealed class TimeLimit : Limit
{
    internal static LimitTree<TimeLimit> Tree { get; } = new();

    private readonly TimeProvider _time;

    private long _startTimestamp;

    private bool _started;

    private TimeSpan? _finalElapsed;

    private CancellationTokenSource? _cts;

    private bool _exceeded;

    /// <summary>Creates a time limit; a negative limit is an <see cref="ArgumentOutOfRangeException"/>. <paramref name="time"/> drives both the clock and the deadline timer.</summary>
    public TimeLimit(TimeSpan? limit, TimeProvider? time = null)
    {
        ValidateTimeLimit("Time", limit);
        Limit = limit;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The configured limit (null for unlimited).</summary>
    public TimeSpan? Limit { get; }

    public override double? LimitValue => Limit?.TotalSeconds;

    /// <summary>Seconds elapsed since the scope was entered (frozen once it is left; zero before entry).</summary>
    public override double Usage => !_started ? 0 : (_finalElapsed ?? _time.GetElapsedTime(_startTimestamp)).TotalSeconds;

    /// <summary>A token that fires at the deadline while the scope is open; <see cref="CancellationToken.None"/> without a limit or outside the scope.</summary>
    public CancellationToken Token => _cts?.Token ?? CancellationToken.None;

    /// <summary>True once the deadline has fired.</summary>
    public bool Exceeded => _exceeded || _cts?.IsCancellationRequested == true;

    /// <summary>The error for an elapsed deadline (without emitting an event), or null while the deadline has not fired.</summary>
    public LimitExceededException? ExceededError()
    {
        if (!Exceeded || Limit is not { } limit)
        {
            return null;
        }

        var message = $"Time limit exceeded. limit: {FormatSeconds(limit)} seconds";
        return new LimitExceededException("time", Usage, limit.TotalSeconds, message, this);
    }

    /// <summary>Port of the raise in <c>_TimeLimit.__exit__</c>: emits the <see cref="SampleLimitEvent"/> and throws when the deadline fired.</summary>
    public void ThrowIfExceeded()
    {
        if (ExceededError() is { } error)
        {
            EmitLimitEvent("time", error.Limit, error.Message);
            throw error;
        }
    }

    /// <summary>The innermost time limit of the current async flow, or null.</summary>
    public static TimeLimit? Current => Tree.Leaf;

    /// <summary>Python renders the configured seconds with <c>str()</c>: an integral limit without a fraction, otherwise the shortest round-trip form.</summary>
    internal static string FormatSeconds(TimeSpan limit)
    {
        var seconds = limit.TotalSeconds;
        return seconds == Math.Floor(seconds) && Math.Abs(seconds) < 1e15
            ? ((long)seconds).ToString(CultureInfo.InvariantCulture)
            : seconds.ToString("R", CultureInfo.InvariantCulture);
    }

    internal static void ValidateTimeLimit(string name, TimeSpan? value)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"{name} limit value must be a non-negative float or None: {value.Value.TotalSeconds}");
        }
    }

    protected override void EnterCore()
    {
        Tree.Push(this);
        _startTimestamp = _time.GetTimestamp();
        _started = true;
        if (Limit is { } limit)
        {
            _cts = new CancellationTokenSource(limit, _time);
        }
    }

    protected override void ExitCore()
    {
        _finalElapsed = _time.GetElapsedTime(_startTimestamp);
        Tree.Pop(this);
        if (_cts is { } cts)
        {
            _exceeded = cts.IsCancellationRequested;
            _cts = null;
            cts.Dispose();
        }
    }
}

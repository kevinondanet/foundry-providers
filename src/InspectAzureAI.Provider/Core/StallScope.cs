using System.Diagnostics;

namespace InspectAzureAI.Provider.Core;

/// <summary>
/// One attempt's stall-detection state (port of the <c>_StallScope</c> + <c>anyio.CancelScope</c> pair of
/// <c>src/inspect_ai/model/_stream.py</c>; see <c>design/stream-idle-timeout.md</c>). The deadline starts
/// infinite; the first <see cref="Bump"/> (the attempt's first stream report) arms it at now +
/// <see cref="Timeout"/> and every later report pushes it forward, throttled to one write per
/// <see cref="BumpInterval"/>. An attempt that never streams can therefore never fire. When the deadline
/// passes, <see cref="Token"/> is cancelled and <see cref="Fired"/> becomes true; the model wrapper turns
/// that into a <c>StreamIdleTimeoutException</c> and retries.
/// </summary>
public sealed class StallScope : IDisposable
{
    /// <summary>
    /// Port of <c>STALL_DEADLINE_BUMP_INTERVAL</c>: maximum seconds between deadline bumps. Rescheduling the
    /// timer per chunk is needless work; the interval is capped at a tenth of the timeout so a small timeout
    /// does not spend a meaningful fraction of its window on throttle slack.
    /// </summary>
    public const double StallDeadlineBumpInterval = 1.0;

    private readonly CancellationTokenSource _cts = new();
    private double _lastBump;

    /// <param name="timeoutSeconds">The <c>stream_idle_timeout</c> (seconds of silence tolerated).</param>
    public StallScope(double timeoutSeconds)
    {
        if (!(timeoutSeconds > 0) || double.IsInfinity(timeoutSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), timeoutSeconds, "stream_idle_timeout must be a positive number of seconds.");
        }

        Timeout = timeoutSeconds;
        BumpInterval = Math.Min(StallDeadlineBumpInterval, timeoutSeconds / 10.0);
    }

    /// <summary>Seconds of silence after which the scope fires.</summary>
    public double Timeout { get; }

    /// <summary>Minimum seconds between deadline writes (<c>min(1, timeout / 10)</c>).</summary>
    public double BumpInterval { get; }

    /// <summary>The deadline on the monotonic clock (<see cref="Now"/>), or null while still infinite (never bumped).</summary>
    public double? Deadline { get; private set; }

    /// <summary>Whether the first report has armed the scope.</summary>
    public bool Armed => Deadline is not null;

    /// <summary>Cancelled when the deadline passes.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>Whether the deadline passed (the attempt stalled).</summary>
    public bool Fired => _cts.IsCancellationRequested;

    /// <summary>Monotonic seconds (<see cref="Stopwatch"/> based), the clock <see cref="Deadline"/> is expressed on.</summary>
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>
    /// Port of <c>_bump_stall_deadline</c>: the stream is alive, so push the deadline forward. The first call
    /// arms unconditionally; later calls within <see cref="BumpInterval"/> of the last write are no-ops.
    /// </summary>
    public void Bump()
    {
        if (_cts.IsCancellationRequested)
        {
            return;
        }

        var now = Now;
        if (Deadline is not null && now - _lastBump < BumpInterval)
        {
            return;
        }

        _lastBump = now;
        Deadline = now + Timeout;
        _cts.CancelAfter(TimeSpan.FromSeconds(Timeout));
    }

    public void Dispose() => _cts.Dispose();
}

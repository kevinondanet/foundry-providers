namespace InspectAzureAI.Eval.Runner;

/// <summary>
/// Port of <c>_eval/task/error.py</c> <c>SampleErrorHandler</c>: counts terminal sample errors (after any
/// per-sample retries are exhausted) and decides, per the <see cref="FailOnError"/> policy, whether the eval must
/// abort. <see cref="TotalSamples"/> is the planned number of sample runs (dataset slice × epochs), the
/// denominator of a fractional threshold. Samples complete concurrently, so the count is guarded by a lock.
/// </summary>
internal sealed class SampleErrorHandler(FailOnError? failOnError, int totalSamples)
{
    private readonly object _sync = new();

    private int _errorCount;

    /// <summary>The policy in force; Python treats an unset value as <c>True</c>.</summary>
    public FailOnError FailOnError { get; } = failOnError ?? FailOnError.Always;

    public int TotalSamples { get; } = totalSamples;

    /// <summary>Terminal sample errors recorded so far.</summary>
    public int ErrorCount
    {
        get
        {
            lock (_sync)
            {
                return _errorCount;
            }
        }
    }

    /// <summary>Port of <c>__call__</c>: records one terminal sample error and returns whether the eval must abort now.</summary>
    public bool RecordError()
    {
        int count;
        lock (_sync)
        {
            count = ++_errorCount;
        }

        return ShouldEvalFail(count, TotalSamples, FailOnError);
    }

    /// <summary>Port of <c>_should_eval_fail</c> (an unset policy fails on any error, like Python's <c>None</c>).</summary>
    public static bool ShouldEvalFail(int errorCount, int totalSamples, FailOnError? failOnError) =>
        (failOnError ?? FailOnError.Always).ShouldFail(errorCount, totalSamples);
}

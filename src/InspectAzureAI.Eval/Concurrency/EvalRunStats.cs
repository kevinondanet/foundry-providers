namespace InspectAzureAI.Eval.Concurrency;

/// <summary>
/// The live counters Python's display footer and <c>ctl</c> views show, captured for <c>IEvalReporter.Stats</c>:
/// sample concurrency, every visible connection limit (in use / limit), per-model throughput, the process retry
/// count and the aggregate output-token rate (null until a retry has occurred, as the footer gate does).
/// </summary>
public sealed record EvalRunStats
{
    /// <summary>The run's sample limiter, when the stats were captured inside a run.</summary>
    public ConcurrencyStatus? Samples { get; init; }

    /// <summary>Port of <c>concurrency_status_display()</c>.</summary>
    public IReadOnlyDictionary<string, ConcurrencyStatus> Connections { get; init; } = new Dictionary<string, ConcurrencyStatus>(StringComparer.Ordinal);

    /// <summary>Port of <c>throughput_snapshot()</c>.</summary>
    public IReadOnlyDictionary<string, ModelThroughputView> Throughput { get; init; } = new Dictionary<string, ModelThroughputView>(StringComparer.Ordinal);

    /// <summary>Port of <c>http_retries_count()</c>.</summary>
    public int HttpRetries { get; init; }

    /// <summary>Port of <c>throughput_footer_rate()</c>.</summary>
    public double? OutputTokensPerSecond { get; init; }

    /// <summary>Captures the current counters.</summary>
    public static EvalRunStats Capture(ISampleLimiter? samples = null, int window = InspectAzureAI.Eval.Concurrency.Throughput.DefaultWindowSeconds) => new()
    {
        Samples = samples is null ? null : new ConcurrencyStatus(samples.InUse, samples.Limit),
        Connections = Concurrency.StatusDisplay(),
        Throughput = InspectAzureAI.Eval.Concurrency.Throughput.Snapshot(window),
        HttpRetries = Concurrency.HttpRetriesCount,
        OutputTokensPerSecond = InspectAzureAI.Eval.Concurrency.Throughput.FooterRate(window),
    };
}

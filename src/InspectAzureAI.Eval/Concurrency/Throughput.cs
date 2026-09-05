using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Concurrency;

/// <summary>Sums over the buckets that overlap a window.</summary>
public readonly record struct WindowSums(int OutputTokens, int TotalTokens, int Requests, int Retries);

/// <summary>
/// Port of <c>model/_throughput.py</c> <c>TokenBuckets</c>: a fixed ring of <see cref="Throughput.BucketCount"/>
/// buckets of <see cref="Throughput.BucketSeconds"/> on the monotonic clock. Slots are epoch-tagged rather than
/// zeroed on advance: a write resets a slot whose stored epoch differs from the current one, and reads include
/// only slots whose epoch falls inside the requested window, so a gap in traffic longer than the horizon can't
/// leak a previous lap's counts into a window sum. Memory is constant per model regardless of request rate.
/// </summary>
public sealed class TokenBuckets
{
    private readonly Bucket[] _buckets = Enumerable.Range(0, Throughput.BucketCount).Select(_ => new Bucket()).ToArray();

    public void Add(double now, int outputTokens = 0, int totalTokens = 0, int requests = 0, int retries = 0)
    {
        var epoch = EpochOf(now);
        var bucket = _buckets[(int)(epoch % Throughput.BucketCount)];
        if (bucket.Epoch != epoch)
        {
            bucket.Epoch = epoch;
            bucket.OutputTokens = 0;
            bucket.TotalTokens = 0;
            bucket.Requests = 0;
            bucket.Retries = 0;
        }

        bucket.OutputTokens += outputTokens;
        bucket.TotalTokens += totalTokens;
        bucket.Requests += requests;
        bucket.Retries += retries;
    }

    /// <summary>Whole-bucket sums overlapping <c>[now - window, now]</c>; the window is clamped to the horizon.</summary>
    public WindowSums WindowSums(double now, double window)
    {
        window = Math.Min(window, Throughput.HorizonSeconds);
        var lo = EpochOf(now - window);
        var hi = EpochOf(now);
        int outputTokens = 0, totalTokens = 0, requests = 0, retries = 0;
        foreach (var bucket in _buckets)
        {
            if (bucket.Epoch >= lo && bucket.Epoch <= hi)
            {
                outputTokens += bucket.OutputTokens;
                totalTokens += bucket.TotalTokens;
                requests += bucket.Requests;
                retries += bucket.Retries;
            }
        }

        return new WindowSums(outputTokens, totalTokens, requests, retries);
    }

    private static long EpochOf(double seconds) => (long)Math.Floor(seconds / Throughput.BucketSeconds);

    private sealed class Bucket
    {
        public long Epoch = -1;

        public int OutputTokens;

        public int TotalTokens;

        public int Requests;

        public int Retries;
    }
}

/// <summary>
/// Port of <c>BackoffInterval</c>: a scheduled retry backoff on the monotonic clock, kept out of the bucket ring
/// (a 30-minute sleep would swamp any window containing its start) and read as its overlap with the window.
/// </summary>
public sealed record BackoffInterval(double Start, double End);

/// <summary>Port of <c>ModelThroughput</c>: the accumulated state for one model (a registry value).</summary>
public sealed class ModelThroughput
{
    private readonly List<BackoffInterval> _backoffIntervals = [];

    internal ModelThroughput()
    {
    }

    public int Requests { get; internal set; }

    public int OutputTokens { get; internal set; }

    public int TotalTokens { get; internal set; }

    public int RetriesRateLimit { get; internal set; }

    public int RetriesTransient { get; internal set; }

    /// <summary>Backoff scheduled since run start (the sum of sleeps).</summary>
    public double RetryWaitSeconds { get; internal set; }

    public DateTimeOffset? FirstActivity { get; internal set; }

    public DateTimeOffset? LastActivity { get; internal set; }

    /// <summary>Monotonic first activity, for clamping a fresh run's window.</summary>
    public double? FirstActivityMonotonic { get; internal set; }

    public TokenBuckets Buckets { get; } = new();

    /// <summary>Scheduled backoff intervals still inside the horizon (see <see cref="Throughput.RecordRetryWait"/> for the pruning).</summary>
    public IReadOnlyList<BackoffInterval> BackoffIntervals => _backoffIntervals.ToArray();

    internal List<BackoffInterval> BackoffIntervalsList => _backoffIntervals;

    internal int BackoffPruneAt { get; set; } = Throughput.BackoffPruneMin;

    /// <summary>Deadline (monotonic) of the retry wait each waiter — one slot per sample — is currently sleeping in.</summary>
    internal Dictionary<object, double> ActiveWaits { get; } = new(ReferenceEqualityComparer.Instance);
}

/// <summary>Port of <c>ModelThroughputView</c>: the read-side snapshot for one model with the derived rates.</summary>
public sealed record ModelThroughputView(
    string Model,
    double WindowSeconds,
    double OutputTokensPerSecond,
    double RequestsPerMinute,
    double RetriesPerMinute,
    double BackoffRatio,
    int RetryWaitsActive,
    int Requests,
    int OutputTokens,
    int TotalTokens,
    int RetriesRateLimit,
    int RetriesTransient,
    double RetryWaitSeconds,
    DateTimeOffset? FirstActivity,
    DateTimeOffset? LastActivity);

/// <summary>
/// Port of <c>model/_throughput.py</c>: the process-global per-model throughput registry (design note
/// <c>design/model-throughput.md</c>) fed by completed generates (tokens), <see cref="Concurrency.ReportHttpRetry"/>
/// (retry counts) and the model retry loop (scheduled backoff), and read as windowed rates. Keys are the model
/// name the usage bookkeeping uses (<c>Model.Name</c>). Every method takes an optional monotonic <c>now</c> so
/// the mechanics are testable with an injected clock. A lock guards the registry because .NET generates complete
/// on many threads (Python relies on its single event loop).
/// </summary>
public static class Throughput
{
    /// <summary>Width of one ring bucket.</summary>
    public const int BucketSeconds = 10;

    /// <summary>Number of ring buckets.</summary>
    public const int BucketCount = 60;

    /// <summary>Maximum lookback window (10 minutes).</summary>
    public const int HorizonSeconds = BucketSeconds * BucketCount;

    /// <summary>Default rate window for snapshots.</summary>
    public const int DefaultWindowSeconds = 60;

    internal const int BackoffPruneMin = 64;

    private static readonly object Sync = new();

    private static readonly Dictionary<string, ModelThroughput> Registry = new(StringComparer.Ordinal);

    /// <summary>Monotonic seconds (the clock the registry keys its buckets on).</summary>
    public static double Monotonic() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Port of <c>init_model_throughput()</c>: clears the registry so a later run (or test) starts clean.</summary>
    public static void Init()
    {
        lock (Sync)
        {
            Registry.Clear();
        }
    }

    /// <summary>The raw record for <paramref name="model"/>, or null if it has no recorded activity.</summary>
    public static ModelThroughput? Record(string model)
    {
        lock (Sync)
        {
            return Registry.GetValueOrDefault(model);
        }
    }

    /// <summary>Port of <c>record_generate</c>: a completed generate's usage (cache hits must not reach here).</summary>
    public static void RecordGenerate(string model, ModelUsage usage, double? now = null)
    {
        ArgumentNullException.ThrowIfNull(usage);
        lock (Sync)
        {
            var (record, at) = Touch(model, now);
            record.Requests++;
            record.OutputTokens += usage.OutputTokens;
            record.TotalTokens += usage.TotalTokens;
            record.Buckets.Add(at, outputTokens: usage.OutputTokens, totalTokens: usage.TotalTokens, requests: 1);
        }
    }

    /// <summary>Port of <c>record_retry</c>: an HTTP retry of the given kind.</summary>
    public static void RecordRetry(string model, RetryKind kind, double? now = null)
    {
        lock (Sync)
        {
            var (record, at) = Touch(model, now);
            if (kind == RetryKind.RateLimit)
            {
                record.RetriesRateLimit++;
            }
            else
            {
                record.RetriesTransient++;
            }

            record.Buckets.Add(at, retries: 1);
        }
    }

    /// <summary>
    /// Port of <c>record_retry_wait</c> plus the per-sample retry-wait record: a scheduled backoff of
    /// <paramref name="waitSeconds"/>. Intervals that ended more than a horizon ago are pruned — on an expired head
    /// or a doubling size threshold, so one long head sleep can't block pruning of the short waits behind it.
    /// <paramref name="waiter"/> (the sample, when there is one) marks that waiter as sleeping until the wait's
    /// deadline; <see cref="ClearRetryWait"/> removes the mark when the retried call resolves.
    /// </summary>
    public static void RecordRetryWait(string model, double waitSeconds, double? now = null, object? waiter = null)
    {
        lock (Sync)
        {
            var (record, at) = Touch(model, now);
            record.RetryWaitSeconds += waitSeconds;
            var intervals = record.BackoffIntervalsList;
            intervals.Add(new BackoffInterval(at, at + waitSeconds));
            var cutoff = at - HorizonSeconds;
            if (intervals[0].End < cutoff || intervals.Count >= record.BackoffPruneAt)
            {
                intervals.RemoveAll(interval => interval.End < cutoff);
                record.BackoffPruneAt = Math.Max(2 * intervals.Count, BackoffPruneMin);
            }

            if (waiter is not null)
            {
                record.ActiveWaits[waiter] = at + waitSeconds;
            }
        }
    }

    /// <summary>Clears <paramref name="waiter"/>'s retry-wait mark (the whole retried call resolved).</summary>
    public static void ClearRetryWait(string model, object waiter)
    {
        ArgumentNullException.ThrowIfNull(waiter);
        lock (Sync)
        {
            Registry.GetValueOrDefault(model)?.ActiveWaits.Remove(waiter);
        }
    }

    /// <summary>Port of <c>throughput_snapshot</c>: every model's view over the trailing window (clamped to the horizon).</summary>
    public static IReadOnlyDictionary<string, ModelThroughputView> Snapshot(int window = DefaultWindowSeconds, double? now = null)
    {
        var at = now ?? Monotonic();
        var clamped = Math.Max(1, Math.Min(window, HorizonSeconds));
        lock (Sync)
        {
            return Registry.ToDictionary(pair => pair.Key, pair => ModelView(pair.Key, pair.Value, at, clamped), StringComparer.Ordinal);
        }
    }

    /// <summary>Port of <c>throughput_view</c>: one model's view, or null if it has no recorded activity.</summary>
    public static ModelThroughputView? View(string model, int window = DefaultWindowSeconds, double? now = null)
    {
        var at = now ?? Monotonic();
        lock (Sync)
        {
            return Registry.TryGetValue(model, out var record) ? ModelView(model, record, at, Math.Max(1, Math.Min(window, HorizonSeconds))) : null;
        }
    }

    /// <summary>
    /// Port of <c>throughput_report</c>: the <c>GET /models/throughput</c> envelope (snake_case keys). The envelope
    /// <c>window_seconds</c> is the requested (clamped) window; each row carries its effective window, further
    /// clamped to time since first activity.
    /// </summary>
    public static JsonObject Report(int window = DefaultWindowSeconds, double? now = null)
    {
        var clamped = Math.Max(1, Math.Min(window, HorizonSeconds));
        var models = new JsonArray();
        foreach (var view in Snapshot(clamped, now).OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value))
        {
            models.Add(new JsonObject
            {
                ["model"] = view.Model,
                ["window_seconds"] = Math.Round(view.WindowSeconds, 1),
                ["output_tokens_per_second"] = Math.Round(view.OutputTokensPerSecond, 1),
                ["requests_per_minute"] = Math.Round(view.RequestsPerMinute, 1),
                ["retries_per_minute"] = Math.Round(view.RetriesPerMinute, 1),
                ["backoff_ratio"] = Math.Round(view.BackoffRatio, 2),
                ["retry_waits_active"] = view.RetryWaitsActive,
                ["cumulative"] = new JsonObject
                {
                    ["requests"] = view.Requests,
                    ["output_tokens"] = view.OutputTokens,
                    ["total_tokens"] = view.TotalTokens,
                    ["retries"] = new JsonObject { ["rate_limit"] = view.RetriesRateLimit, ["transient"] = view.RetriesTransient },
                    ["retry_wait_seconds"] = Math.Round(view.RetryWaitSeconds, 1),
                    ["first_activity_at"] = Iso(view.FirstActivity),
                    ["last_activity_at"] = Iso(view.LastActivity),
                },
            });
        }

        return new JsonObject
        {
            ["as_of"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["window_seconds"] = clamped,
            ["models"] = models,
        };
    }

    /// <summary>
    /// Port of <c>throughput_footer_rate</c>: the aggregate output tok/s across models, or null until any retry has
    /// been recorded (a healthy run's footer doesn't gain a noisy number).
    /// </summary>
    public static double? FooterRate(int window = DefaultWindowSeconds, double? now = null)
    {
        lock (Sync)
        {
            if (!Registry.Values.Any(record => record.RetriesRateLimit > 0 || record.RetriesTransient > 0))
            {
                return null;
            }
        }

        return Snapshot(window, now).Values.Sum(view => view.OutputTokensPerSecond);
    }

    /// <summary>
    /// Port of <c>_window_backoff_seconds</c>: the sum of each interval's overlap with <c>[now - window, now]</c>.
    /// Only elapsed backoff counts, summed across concurrent generates (so the ratio can exceed 1.0).
    /// </summary>
    public static double WindowBackoffSeconds(IEnumerable<BackoffInterval> intervals, double now, double window)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        var lo = now - window;
        return intervals.Sum(interval => Math.Max(0.0, Math.Min(interval.End, now) - Math.Max(interval.Start, lo)));
    }

    // Under the lock.
    private static (ModelThroughput Record, double Now) Touch(string model, double? now)
    {
        ArgumentException.ThrowIfNullOrEmpty(model);
        var at = now ?? Monotonic();
        if (!Registry.TryGetValue(model, out var record))
        {
            record = new ModelThroughput();
            Registry[model] = record;
        }

        var wall = DateTimeOffset.UtcNow;
        if (record.FirstActivity is null)
        {
            record.FirstActivity = wall;
            record.FirstActivityMonotonic = at;
        }

        record.LastActivity = wall;
        return (record, at);
    }

    // Under the lock. The window is clamped to time since first activity so a fresh run isn't diluted.
    private static ModelThroughputView ModelView(string model, ModelThroughput record, double now, int window)
    {
        var effective = (double)window;
        if (record.FirstActivityMonotonic is { } first)
        {
            effective = Math.Min(effective, now - first);
        }

        effective = Math.Max(effective, 1.0);
        var sums = record.Buckets.WindowSums(now, effective);
        var backoff = WindowBackoffSeconds(record.BackoffIntervalsList, now, effective);
        var waitsActive = record.ActiveWaits.Values.Count(deadline => deadline > now);
        return new ModelThroughputView(
            model,
            effective,
            sums.OutputTokens / effective,
            sums.Requests * 60.0 / effective,
            sums.Retries * 60.0 / effective,
            backoff / effective,
            waitsActive,
            record.Requests,
            record.OutputTokens,
            record.TotalTokens,
            record.RetriesRateLimit,
            record.RetriesTransient,
            record.RetryWaitSeconds,
            record.FirstActivity,
            record.LastActivity);
    }

    private static string? Iso(DateTimeOffset? at) => at?.ToString("o", CultureInfo.InvariantCulture);
}

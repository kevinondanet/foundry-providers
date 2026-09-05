using System.Text.Json;
using System.Text.Json.Serialization;

namespace InspectAzureAI.Eval.Concurrency;

/// <summary>
/// Port of <c>LimitChangeReason</c>: why a controller limit changed — an adaptive-scaling decision
/// (<see cref="SlowStart"/> / <see cref="SteadyStateUp"/> / <see cref="RateLimit"/>) or a <see cref="Manual"/>
/// <c>SetMax</c> retune. Serialised as Python's snake_case literals.
/// </summary>
[JsonConverter(typeof(LimitChangeReasonJsonConverter))]
public enum LimitChangeReason
{
    SlowStart,
    SteadyStateUp,
    RateLimit,
    Manual,
}

/// <summary>The Python literal for a <see cref="LimitChangeReason"/>.</summary>
public static class LimitChangeReasonExtensions
{
    public static string ToPythonString(this LimitChangeReason reason) => reason switch
    {
        LimitChangeReason.SlowStart => "slow_start",
        LimitChangeReason.SteadyStateUp => "steady_state_up",
        LimitChangeReason.RateLimit => "rate_limit",
        LimitChangeReason.Manual => "manual",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown limit change reason."),
    };

    public static LimitChangeReason ParseLimitChangeReason(string value) => value switch
    {
        "slow_start" => LimitChangeReason.SlowStart,
        "steady_state_up" => LimitChangeReason.SteadyStateUp,
        "rate_limit" => LimitChangeReason.RateLimit,
        "manual" => LimitChangeReason.Manual,
        _ => throw new JsonException($"'{value}' is not a limit change reason (expected slow_start, steady_state_up, rate_limit or manual)."),
    };
}

/// <summary>Writes <see cref="LimitChangeReason"/> as Python's literal strings so the log matches <c>ConnectionLimitChange.reason</c>.</summary>
public sealed class LimitChangeReasonJsonConverter : JsonConverter<LimitChangeReason>
{
    public override LimitChangeReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? LimitChangeReasonExtensions.ParseLimitChangeReason(reader.GetString()!)
            : throw new JsonException($"Expected a string for a limit change reason, got {reader.TokenType}.");

    public override void Write(Utf8JsonWriter writer, LimitChangeReason value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToPythonString());
}

/// <summary>
/// Port of <c>LimitChangeRecord</c> / the log's <c>ConnectionLimitChange</c>: one scale change. <see cref="Model"/>
/// is the controller's display name, never the (possibly secret-bearing) connection key; <see cref="Timestamp"/>
/// is Unix seconds, as <c>time.time()</c> reports.
/// </summary>
public sealed record LimitChangeRecord(double Timestamp, string Model, int OldLimit, int NewLimit, LimitChangeReason Reason);

/// <summary>
/// Port of <c>AdaptiveConcurrencyController</c>: slow start + AIMD driven only by feedback that says something
/// about the rate limit. Until the first rate-limit signal each clean <em>round</em> (<c>max(limit, 4)</c>
/// successes with the limit at least 80% saturated) doubles the limit; afterwards a clean round adds
/// <c>max(1, round(limit * ScaleUpPercent))</c> rounded up to a nice number; a rate-limit retry multiplies by
/// <c>DecreaseFactor</c> rounded down to a nice number and floored at <c>Min</c>, debounced to one cut per
/// <c>CooldownSeconds</c>. Transient retries only mark the request so its eventual success is neutral. Every
/// change is appended to a bounded <see cref="History"/>. Python's controller runs on one event-loop thread; here
/// the accounting is under a lock because generates complete on the thread pool.
/// </summary>
public sealed class AdaptiveConcurrencyController : IConcurrencySemaphore
{
    public const int RoundSizeFloor = 4;

    public const int HistoryLimit = 200;

    /// <summary>Minimum peak-in-flight / limit ratio within a round for the round to count toward growth.</summary>
    public const double SaturationThreshold = 0.8;

    private readonly object _sync = new();

    private readonly ResizableLimiter _limiter;

    private readonly TimeProvider _time;

    private readonly int _configuredMin;

    private readonly List<LimitChangeRecord> _history = [];

    private readonly List<Action> _observers = [];

    private AdaptiveConcurrency _config;

    private int _successCount;

    private int _maxBorrowedThisRound;

    private bool _firstRetrySeen;

    private double _cooldownUntil;

    /// <summary>
    /// Creates a controller starting at <c>config.Start</c>. The config is copied by value (a record), so a
    /// <see cref="SetMax"/> retune of one controller can't leak into another built from the same instance.
    /// </summary>
    public AdaptiveConcurrencyController(string name, AdaptiveConcurrency config, bool visible = true, string? key = null, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();
        Name = name;
        Key = key ?? name;
        Visible = visible;
        _config = config;
        _configuredMin = config.Min;
        _limiter = new ResizableLimiter(config.Start);
        _time = timeProvider ?? TimeProvider.System;
    }

    public string Name { get; }

    /// <summary>The registry key (the model's connection-pool key); the identity a <see cref="DynamicSampleLimiter"/> follows.</summary>
    public string Key { get; }

    public bool Visible { get; }

    /// <summary>The live limit (the limiter's capacity, kept in sync by every scale change).</summary>
    public int Concurrency => _limiter.Limit;

    /// <summary>Free capacity clamped to >= 0 (a cut may lower the limit below the in-flight count).</summary>
    public int Value => _limiter.Available;

    /// <summary>Exact in-flight count, read from the limiter rather than derived so it stays true after a cut below in-flight.</summary>
    public int InUse => _limiter.InUse;

    /// <summary>The controller's lower scaling bound.</summary>
    public int Min
    {
        get
        {
            lock (_sync)
            {
                return _config.Min;
            }
        }
    }

    /// <summary>The controller's upper scaling bound.</summary>
    public int Max
    {
        get
        {
            lock (_sync)
            {
                return _config.Max;
            }
        }
    }

    /// <summary>Bounded history of scale changes (oldest first) for eval log capture.</summary>
    public IReadOnlyList<LimitChangeRecord> History
    {
        get
        {
            lock (_sync)
            {
                return _history.ToArray();
            }
        }
    }

    /// <summary>True while a post-cut cooldown suppresses success accounting and saturation sampling.</summary>
    public bool InCooldown
    {
        get
        {
            lock (_sync)
            {
                return Now() < _cooldownUntil;
            }
        }
    }

    internal int SuccessCount
    {
        get
        {
            lock (_sync)
            {
                return _successCount;
            }
        }
    }

    internal int MaxBorrowedThisRound
    {
        get
        {
            lock (_sync)
            {
                return _maxBorrowedThisRound;
            }
        }

        set
        {
            lock (_sync)
            {
                _maxBorrowedThisRound = value;
            }
        }
    }

    internal bool FirstRetrySeen
    {
        set
        {
            lock (_sync)
            {
                _firstRetrySeen = value;
            }
        }
    }

    internal double CooldownUntil
    {
        get
        {
            lock (_sync)
            {
                return _cooldownUntil;
            }
        }

        set
        {
            lock (_sync)
            {
                _cooldownUntil = value;
            }
        }
    }

    /// <summary>The controller's monotonic clock in seconds (for tests that reason about the cooldown horizon).</summary>
    public double Now() => _time.GetTimestamp() / (double)_time.TimestampFrequency;

    /// <summary>Smallest 'nice' integer >= value: multiples of 5 above 10, integers below (port of <c>_ceil_to_nice</c>).</summary>
    public static int CeilToNice(int value) => value < 10 ? Math.Max(1, value) : (value + 4) / 5 * 5;

    /// <summary>Largest 'nice' integer &lt;= value: multiples of 5 above 10, integers below (port of <c>_floor_to_nice</c>).</summary>
    public static int FloorToNice(int value) => value < 10 ? Math.Max(1, value) : value / 5 * 5;

    /// <summary>
    /// Port of <c>_SaturationTrackingLimiter.__aenter__</c>: takes a slot, then records the in-flight high-water
    /// mark on acquire (recording on release would undercount by the just-returned slot). Acquires during a
    /// post-cut cooldown are pre-cut traffic and don't update the mark.
    /// </summary>
    public async ValueTask<ConcurrencyLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        var lease = await _limiter.AcquireAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            if (Now() >= _cooldownUntil)
            {
                var borrowed = _limiter.InUse;
                if (borrowed > _maxBorrowedThisRound)
                {
                    _maxBorrowedThisRound = borrowed;
                }
            }
        }

        return lease;
    }

    /// <summary>Registers a callback fired after each scale change (observers read the controller's live state).</summary>
    public void AddObserver(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_sync)
        {
            _observers.Add(callback);
        }
    }

    /// <summary>
    /// Records a successful logical request that had no retries. Successes during the post-cut cooldown are
    /// discarded entirely (they were in flight before the cut); a completed round grows the limit only if it
    /// observed real saturation.
    /// </summary>
    public void NotifySuccess()
    {
        var changed = false;
        lock (_sync)
        {
            if (Now() < _cooldownUntil)
            {
                return;
            }

            _successCount++;
            var old = _limiter.Limit;
            var roundSize = Math.Max(old, RoundSizeFloor);
            if (_successCount < roundSize)
            {
                return;
            }

            var peakBorrowed = _maxBorrowedThisRound;
            _maxBorrowedThisRound = 0;
            _successCount = 0;
            if (old > 0 && peakBorrowed < SaturationThreshold * old)
            {
                return;
            }

            int target;
            LimitChangeReason reason;
            if (!_firstRetrySeen)
            {
                target = Math.Min(old * 2, _config.Max);
                reason = LimitChangeReason.SlowStart;
            }
            else
            {
                // Math.Round defaults to banker's rounding, as Python's round() does.
                var increment = Math.Max(1, (int)Math.Round(old * _config.ScaleUpPercent));
                target = Math.Min(CeilToNice(old + increment), _config.Max);
                reason = LimitChangeReason.SteadyStateUp;
            }

            if (target != old)
            {
                SetLimit(target, reason);
                changed = true;
            }
        }

        if (changed)
        {
            NotifyObservers();
        }
    }

    /// <summary>
    /// Records a rate-limit retry signal. Success accounting and the saturation mark reset on every call (even a
    /// debounced one); the cut itself lands at most once per <c>CooldownSeconds</c>. <paramref name="retryAfter"/>
    /// is accepted but deliberately never paces the cooldown — a server-chosen horizon would invert the loop.
    /// </summary>
    public void NotifyRetry(double? retryAfter = null)
    {
        _ = retryAfter;
        var changed = false;
        lock (_sync)
        {
            _successCount = 0;
            _maxBorrowedThisRound = 0;
            _firstRetrySeen = true;
            var now = Now();
            if (now < _cooldownUntil)
            {
                return;
            }

            var old = _limiter.Limit;
            var target = (int)(old * _config.DecreaseFactor);
            var cut = Math.Max(FloorToNice(target), Math.Max(_config.Min, 1));
            _cooldownUntil = now + _config.CooldownSeconds;
            if (cut != old)
            {
                SetLimit(cut, LimitChangeReason.RateLimit);
                changed = true;
            }
        }

        if (changed)
        {
            NotifyObservers();
        }
    }

    /// <summary>
    /// Retunes the scaling ceiling mid-flight. Lowering below the current limit clamps it down immediately (a
    /// <see cref="LimitChangeReason.Manual"/> entry; in-flight holders drain, never preempted); raising only lifts
    /// the ceiling. <c>Min</c> follows as <c>min(configured min, new max)</c> so a temporary throttle never
    /// permanently weakens the floor.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="newMax"/> is below 1 (validated before any state changes).</exception>
    public void SetMax(int newMax)
    {
        if (newMax < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(newMax), newMax, $"connection limit ceiling must be >= 1 (got {newMax})");
        }

        var changed = false;
        lock (_sync)
        {
            _config = _config with { Max = newMax, Min = Math.Min(_configuredMin, newMax) };
            if (_limiter.Limit > newMax)
            {
                SetLimit(newMax, LimitChangeReason.Manual);
                changed = true;
            }
        }

        if (changed)
        {
            NotifyObservers();
        }
    }

    // Under the lock; the caller notifies observers after releasing it.
    private void SetLimit(int target, LimitChangeReason reason)
    {
        var old = _limiter.Limit;
        _limiter.Limit = target;
        _history.Add(new LimitChangeRecord(_time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0, Name, old, target, reason));
        if (_history.Count > HistoryLimit)
        {
            _history.RemoveAt(0);
        }
    }

    private void NotifyObservers()
    {
        Action[] observers;
        lock (_sync)
        {
            observers = _observers.ToArray();
        }

        foreach (var observer in observers)
        {
            observer();
        }
    }
}

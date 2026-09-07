using System.Text.Json.Nodes;
using Azure;
using InspectAzureAI.Eval.Concurrency;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Testing;

namespace InspectAzureAI.Eval.Tests;

using Concurrency = InspectAzureAI.Eval.Concurrency.Concurrency;
using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;
using Scorers = InspectAzureAI.Eval.Scorers.Scorers;

/// <summary>
/// Port-level behaviour of <c>util/_concurrency.py</c>, <c>model/_throughput.py</c>, the connection concurrency of
/// <c>model/_model.py</c> and <c>create_sample_semaphore</c> (<c>_eval/task/run.py</c>), mirroring the cases of
/// <c>tests/util/test_concurrency.py</c>, <c>test_adaptive_concurrency.py</c>, <c>tests/model/test_model_throughput.py</c>
/// and <c>test_adaptive_connections.py</c>. The registries are process-global, so every test starts from a reset.
/// </summary>
public sealed class ConcurrencyTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public ConcurrencyTests()
    {
        Concurrency.Init();
        Throughput.Init();
    }

    public void Dispose()
    {
        Concurrency.Init();
        Throughput.Init();
    }

    private sealed class FakeTime : TimeProvider
    {
        private double _seconds = 1_000_000;

        public override long TimestampFrequency => 1_000_000;

        public override long GetTimestamp() => (long)(_seconds * TimestampFrequency);

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(_seconds);

        public void Advance(double seconds) => _seconds += seconds;
    }

    private sealed class RecordingReporter : IEvalReporter
    {
        public List<EvalRunStats> Stats { get; } = [];

        public void SampleStarted(object id, int epoch)
        {
        }

        public void SampleCompleted(EvalSample sample)
        {
        }

        public void Message(string text)
        {
        }

        void IEvalReporter.Stats(EvalRunStats stats) => Stats.Add(stats);
    }

    private static AdaptiveConcurrencyController Controller(int min = 1, int max = 200, int start = 10, double cooldown = 15.0, double decrease = 0.8, double scaleUp = 0.05, TimeProvider? time = null) =>
        new("t", AdaptiveConcurrency.Create(min: min, start: start, max: max, cooldownSeconds: cooldown, decreaseFactor: decrease, scaleUpPercent: scaleUp), visible: true, timeProvider: time);

    /// <summary>Port of the <c>_saturated_successes</c> helper: successes with the limit fully saturated (one round by default).</summary>
    private static void SaturatedSuccesses(AdaptiveConcurrencyController controller, int? count = null)
    {
        var n = count ?? Math.Max(controller.Concurrency, AdaptiveConcurrencyController.RoundSizeFloor);
        for (var i = 0; i < n; i++)
        {
            controller.MaxBorrowedThisRound = controller.Concurrency;
            controller.NotifySuccess();
        }
    }

    private static RequestFailedException Http(int status, IReadOnlyDictionary<string, string>? headers = null) =>
        new(CannedResponse.Error(status, $"http {status}", headers));

    private static Task NoDelay(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;

    private static ModelUsage Usage(int outputTokens = 10, int totalTokens = 30) => new(totalTokens - outputTokens, outputTokens, totalTokens);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(5);
        }
    }

    // ---------------------------------------------------------------- ResizableLimiter

    [Fact]
    public void resizable_limiter_reports_and_resizes_its_limit()
    {
        var limiter = new ResizableLimiter(5);
        Assert.Equal(5, limiter.Limit);
        Assert.Equal(0, limiter.InUse);
        Assert.Equal(5, limiter.Available);

        limiter.Limit = 8;

        Assert.Equal(8, limiter.Limit);
        Assert.Equal(8, limiter.Available);
    }

    [Fact]
    public async Task resizable_limiter_tracks_leases_and_releases_once()
    {
        var limiter = new ResizableLimiter(3);
        ConcurrencyLease lease;
        await using (lease = await limiter.AcquireAsync())
        {
            Assert.Equal(1, limiter.InUse);
            Assert.Equal(2, limiter.Available);
            Assert.False(lease.Released);
        }

        Assert.True(lease.Released);
        Assert.Equal(0, limiter.InUse);
        lease.Release();
        lease.Dispose();
        Assert.Equal(0, limiter.InUse);
    }

    [Fact]
    public void resizable_limiter_rejects_non_positive_limits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResizableLimiter(0));
        var limiter = new ResizableLimiter(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => limiter.Limit = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => limiter.Limit = -3);
        Assert.Equal(2, limiter.Limit);
    }

    [Fact]
    public async Task resize_shrinks_and_grows_live()
    {
        var limiter = new ResizableLimiter(3);
        var a = await limiter.AcquireAsync();
        var b = await limiter.AcquireAsync();

        // lowering below in-use never preempts: the holders keep running and available clamps to 0
        limiter.Limit = 1;
        Assert.Equal(1, limiter.Limit);
        Assert.Equal(2, limiter.InUse);
        Assert.Equal(0, limiter.Available);

        var pending = limiter.AcquireAsync().AsTask();
        await Task.Delay(50);
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, limiter.Waiting);

        a.Release();
        await Task.Delay(50);
        Assert.False(pending.IsCompleted);

        b.Release();
        var c = await pending.WaitAsync(Timeout);
        Assert.Equal(1, limiter.InUse);
        Assert.Equal(0, limiter.Waiting);

        var pending2 = limiter.AcquireAsync().AsTask();
        await Task.Delay(50);
        Assert.False(pending2.IsCompleted);

        // growing grants the waiter at once
        limiter.Limit = 2;
        var d = await pending2.WaitAsync(Timeout);
        Assert.Equal(2, limiter.InUse);

        c.Release();
        d.Release();
        Assert.Equal(0, limiter.InUse);
    }

    [Fact]
    public async Task cancelling_a_pending_acquire_leaves_the_queue_without_taking_a_slot()
    {
        var limiter = new ResizableLimiter(1);
        var held = await limiter.AcquireAsync();
        using var cts = new CancellationTokenSource();
        var cancelled = limiter.AcquireAsync(cts.Token).AsTask();
        var next = limiter.AcquireAsync().AsTask();
        Assert.Equal(2, limiter.Waiting);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(1, limiter.Waiting);
        Assert.Equal(1, limiter.InUse);

        held.Release();
        var lease = await next.WaitAsync(Timeout);
        Assert.Equal(1, limiter.InUse);
        lease.Release();
        Assert.Equal(0, limiter.InUse);

        // an already-cancelled token never takes a slot, even a free one
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await limiter.AcquireAsync(new CancellationToken(canceled: true)));
        Assert.Equal(0, limiter.InUse);
    }

    // ---------------------------------------------------------------- registry

    [Theory]
    [InlineData(1, 5, 1)]
    [InlineData(2, 10, 2)]
    [InlineData(4, 4, 4)]
    [InlineData(8, 3, 3)]
    public async Task concurrency_limits_are_enforced_under_load(int limit, int tasks, int expectedMax)
    {
        var peak = 0;
        var entered = 0;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runs = Enumerable.Range(0, tasks).Select(async _ =>
        {
            await using var lease = await Concurrency.AcquireAsync("test-resource", limit);
            var inUse = Concurrency.StatusDisplay()["test-resource"].InUse;
            int seen;
            while ((seen = Volatile.Read(ref peak)) < inUse && Interlocked.CompareExchange(ref peak, inUse, seen) != seen)
            {
            }

            if (Interlocked.Increment(ref entered) >= expectedMax)
            {
                barrier.TrySetResult();
            }

            await barrier.Task;
        }).ToArray();

        await Task.WhenAll(runs).WaitAsync(Timeout);

        Assert.Equal(expectedMax, peak);
        Assert.Equal(new ConcurrencyStatus(0, limit), Concurrency.StatusDisplay()["test-resource"]);
    }

    [Fact]
    public async Task keying_isolates_limits_and_the_same_key_shares_one()
    {
        await using var a = await Concurrency.AcquireAsync("resource-a", 1);
        await using var b = await Concurrency.AcquireAsync("resource-b", 1);
        var status = Concurrency.StatusDisplay();
        Assert.Equal(new ConcurrencyStatus(1, 1), status["resource-a"]);
        Assert.Equal(new ConcurrencyStatus(1, 1), status["resource-b"]);

        // the key defaults to the name; nested acquires of one context take one slot each
        await using var c = await Concurrency.AcquireAsync("shared", 2);
        Assert.Equal(new ConcurrencyStatus(1, 2), Concurrency.StatusDisplay()["shared"]);
        await using var d = await Concurrency.AcquireAsync("shared", 2);
        Assert.Equal(new ConcurrencyStatus(2, 2), Concurrency.StatusDisplay()["shared"]);

        // an explicit key is the identity: two names on one key coalesce (first-created bounds win), one name on two keys doesn't
        var k1 = Concurrency.GetOrCreateSemaphore("name-1", 3, key: "key-1");
        Assert.Same(k1, Concurrency.GetOrCreateSemaphore("name-other", 9, key: "key-1"));
        Assert.Equal(3, k1.Concurrency);
        Assert.Equal("key-1", k1.Key);
        Assert.NotSame(k1, Concurrency.GetOrCreateSemaphore("name-1", 3, key: "key-2"));
        Assert.Contains("name-1", Concurrency.StatusDisplay().Keys);
    }

    [Fact]
    public void status_display_shortens_a_unique_prefix_and_hides_invisible_entries()
    {
        Concurrency.GetOrCreateSemaphore("openai/gpt-4o", 5);
        Concurrency.GetOrCreateSemaphore("hidden", 1, visible: false);

        var status = Concurrency.StatusDisplay();

        Assert.Equal(new ConcurrencyStatus(0, 5), status["openai"]);
        Assert.DoesNotContain("hidden", status.Keys);
        Assert.Contains(Concurrency.Semaphores(), s => s.Name == "hidden");

        Concurrency.GetOrCreateSemaphore("openai/gpt-4o-mini", 3);
        status = Concurrency.StatusDisplay();

        Assert.Equal(new ConcurrencyStatus(0, 5), status["openai/gpt-4o"]);
        Assert.Equal(new ConcurrencyStatus(0, 3), status["openai/gpt-4o-mini"]);
    }

    [Fact]
    public void registry_separates_adaptive_and_static_entries_and_init_clears_everything()
    {
        var fixedLimit = Concurrency.GetOrCreateSemaphore("m", 5, key: "k");
        var adaptive = Concurrency.GetOrCreateSemaphore("m", 5, key: "k", adaptive: AdaptiveConcurrency.Create(min: 1, start: 10, max: 50));

        var resizable = Assert.IsType<ResizableSemaphore>(fixedLimit);
        Assert.Equal(5, resizable.Concurrency);
        var controller = Assert.IsType<AdaptiveConcurrencyController>(adaptive);
        Assert.Equal(10, controller.Concurrency);
        Assert.Equal("k", controller.Key);
        Assert.Equal("m", controller.Name);
        Assert.Same(fixedLimit, Concurrency.GetOrCreateSemaphore("m", 99, key: "k"));
        Assert.Same(adaptive, Concurrency.GetOrCreateSemaphore("m", 99, key: "k", adaptive: new AdaptiveConcurrency()));
        Assert.Equal([controller], Concurrency.AdaptiveControllers());

        var created = new List<AdaptiveConcurrencyController>();
        Concurrency.AddControllerCreatedObserver(created.Add);
        Concurrency.GetOrCreateSemaphore("model-y", 10, key: "ky", adaptive: new AdaptiveConcurrency());
        Assert.Equal("model-y", Assert.Single(created).Name);

        Concurrency.Init();

        Assert.Empty(Concurrency.Semaphores());
        Assert.Empty(Concurrency.AdaptiveControllers());
        Concurrency.GetOrCreateSemaphore("model-z", 10, key: "kz", adaptive: new AdaptiveConcurrency());
        Assert.Single(created);
    }

    [Fact]
    public void a_static_limit_below_one_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Concurrency.GetOrCreateSemaphore("zero", 0));
        Assert.Empty(Concurrency.Semaphores());
    }

    [Fact]
    public async Task a_lease_is_released_when_the_holder_throws()
    {
        var semaphore = Concurrency.GetOrCreateSemaphore("throws", 1);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var lease = await Concurrency.AcquireAsync("throws", 1);
            Assert.Equal(1, semaphore.InUse);
            throw new InvalidOperationException("boom");
        });

        Assert.Equal(0, semaphore.InUse);
        Assert.Equal(1, semaphore.Value);
    }

    // ---------------------------------------------------------------- adaptive controller

    [Theory]
    [InlineData(1, 1)]
    [InlineData(9, 9)]
    [InlineData(10, 10)]
    [InlineData(11, 15)]
    [InlineData(15, 15)]
    [InlineData(16, 20)]
    [InlineData(64, 65)]
    [InlineData(100, 100)]
    [InlineData(101, 105)]
    public void ceil_to_nice(int value, int expected) => Assert.Equal(expected, AdaptiveConcurrencyController.CeilToNice(value));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(9, 9)]
    [InlineData(10, 10)]
    [InlineData(14, 10)]
    [InlineData(15, 15)]
    [InlineData(64, 60)]
    [InlineData(100, 100)]
    public void floor_to_nice(int value, int expected) => Assert.Equal(expected, AdaptiveConcurrencyController.FloorToNice(value));

    [Fact]
    public void controller_initial_state()
    {
        var c = Controller(min: 2, max: 80, start: 10);
        Assert.Equal(10, c.Concurrency);
        Assert.Equal(10, c.Value);
        Assert.Equal(0, c.InUse);
        Assert.Equal(2, c.Min);
        Assert.Equal(80, c.Max);
        Assert.Empty(c.History);
        Assert.False(c.InCooldown);
    }

    [Fact]
    public void slow_start_doubles_until_first_retry()
    {
        var c = Controller(start: 10);
        SaturatedSuccesses(c, 10);
        Assert.Equal(20, c.Concurrency);
        SaturatedSuccesses(c, 20);
        Assert.Equal(40, c.Concurrency);
        SaturatedSuccesses(c, 40);
        Assert.Equal(80, c.Concurrency);
        Assert.All(c.History, entry => Assert.Equal(LimitChangeReason.SlowStart, entry.Reason));
        Assert.Equal((10, 20), (c.History[0].OldLimit, c.History[0].NewLimit));
        Assert.Equal("t", c.History[0].Model);
    }

    [Fact]
    public void aimd_after_first_retry()
    {
        var time = new FakeTime();
        var c = Controller(start: 40, time: time);

        c.NotifyRetry();
        Assert.Equal(30, c.Concurrency);
        Assert.Equal(LimitChangeReason.RateLimit, c.History[^1].Reason);
        Assert.True(c.InCooldown);

        // an immediate further retry is debounced
        c.NotifyRetry();
        Assert.Equal(30, c.Concurrency);

        time.Advance(16);
        Assert.False(c.InCooldown);
        // round_size = 30; +max(1, round(30 * 0.05) = 2) = 32 -> ceil_to_nice = 35
        SaturatedSuccesses(c, 30);
        Assert.Equal(35, c.Concurrency);
        Assert.Equal(LimitChangeReason.SteadyStateUp, c.History[^1].Reason);
    }

    [Fact]
    public void no_success_accounting_during_cooldown()
    {
        var time = new FakeTime();
        var c = Controller(start: 100, time: time);
        c.NotifyRetry();
        Assert.Equal(80, c.Concurrency);

        for (var i = 0; i < 80; i++)
        {
            c.NotifySuccess();
        }

        Assert.Equal(0, c.SuccessCount);
        Assert.Equal(80, c.Concurrency);

        c.NotifyRetry();
        Assert.Equal(80, c.Concurrency);
        Assert.Equal(0, c.SuccessCount);

        time.Advance(16);
        SaturatedSuccesses(c, 80);
        Assert.Equal(85, c.Concurrency);
    }

    [Fact]
    public void retry_debounce_via_cooldown()
    {
        var time = new FakeTime();
        var c = Controller(start: 80, time: time);
        c.NotifyRetry();
        var first = c.Concurrency;
        for (var i = 0; i < 5; i++)
        {
            c.NotifyRetry();
        }

        Assert.Equal(first, c.Concurrency);
        time.Advance(16);
        c.NotifyRetry();
        Assert.True(c.Concurrency < first);
    }

    [Fact]
    public void bounds_clamp_slow_start_and_cuts()
    {
        var c = Controller(min: 1, max: 15, start: 10);
        SaturatedSuccesses(c, 10);
        Assert.Equal(15, c.Concurrency);

        var c2 = Controller(min: 8, max: 200, start: 10);
        c2.NotifyRetry();
        Assert.Equal(8, c2.Concurrency);
    }

    [Fact]
    public void steady_state_up_does_not_exceed_max()
    {
        var c = Controller(min: 1, max: 20, start: 20);
        c.FirstRetrySeen = true;
        SaturatedSuccesses(c, 20);
        Assert.Equal(20, c.Concurrency);
    }

    [Fact]
    public void round_size_floor_at_low_limits()
    {
        var c = Controller(start: 2);
        SaturatedSuccesses(c, 2);
        Assert.Equal(2, c.Concurrency);
        SaturatedSuccesses(c, 2);
        Assert.Equal(4, c.Concurrency);
    }

    [Fact]
    public void history_is_bounded()
    {
        var c = Controller(min: 1, max: 1000, start: 1000);
        for (var i = 1; i <= AdaptiveConcurrencyController.HistoryLimit + 50; i++)
        {
            c.SetMax(1000 - i);
        }

        Assert.Equal(AdaptiveConcurrencyController.HistoryLimit, c.History.Count);
        Assert.All(c.History, entry => Assert.Equal(LimitChangeReason.Manual, entry.Reason));
        Assert.Equal(1000 - (AdaptiveConcurrencyController.HistoryLimit + 50), c.History[^1].NewLimit);
    }

    [Fact]
    public void growth_is_gated_on_saturation()
    {
        // 50% saturation (peak 5 of limit 10) blocks growth and resets the mark for the next round
        var c = Controller(start: 10);
        c.MaxBorrowedThisRound = 5;
        for (var i = 0; i < 10; i++)
        {
            c.NotifySuccess();
        }

        Assert.Equal(10, c.Concurrency);
        Assert.Empty(c.History);
        Assert.Equal(0, c.MaxBorrowedThisRound);

        // exactly the threshold (8 of 10) still permits scale-up
        c.MaxBorrowedThisRound = 8;
        for (var i = 0; i < 10; i++)
        {
            c.NotifySuccess();
        }

        Assert.Equal(20, c.Concurrency);
        Assert.Equal(LimitChangeReason.SlowStart, c.History[^1].Reason);

        // a workload too small to fill `start` never grows
        var small = Controller(start: 20);
        for (var round = 0; round < 5; round++)
        {
            small.MaxBorrowedThisRound = 3;
            for (var i = 0; i < 20; i++)
            {
                small.NotifySuccess();
            }
        }

        Assert.Equal(20, small.Concurrency);
        Assert.Empty(small.History);

        // a rate-limit cut clears the high-water mark
        var cut = Controller(start: 40);
        cut.MaxBorrowedThisRound = 35;
        cut.NotifyRetry();
        Assert.Equal(0, cut.MaxBorrowedThisRound);
    }

    [Fact]
    public async Task saturation_at_low_limits_is_sampled_on_acquire()
    {
        var c = Controller(start: 4);
        var allIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;

        var holders = Enumerable.Range(0, 4).Select(async _ =>
        {
            await using (await c.AcquireAsync())
            {
                if (Interlocked.Increment(ref entered) == 4)
                {
                    allIn.TrySetResult();
                }

                await release.Task;
            }

            c.NotifySuccess();
        }).ToArray();

        await allIn.Task.WaitAsync(Timeout);
        Assert.Equal(4, c.InUse);
        Assert.Equal(0, c.Value);
        release.SetResult();
        await Task.WhenAll(holders).WaitAsync(Timeout);

        // ROUND_SIZE_FLOOR=4: four successes at peak 4/4 scale up via slow start
        Assert.Equal(8, c.Concurrency);
        Assert.Equal(LimitChangeReason.SlowStart, c.History[^1].Reason);
    }

    [Fact]
    public async Task serial_workload_at_low_limit_does_not_grow()
    {
        var c = Controller(start: 10);
        for (var i = 0; i < 10; i++)
        {
            await using (await c.AcquireAsync())
            {
            }

            c.NotifySuccess();
        }

        Assert.Equal(10, c.Concurrency);
        Assert.Empty(c.History);
    }

    [Fact]
    public void config_defaults_and_implicit_clamping()
    {
        var cfg = new AdaptiveConcurrency();
        Assert.Equal((10, 20, 100), (cfg.Min, cfg.Start, cfg.Max));
        Assert.Equal(15.0, cfg.CooldownSeconds);
        Assert.Equal(0.8, cfg.DecreaseFactor);
        Assert.Equal(0.05, cfg.ScaleUpPercent);

        var small = AdaptiveConcurrency.Create(max: 8);
        Assert.Equal((8, 8, 8), (small.Min, small.Start, small.Max));

        var wide = AdaptiveConcurrency.Create(min: 1, max: 15);
        Assert.Equal((1, 15, 15), (wide.Min, wide.Start, wide.Max));
    }

    [Fact]
    public void config_validation_rejects_out_of_range_fields()
    {
        Assert.Contains("cooldown_seconds", Assert.Throws<ArgumentOutOfRangeException>(() => AdaptiveConcurrency.Create(cooldownSeconds: -1)).Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptiveConcurrency.Create(cooldownSeconds: double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptiveConcurrency.Create(cooldownSeconds: double.NaN));
        Assert.Equal(0, AdaptiveConcurrency.Create(cooldownSeconds: 0).CooldownSeconds);
        Assert.Contains("decrease_factor", Assert.Throws<ArgumentOutOfRangeException>(() => AdaptiveConcurrency.Create(decreaseFactor: 0)).Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptiveConcurrency.Create(decreaseFactor: 1));
        Assert.Contains("scale_up_percent", Assert.Throws<ArgumentOutOfRangeException>(() => AdaptiveConcurrency.Create(scaleUpPercent: 0)).Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptiveConcurrency.Create(scaleUpPercent: 1.5));
        Assert.Contains("min must be >= 1", Assert.Throws<ArgumentOutOfRangeException>(() => AdaptiveConcurrency.Create(min: 0)).Message);
        Assert.Contains("must be >= min", Assert.Throws<ArgumentOutOfRangeException>(() => AdaptiveConcurrency.Create(min: 5, max: 4)).Message);
        Assert.Contains("start", Assert.Throws<ArgumentOutOfRangeException>(() => AdaptiveConcurrency.Create(min: 5, start: 50, max: 40)).Message);

        // an object-initialised instance is rejected by every consumer rather than coerced
        var invalid = new AdaptiveConcurrency { Min = 0 };
        Assert.Throws<ArgumentOutOfRangeException>(() => invalid.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveConcurrencyController("t", invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DynamicSampleLimiter(invalid, "k"));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptiveConnections.From(invalid));
    }

    [Fact]
    public void shorthand_and_setting_forms_parse_like_the_cli()
    {
        Assert.Equal((4, 20, 80), Triple(AdaptiveConcurrency.Parse("4-80")));
        Assert.Equal((4, 20, 80), Triple(AdaptiveConcurrency.Parse("4-20-80")));
        Assert.Throws<FormatException>(() => AdaptiveConcurrency.Parse("1-2-3-4"));
        Assert.Throws<FormatException>(() => AdaptiveConcurrency.Parse("not-a-number"));

        foreach (var value in new[] { "true", "TRUE", "yes" })
        {
            var setting = AdaptiveConnections.Parse(value);
            Assert.True(setting.Enabled);
            Assert.Null(setting.Config);
            Assert.Equal(new AdaptiveConcurrency(), setting.Resolve());
        }

        foreach (var value in new[] { "false", "no" })
        {
            Assert.Same(AdaptiveConnections.Disabled, AdaptiveConnections.Parse(value));
        }

        Assert.Equal((10, 20, 200), Triple(AdaptiveConnections.Parse("200").Resolve()));
        Assert.Equal((10, 20, 50), Triple(AdaptiveConnections.Parse("50").Resolve()));
        Assert.Equal((1, 1, 1), Triple(AdaptiveConnections.Parse("1").Resolve()));
        Assert.Equal((4, 20, 80), Triple(AdaptiveConnections.Parse("4-80").Resolve()));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptiveConnections.Parse("0"));
        Assert.Throws<FormatException>(() => AdaptiveConnections.Parse("nope"));
        Assert.Throws<FormatException>(() => AdaptiveConnections.Parse("1-2-3-4"));
        Assert.Throws<InvalidOperationException>(() => AdaptiveConnections.Disabled.Resolve());
        Assert.Equal((10, 20, 100), Triple(AdaptiveConnections.WithMax(100).Resolve()));

        static (int, int, int) Triple(AdaptiveConcurrency c) => (c.Min, c.Start, c.Max);
    }

    [Fact]
    public void generate_config_adaptive_connections_values_map_to_the_setting()
    {
        Assert.Null(AdaptiveConnections.FromConfigValue(null));
        Assert.Same(AdaptiveConnections.Default, AdaptiveConnections.FromConfigValue(true));
        Assert.Same(AdaptiveConnections.Disabled, AdaptiveConnections.FromConfigValue(false));
        Assert.Same(AdaptiveConnections.Disabled, AdaptiveConnections.FromConfigValue(AdaptiveConnections.Disabled));
        Assert.Equal((10, 20, 50), Triple(AdaptiveConnections.FromConfigValue(50)!.Resolve()));
        Assert.Equal((4, 20, 80), Triple(AdaptiveConnections.FromConfigValue("4-80")!.Resolve()));
        Assert.Equal((4, 20, 80), Triple(AdaptiveConnections.FromConfigValue(AdaptiveConcurrency.Parse("4-80"))!.Resolve()));
        Assert.Throws<ArgumentException>(() => AdaptiveConnections.FromConfigValue(1.5));
        Assert.Throws<FormatException>(() => AdaptiveConnections.FromConfigValue("nope"));

        static (int, int, int) Triple(AdaptiveConcurrency c) => (c.Min, c.Start, c.Max);
    }

    [Fact]
    public void advanced_fields_override_controller_behaviour()
    {
        var c = Controller(start: 40, decrease: 0.5);
        c.NotifyRetry();
        Assert.Equal(20, c.Concurrency);

        var c2 = Controller(start: 20, scaleUp: 0.5);
        c2.FirstRetrySeen = true;
        SaturatedSuccesses(c2, 20);
        Assert.Equal(30, c2.Concurrency);
    }

    [Fact]
    public void cooldown_debounce_lasts_exactly_cooldown_seconds_whatever_the_server_suggests()
    {
        var time = new FakeTime();
        var c = Controller(start: 40, cooldown: 15.0, time: time);
        c.NotifyRetry(retryAfter: 60.0);
        var cut = c.Concurrency;
        time.Advance(15.0 - 0.1);
        c.NotifyRetry(retryAfter: 60.0);
        Assert.Equal(cut, c.Concurrency);
        time.Advance(0.2);
        c.NotifyRetry(retryAfter: 60.0);
        Assert.True(c.Concurrency < cut);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2.0)]
    [InlineData(15.0)]
    [InlineData(89.0)]
    [InlineData(600.0)]
    [InlineData(3600.0)]
    [InlineData(86400.0)]
    public void cooldown_horizon_never_exceeds_configured_cooldown(double? retryAfter)
    {
        var time = new FakeTime();
        var c = Controller(start: 100, cooldown: 15.0, time: time);
        for (var i = 0; i < 200; i++)
        {
            c.NotifyRetry(retryAfter);
            Assert.True(c.CooldownUntil - c.Now() <= 15.0);
            Assert.True(c.CooldownUntil > c.Now());
            time.Advance(5.0);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(10.0)]
    [InlineData(15.0)]
    [InlineData(89.0)]
    [InlineData(3600.0)]
    [InlineData(86400.0)]
    public void sustained_rate_limits_reach_min_regardless_of_retry_after(double? retryAfter)
    {
        var time = new FakeTime();
        var c = Controller(min: 10, max: 100, start: 100, cooldown: 15.0, time: time);
        for (var i = 0; i < 24; i++)
        {
            c.NotifyRetry(retryAfter);
            time.Advance(5.0);
        }

        Assert.Equal(10, c.Concurrency);
    }

    [Fact]
    public void growth_resumes_one_cooldown_after_a_huge_retry_after()
    {
        var time = new FakeTime();
        var c = Controller(start: 100, cooldown: 15.0, time: time);
        c.NotifyRetry(retryAfter: 86400.0);
        Assert.Equal(80, c.Concurrency);
        time.Advance(16.0);
        SaturatedSuccesses(c);
        Assert.Equal(85, c.Concurrency);
    }

    [Fact]
    public void report_http_retry_transient_marks_the_request_but_does_not_scale_down()
    {
        var c = Controller(start: 40);
        Assert.Null(Concurrency.ActiveController);
        var before = Concurrency.HttpRetriesCount;
        using (var scope = Concurrency.BeginRequest(c))
        {
            Assert.Same(c, Concurrency.ActiveController);
            Assert.False(scope.Request.HadRetry);

            Concurrency.ReportHttpRetry();

            Assert.Equal(40, c.Concurrency);
            Assert.Empty(c.History);
            Assert.True(scope.Request.HadRetry);
            Assert.Equal(before + 1, Concurrency.HttpRetriesCount);
        }

        Assert.Null(Concurrency.ActiveController);
        Assert.Null(Concurrency.ActiveRequest);
    }

    [Fact]
    public void report_http_retry_rate_limit_scales_down()
    {
        var c = Controller(start: 40);
        using var scope = Concurrency.BeginRequest(c);

        Concurrency.ReportHttpRetry(RetryKind.RateLimit, retryAfter: 30.0);

        Assert.Equal(30, c.Concurrency);
        var entry = Assert.Single(c.History);
        Assert.Equal(LimitChangeReason.RateLimit, entry.Reason);
        Assert.Equal((40, 30), (entry.OldLimit, entry.NewLimit));
        Assert.True(scope.Request.HadRetry);
    }

    [Fact]
    public void observers_fire_on_each_scale_change()
    {
        var c = Controller(start: 10);
        var events = new List<int>();
        var events2 = new List<int>();
        c.AddObserver(() => events.Add(c.Concurrency));
        c.AddObserver(() => events2.Add(c.Concurrency));

        SaturatedSuccesses(c, 10);

        Assert.Equal([20], events);
        Assert.Equal([20], events2);
    }

    [Fact]
    public void set_max_lower_clamps_and_raise_only_lifts_the_ceiling()
    {
        var c = Controller(start: 40);
        c.SetMax(30);
        Assert.Equal(30, c.Concurrency);
        Assert.Equal(30, c.Max);
        var entry = c.History[^1];
        Assert.Equal((LimitChangeReason.Manual, 40, 30), (entry.Reason, entry.OldLimit, entry.NewLimit));

        var raised = Controller(start: 20, max: 50);
        raised.SetMax(80);
        Assert.Equal(20, raised.Concurrency);
        Assert.Equal(80, raised.Max);
        Assert.Empty(raised.History);
        SaturatedSuccesses(raised, 20);
        SaturatedSuccesses(raised, 40);
        Assert.Equal(80, raised.Concurrency);
    }

    [Fact]
    public void set_max_guards_its_config()
    {
        // one config instance shared by two controllers: a retune of one doesn't leak into the other
        var shared = AdaptiveConcurrency.Create(min: 1, start: 20, max: 200);
        var a = new AdaptiveConcurrencyController("a", shared);
        var b = new AdaptiveConcurrencyController("b", shared);
        a.SetMax(5);
        Assert.Equal(5, a.Max);
        Assert.Equal(200, b.Max);
        Assert.Equal(200, shared.Max);

        // below one is rejected before any state changes
        var c = Controller(start: 40);
        Assert.Throws<ArgumentOutOfRangeException>(() => c.SetMax(0));
        Assert.Equal(200, c.Max);
        Assert.Equal(40, c.Concurrency);
        Assert.Empty(c.History);

        // min follows the ceiling down and is restored on a raise
        var d = Controller(min: 10, max: 100, start: 20);
        d.SetMax(5);
        Assert.Equal((5, 5, 5), (d.Min, d.Max, d.Concurrency));
        d.SetMax(50);
        Assert.Equal((10, 50, 5), (d.Min, d.Max, d.Concurrency));
    }

    [Fact]
    public async Task value_clamps_and_in_use_stays_exact_after_a_cut_below_in_flight()
    {
        var c = (AdaptiveConcurrencyController)Concurrency.GetOrCreateSemaphore("m-clamp", 10, key: "kc", adaptive: AdaptiveConcurrency.Create(min: 1, start: 10, max: 80, decreaseFactor: 0.5));
        var leases = new List<ConcurrencyLease>();
        for (var i = 0; i < 8; i++)
        {
            leases.Add(await c.AcquireAsync());
        }

        c.NotifyRetry();

        Assert.Equal(5, c.Concurrency);
        Assert.Equal(8, c.InUse);
        Assert.Equal(0, c.Value);
        Assert.Equal(new ConcurrencyStatus(8, 5), Concurrency.StatusDisplay()["m-clamp"]);

        // a new acquire waits until in-flight drains below the new limit
        var pending = c.AcquireAsync().AsTask();
        await Task.Delay(50);
        Assert.False(pending.IsCompleted);
        foreach (var lease in leases.Take(4))
        {
            lease.Release();
        }

        var granted = await pending.WaitAsync(Timeout);
        Assert.Equal(5, c.InUse);
        granted.Release();
        foreach (var lease in leases.Skip(4))
        {
            lease.Release();
        }

        Assert.Equal(0, c.InUse);
    }

    [Fact]
    public async Task max_borrowed_is_not_inflated_during_cooldown()
    {
        var time = new FakeTime();
        var c = Controller(start: 10, cooldown: 15.0, time: time);
        c.NotifyRetry();
        Assert.Equal(8, c.Concurrency);
        Assert.Equal(0, c.MaxBorrowedThisRound);

        for (var i = 0; i < 3; i++)
        {
            await using (await c.AcquireAsync())
            {
            }
        }

        Assert.Equal(0, c.MaxBorrowedThisRound);

        time.Advance(16);
        await using (await c.AcquireAsync())
        {
        }

        Assert.Equal(1, c.MaxBorrowedThisRound);
    }

    // ---------------------------------------------------------------- DynamicSampleLimiter

    [Fact]
    public void dynamic_sample_limiter_starts_at_start_plus_buffer_and_follows_its_controller()
    {
        var limiter = new DynamicSampleLimiter(AdaptiveConcurrency.Create(min: 1, max: 80, start: 10), "k");
        Assert.Equal(15, limiter.Limit);
        Assert.Equal(15, limiter.TotalTokens);
        Assert.Null(limiter.Controller);

        var controller = (AdaptiveConcurrencyController)Concurrency.GetOrCreateSemaphore("m", 10, key: "k", adaptive: AdaptiveConcurrency.Create(min: 1, max: 80, start: 10));
        Assert.Same(controller, limiter.Controller);
        Assert.Equal(15, limiter.Limit);

        SaturatedSuccesses(controller, 10);
        Assert.Equal(20, controller.Concurrency);
        Assert.Equal(25, limiter.Limit);

        controller.NotifyRetry();
        Assert.Equal(15, controller.Concurrency);
        Assert.Equal(20, limiter.Limit);
    }

    [Fact]
    public void dynamic_sample_limiter_caps_at_adaptive_max_plus_buffer()
    {
        var limiter = new DynamicSampleLimiter(AdaptiveConcurrency.Create(min: 1, max: 20, start: 10), "k");
        var controller = (AdaptiveConcurrencyController)Concurrency.GetOrCreateSemaphore("m", 10, key: "k", adaptive: AdaptiveConcurrency.Create(min: 1, max: 20, start: 10));
        SaturatedSuccesses(controller, 10);
        SaturatedSuccesses(controller, 20);
        Assert.Equal(20, controller.Concurrency);
        Assert.Equal(25, limiter.Limit);
    }

    [Fact]
    public void dynamic_sample_limiter_is_scoped_to_its_own_model()
    {
        var limiter = new DynamicSampleLimiter(AdaptiveConcurrency.Create(min: 1, max: 80, start: 10), "k-mine");
        var other = (AdaptiveConcurrencyController)Concurrency.GetOrCreateSemaphore("grader", 50, key: "k-other", adaptive: AdaptiveConcurrency.Create(min: 1, max: 400, start: 50));
        Assert.Null(limiter.Controller);
        Assert.Equal(15, limiter.Limit);
        SaturatedSuccesses(other, 50);
        Assert.Equal(100, other.Concurrency);
        Assert.Equal(15, limiter.Limit);

        var mine = (AdaptiveConcurrencyController)Concurrency.GetOrCreateSemaphore("mine", 10, key: "k-mine", adaptive: AdaptiveConcurrency.Create(min: 1, max: 80, start: 10));
        Assert.Same(mine, limiter.Controller);
        SaturatedSuccesses(mine, 10);
        Assert.Equal(25, limiter.Limit);

        // a key that never matches stays at its initial value
        var parked = new DynamicSampleLimiter(AdaptiveConcurrency.Create(min: 1, max: 80, start: 10), "<no-model>");
        SaturatedSuccesses(mine, 20);
        Assert.Equal(15, parked.Limit);
    }

    [Fact]
    public void dynamic_sample_limiter_catches_up_to_an_existing_controller()
    {
        var controller = (AdaptiveConcurrencyController)Concurrency.GetOrCreateSemaphore("m", 10, key: "k", adaptive: AdaptiveConcurrency.Create(min: 1, max: 80, start: 10));
        SaturatedSuccesses(controller, 10);

        var limiter = new DynamicSampleLimiter(AdaptiveConcurrency.Create(min: 1, max: 80, start: 10), "k");

        Assert.Same(controller, limiter.Controller);
        Assert.Equal(25, limiter.Limit);
    }

    [Fact]
    public async Task dynamic_sample_limiter_recovers_after_shrinking_below_borrowed()
    {
        var limiter = new DynamicSampleLimiter(AdaptiveConcurrency.Create(min: 1, max: 80, start: 10), "k");
        var controller = (AdaptiveConcurrencyController)Concurrency.GetOrCreateSemaphore("m", 10, key: "k", adaptive: AdaptiveConcurrency.Create(min: 1, max: 80, start: 10, decreaseFactor: 0.5));
        var leases = new List<ConcurrencyLease>();
        for (var i = 0; i < 12; i++)
        {
            leases.Add(await limiter.AcquireAsync());
        }

        controller.NotifyRetry();
        Assert.Equal(5, controller.Concurrency);
        Assert.Equal(10, limiter.Limit);
        Assert.Equal(12, limiter.InUse);

        var pending = limiter.AcquireAsync().AsTask();
        await Task.Delay(50);
        Assert.False(pending.IsCompleted);
        foreach (var lease in leases.Take(3))
        {
            lease.Release();
        }

        var granted = await pending.WaitAsync(Timeout);
        Assert.Equal(10, limiter.InUse);
        granted.Release();
        foreach (var lease in leases.Skip(3))
        {
            lease.Release();
        }

        Assert.Equal(0, limiter.InUse);
    }

    [Fact]
    public void dynamic_sample_limiter_pin_and_clear()
    {
        var limiter = new DynamicSampleLimiter(AdaptiveConcurrency.Create(min: 1, max: 80, start: 10), "k");
        var controller = (AdaptiveConcurrencyController)Concurrency.GetOrCreateSemaphore("m", 10, key: "k", adaptive: AdaptiveConcurrency.Create(min: 1, max: 80, start: 10));

        limiter.SetOverride(3);
        Assert.Equal(3, limiter.Limit);
        Assert.Equal(3, limiter.Override);
        SaturatedSuccesses(controller, 10);
        Assert.Equal(20, controller.Concurrency);
        Assert.Equal(3, limiter.Limit);

        // an invalid pin is rejected without committing
        Assert.Throws<ArgumentOutOfRangeException>(() => limiter.SetOverride(0));
        Assert.Equal(3, limiter.Override);
        Assert.Equal(3, limiter.Limit);

        limiter.SetOverride(null);
        Assert.Null(limiter.Override);
        Assert.Equal(25, limiter.Limit);

        // clearing without a controller restores the initial capacity
        var parked = new DynamicSampleLimiter(AdaptiveConcurrency.Create(min: 1, max: 80, start: 10), "nobody");
        parked.SetOverride(2);
        parked.SetOverride(null);
        Assert.Equal(15, parked.Limit);

        // a pin survives a later adoption
        var early = new DynamicSampleLimiter(AdaptiveConcurrency.Create(min: 1, max: 80, start: 10), "k-late");
        early.SetOverride(4);
        var late = (AdaptiveConcurrencyController)Concurrency.GetOrCreateSemaphore("late", 10, key: "k-late", adaptive: AdaptiveConcurrency.Create(min: 1, max: 80, start: 10));
        Assert.Same(late, early.Controller);
        Assert.Equal(4, early.Limit);
        SaturatedSuccesses(late, 10);
        Assert.Equal(4, early.Limit);
        early.SetOverride(null);
        Assert.Equal(25, early.Limit);
    }

    // ---------------------------------------------------------------- throughput

    [Fact]
    public void buckets_window_sums()
    {
        var buckets = new TokenBuckets();
        buckets.Add(1000.0, outputTokens: 10, totalTokens: 20, requests: 1);
        buckets.Add(1030.0, outputTokens: 5, totalTokens: 10, requests: 1, retries: 2);

        var sums = buckets.WindowSums(1040.0, window: 60);
        Assert.Equal(new WindowSums(15, 30, 2, 2), sums);

        sums = buckets.WindowSums(1040.0, window: Throughput.BucketSeconds);
        Assert.Equal(5, sums.OutputTokens);
        Assert.Equal(2, sums.Retries);
    }

    [Fact]
    public void buckets_gap_does_not_leak_previous_lap()
    {
        var buckets = new TokenBuckets();
        buckets.Add(0.0, outputTokens: 100);
        var later = (double)(Throughput.HorizonSeconds + Throughput.BucketSeconds);
        Assert.Equal(0, buckets.WindowSums(later + 5, window: Throughput.HorizonSeconds).OutputTokens);
        buckets.Add(later, outputTokens: 7);
        Assert.Equal(7, buckets.WindowSums(later + 5, window: Throughput.HorizonSeconds).OutputTokens);
    }

    [Fact]
    public void buckets_window_clamped_to_horizon()
    {
        var buckets = new TokenBuckets();
        buckets.Add(0.0, outputTokens: 100);
        buckets.Add(Throughput.HorizonSeconds * 2.0, outputTokens: 1);
        Assert.Equal(1, buckets.WindowSums(Throughput.HorizonSeconds * 2.0, window: 10.0 * Throughput.HorizonSeconds).OutputTokens);
    }

    [Fact]
    public void backoff_overlap_partial_and_future_excluded()
    {
        Assert.Equal(30.0, Throughput.WindowBackoffSeconds([new BackoffInterval(70.0, 130.0)], now: 100.0, window: 60.0));
        Assert.Equal(10.0, Throughput.WindowBackoffSeconds([new BackoffInterval(50.0, 60.0)], now: 100.0, window: 60.0));
        Assert.Equal(0.0, Throughput.WindowBackoffSeconds([new BackoffInterval(0.0, 30.0)], now: 100.0, window: 60.0));
        Assert.Equal(120.0, Throughput.WindowBackoffSeconds([new BackoffInterval(40.0, 100.0), new BackoffInterval(40.0, 100.0)], now: 100.0, window: 60.0));
    }

    [Fact]
    public void backoff_intervals_pruned_past_horizon()
    {
        Throughput.RecordRetryWait("test/m", 10.0, now: 0.0);
        Throughput.RecordRetryWait("test/m", 10.0, now: Throughput.HorizonSeconds * 2.0);

        var intervals = Throughput.Record("test/m")!.BackoffIntervals;
        var only = Assert.Single(intervals);
        Assert.Equal(Throughput.HorizonSeconds * 2.0, only.Start);
    }

    [Fact]
    public void backoff_intervals_pruned_behind_a_long_head()
    {
        var headWait = 30.0 * 60.0;
        Throughput.RecordRetryWait("test/m", headWait, now: 0.0);
        for (var i = 1; i <= 1000; i++)
        {
            Throughput.RecordRetryWait("test/m", 0.1, now: i * 2.0);
        }

        var intervals = Throughput.Record("test/m")!.BackoffIntervals;
        Assert.True(intervals.Count < 700, $"expected a bounded list, got {intervals.Count}");
        Assert.Equal(new BackoffInterval(0.0, headWait), intervals[0]);
    }

    [Fact]
    public void snapshot_rates_and_cumulative()
    {
        Throughput.RecordGenerate("test/m", Usage(outputTokens: 120, totalTokens: 300), now: 1000.0);
        Throughput.RecordGenerate("test/m", Usage(outputTokens: 120, totalTokens: 300), now: 1030.0);
        Throughput.RecordRetry("test/m", RetryKind.RateLimit, now: 1030.0);
        Throughput.RecordRetry("test/m", RetryKind.Transient, now: 1030.0);
        Throughput.RecordRetryWait("test/m", 30.0, now: 1030.0);

        var view = Throughput.Snapshot(window: 60, now: 1060.0)["test/m"];

        Assert.Equal(60.0, view.WindowSeconds);
        Assert.Equal(240 / 60.0, view.OutputTokensPerSecond, 6);
        Assert.Equal(2.0, view.RequestsPerMinute, 6);
        Assert.Equal(2.0, view.RetriesPerMinute, 6);
        Assert.Equal(0.5, view.BackoffRatio, 6);
        Assert.Equal(2, view.Requests);
        Assert.Equal(240, view.OutputTokens);
        Assert.Equal(600, view.TotalTokens);
        Assert.Equal(1, view.RetriesRateLimit);
        Assert.Equal(1, view.RetriesTransient);
        Assert.Equal(30.0, view.RetryWaitSeconds);
        Assert.NotNull(view.FirstActivity);
        Assert.NotNull(view.LastActivity);
    }

    [Fact]
    public void snapshot_clamps_a_fresh_run_window()
    {
        Throughput.RecordGenerate("test/m", Usage(outputTokens: 100), now: 1000.0);
        var view = Throughput.Snapshot(window: 60, now: 1010.0)["test/m"];
        Assert.Equal(10.0, view.WindowSeconds, 6);
        Assert.Equal(10.0, view.OutputTokensPerSecond, 6);
    }

    [Fact]
    public void report_http_retry_attributes_the_model()
    {
        var before = Concurrency.HttpRetriesCount;
        Concurrency.ReportHttpRetry(RetryKind.RateLimit, model: "test/m");
        Concurrency.ReportHttpRetry();

        var view = Throughput.View("test/m");
        Assert.NotNull(view);
        Assert.Equal(1, view.RetriesRateLimit);
        Assert.Equal(["test/m"], Throughput.Snapshot().Keys);
        Assert.Equal(before + 2, Concurrency.HttpRetriesCount);
        Assert.Null(Throughput.View("unknown"));
    }

    [Fact]
    public void init_clears_the_throughput_registry()
    {
        Throughput.RecordGenerate("test/m", Usage());
        Assert.NotEmpty(Throughput.Snapshot());
        Throughput.Init();
        Assert.Empty(Throughput.Snapshot());
    }

    [Fact]
    public void throughput_report_envelope()
    {
        Throughput.RecordGenerate("test/m", Usage(outputTokens: 60, totalTokens: 90));
        Throughput.RecordRetry("test/m", RetryKind.RateLimit);

        var report = Throughput.Report(window: 60);

        Assert.Equal(60, (int)report["window_seconds"]!);
        Assert.False(string.IsNullOrEmpty((string)report["as_of"]!));
        var row = Assert.Single(report["models"]!.AsArray())!.AsObject();
        Assert.Equal("test/m", (string)row["model"]!);
        var window = (double)row["window_seconds"]!;
        Assert.True(window > 0 && window <= 60);
        Assert.Equal(1, (int)row["cumulative"]!["requests"]!);
        Assert.Equal(1, (int)row["cumulative"]!["retries"]!["rate_limit"]!);
        Assert.Equal(0, (int)row["cumulative"]!["retries"]!["transient"]!);
        Assert.False(string.IsNullOrEmpty((string)row["cumulative"]!["first_activity_at"]!));
        Assert.Equal(0, (int)row["retry_waits_active"]!);
    }

    [Fact]
    public void footer_rate_is_gated_on_retries()
    {
        Throughput.RecordGenerate("test/m", Usage(outputTokens: 60));
        Assert.Null(Throughput.FooterRate());
        Throughput.RecordRetry("test/m", RetryKind.RateLimit);
        var rate = Throughput.FooterRate();
        Assert.NotNull(rate);
        Assert.True(rate > 0);
    }

    [Fact]
    public void retry_waits_active_counts_only_unelapsed_uncleared_waiters()
    {
        var a = new object();
        var b = new object();
        Throughput.RecordRetryWait("test/m", 60.0, now: 100.0, waiter: a);
        Throughput.RecordRetryWait("test/m", 10.0, now: 100.0, waiter: b);

        Assert.Equal(1, Throughput.View("test/m", now: 120.0)!.RetryWaitsActive);
        Assert.Equal(2, Throughput.View("test/m", now: 105.0)!.RetryWaitsActive);

        Throughput.ClearRetryWait("test/m", a);
        Assert.Equal(0, Throughput.View("test/m", now: 120.0)!.RetryWaitsActive);
        Assert.Equal(70.0, Throughput.View("test/m", now: 120.0)!.RetryWaitSeconds);
    }

    // ---------------------------------------------------------------- Model integration

    [Fact]
    public async Task adaptive_is_on_by_default_and_clean_generates_count_toward_the_round()
    {
        var api = new ScriptedModelApi(Enumerable.Range(0, 4).Select(_ => ScriptedTurn.Text("ok", new ModelUsage(1, 1, 2))));
        var model = new Model(api) { AdaptiveConnections = AdaptiveConnections.From(AdaptiveConcurrency.Create(min: 1, start: 1, max: 8)) };

        for (var i = 0; i < 4; i++)
        {
            await model.GenerateAsync("hi");
        }

        var controller = Assert.Single(Concurrency.AdaptiveControllers());
        Assert.Equal("ModelScriptedModelApi:default", model.ConcurrencyKey);
        Assert.Equal(model.ConcurrencyKey, controller.Key);
        Assert.Equal("scripted", controller.Name);
        Assert.Equal(2, controller.Concurrency);
        Assert.Equal(LimitChangeReason.SlowStart, Assert.Single(controller.History).Reason);
        Assert.Equal(0, controller.InUse);
        Assert.Null(Concurrency.ActiveController);

        // the null setting is Python's None: adaptive with the default bounds
        Concurrency.Init();
        var plain = new Model(new ScriptedModelApi(ScriptedTurn.Text("ok")));
        await plain.GenerateAsync("hi");
        var defaults = Assert.Single(Concurrency.AdaptiveControllers());
        Assert.Equal(20, defaults.Concurrency);
        Assert.Empty(defaults.History);
        Assert.Equal(new ConcurrencyStatus(0, 20), Concurrency.StatusDisplay()["scripted"]);
    }

    [Fact]
    public async Task explicit_max_connections_wins_and_disabled_uses_the_api_default()
    {
        var model = new Model(new ScriptedModelApi(ScriptedTurn.Text("ok")), new GenerateConfig { MaxConnections = 3 });
        await model.GenerateAsync("hi");
        Assert.Empty(Concurrency.AdaptiveControllers());
        var fixedLimit = Assert.IsType<ResizableSemaphore>(Assert.Single(Concurrency.Semaphores()));
        Assert.Equal(3, fixedLimit.Concurrency);
        Assert.Equal(model.ConcurrencyKey, fixedLimit.Key);

        Concurrency.Init();
        var disabled = new Model(new ScriptedModelApi([ScriptedTurn.Text("ok")]) { ConnectionLimit = 7 }) { AdaptiveConnections = AdaptiveConnections.Disabled };
        await disabled.GenerateAsync("hi");
        Assert.Empty(Concurrency.AdaptiveControllers());
        Assert.Equal(7, Assert.IsType<ResizableSemaphore>(Assert.Single(Concurrency.Semaphores())).Concurrency);

        // the eval-level setting and the sink copy carry the model's setting
        Assert.Same(AdaptiveConnections.Disabled, disabled.WithEventSink(new NullSink()).AdaptiveConnections);

        // a zero limit is an error, not "unset"
        Concurrency.Init();
        var zero = new Model(new ScriptedModelApi(ScriptedTurn.Text("ok")), new GenerateConfig { MaxConnections = 0 });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => zero.GenerateAsync("hi"));
    }

    private sealed class NullSink : IModelEventSink
    {
        public void OnModelEvent(ModelEvent e)
        {
        }
    }

    [Fact]
    public async Task model_honours_max_connections_with_peak_concurrent_calls()
    {
        var entered = 0;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new ScriptedModelApi(Enumerable.Range(0, 10).Select(_ => ScriptedTurn.Text("ok")))
        {
            ConnectionLimit = 3,
            Gate = async _ =>
            {
                if (Interlocked.Increment(ref entered) == 3)
                {
                    barrier.TrySetResult();
                }

                await barrier.Task;
            },
        };
        var model = new Model(api) { AdaptiveConnections = AdaptiveConnections.Disabled };

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => model.GenerateAsync("hi"))).WaitAsync(Timeout);

        Assert.Equal(3, api.PeakConcurrentCalls);
        Assert.Equal(0, api.ActiveCalls);
        Assert.Equal(10, api.Requests.Count);
        Assert.Equal(new ConcurrencyStatus(0, 3), Concurrency.StatusDisplay()["scripted"]);
    }

    [Fact]
    public async Task an_adaptive_controller_bounds_concurrent_calls_at_its_current_limit()
    {
        var entered = 0;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new ScriptedModelApi(Enumerable.Range(0, 6).Select(_ => ScriptedTurn.Text("ok")))
        {
            Gate = async _ =>
            {
                if (Interlocked.Increment(ref entered) == 2)
                {
                    barrier.TrySetResult();
                }

                await barrier.Task;
            },
        };
        var model = new Model(api) { AdaptiveConnections = AdaptiveConnections.From(AdaptiveConcurrency.Create(min: 1, start: 2, max: 2)) };

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => model.GenerateAsync("hi"))).WaitAsync(Timeout);

        Assert.Equal(2, api.PeakConcurrentCalls);
        var controller = Assert.Single(Concurrency.AdaptiveControllers());
        Assert.Equal(2, controller.Concurrency);
        Assert.Equal(0, controller.InUse);
    }

    [Fact]
    public async Task a_rate_limit_retry_cuts_the_controller_and_the_retried_success_is_neutral()
    {
        var headers = new Dictionary<string, string> { ["Retry-After"] = "7" };
        var api = new ScriptedModelApi(ScriptedTurn.Throw(Http(429, headers)), ScriptedTurn.Text("ok", new ModelUsage(2, 3, 5)));
        var model = new Model(api, retry: new ModelRetryOptions(MaxRetries: 5, Delay: NoDelay))
        {
            AdaptiveConnections = AdaptiveConnections.From(AdaptiveConcurrency.Create(min: 1, start: 10, max: 50)),
        };
        var retriesBefore = Concurrency.HttpRetriesCount;

        var output = await model.GenerateAsync("hi");

        Assert.Equal("ok", output.Completion);
        var controller = Assert.Single(Concurrency.AdaptiveControllers());
        Assert.Equal(8, controller.Concurrency);
        var cut = Assert.Single(controller.History);
        Assert.Equal((LimitChangeReason.RateLimit, 10, 8), (cut.Reason, cut.OldLimit, cut.NewLimit));
        Assert.Equal(0, controller.SuccessCount);
        Assert.Equal(0, controller.InUse);
        Assert.Equal(retriesBefore + 1, Concurrency.HttpRetriesCount);

        var view = Throughput.View("scripted");
        Assert.NotNull(view);
        Assert.Equal(1, view.RetriesRateLimit);
        Assert.Equal(0, view.RetriesTransient);
        Assert.Equal(7.0, view.RetryWaitSeconds);
        Assert.Equal(1, view.Requests);
        Assert.Equal(3, view.OutputTokens);
        Assert.Equal(5, view.TotalTokens);
    }

    [Fact]
    public async Task a_transient_retry_does_not_scale_down_but_makes_the_success_neutral()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Throw(Http(503)), ScriptedTurn.Text("ok"));
        var model = new Model(api, retry: new ModelRetryOptions(MaxRetries: 5, Delay: NoDelay))
        {
            AdaptiveConnections = AdaptiveConnections.From(AdaptiveConcurrency.Create(min: 1, start: 4, max: 50)),
        };

        await model.GenerateAsync("hi");

        var controller = Assert.Single(Concurrency.AdaptiveControllers());
        Assert.Equal(4, controller.Concurrency);
        Assert.Empty(controller.History);
        Assert.False(controller.InCooldown);
        Assert.Equal(0, controller.SuccessCount);
        Assert.Equal(1, Throughput.View("scripted")!.RetriesTransient);
    }

    [Fact]
    public async Task a_non_retryable_failure_still_reports_no_retry_and_releases_the_slot()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Throw(Http(400)));
        var model = new Model(api, retry: new ModelRetryOptions(Delay: NoDelay)) { AdaptiveConnections = AdaptiveConnections.Disabled };
        var before = Concurrency.HttpRetriesCount;

        await Assert.ThrowsAsync<RequestFailedException>(() => model.GenerateAsync("hi"));

        Assert.Equal(before, Concurrency.HttpRetriesCount);
        Assert.Equal(0, Assert.Single(Concurrency.Semaphores()).InUse);
        Assert.Null(Throughput.View("scripted"));
    }

    [Fact]
    public async Task a_retry_wait_marks_the_sample_until_the_call_resolves()
    {
        using var scope = new SampleContextScope();
        var observed = -1;
        var headers = new Dictionary<string, string> { ["Retry-After"] = "30" };
        var api = new ScriptedModelApi(
            ScriptedTurn.Throw(Http(429, headers)),
            ScriptedTurn.From((_, _) =>
            {
                observed = Throughput.View("scripted")!.RetryWaitsActive;
                return ModelOutput.FromContent("scripted", "ok");
            }));
        var model = new Model(api, retry: new ModelRetryOptions(Delay: NoDelay)) { AdaptiveConnections = AdaptiveConnections.Disabled };

        await model.GenerateAsync("hi");

        Assert.Equal(1, observed);
        Assert.Equal(0, Throughput.View("scripted")!.RetryWaitsActive);
        Assert.Equal(30.0, Throughput.View("scripted")!.RetryWaitSeconds);
    }

    [Fact]
    public async Task cancellation_releases_leases_whether_waiting_or_holding()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new ScriptedModelApi(Enumerable.Range(0, 3).Select(_ => ScriptedTurn.Text("ok")))
        {
            ConnectionLimit = 1,
            Gate = async token =>
            {
                using var registration = token.Register(() => gate.TrySetCanceled(token));
                await gate.Task;
            },
        };
        var model = new Model(api) { AdaptiveConnections = AdaptiveConnections.Disabled };

        // a holder inside the api, then a waiter queued for its slot
        var first = model.GenerateAsync("a");
        await WaitUntilAsync(() => api.ActiveCalls == 1);
        var semaphore = Assert.IsType<ResizableSemaphore>(Assert.Single(Concurrency.Semaphores()));
        using var waitingCts = new CancellationTokenSource();
        var second = model.GenerateAsync("b", cancellationToken: waitingCts.Token);
        await WaitUntilAsync(() => semaphore.Limiter.Waiting == 1);

        // cancelling the waiter never consumes the slot
        waitingCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(1, semaphore.InUse);
        Assert.Equal(0, semaphore.Limiter.Waiting);

        gate.SetResult();
        await first.WaitAsync(Timeout);
        Assert.Equal(0, semaphore.InUse);

        // cancelling the holder mid-call releases its slot
        var holdGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = new ScriptedModelApi([ScriptedTurn.Text("ok")]) { ConnectionLimit = 1, Gate = token => Task.Delay(System.Threading.Timeout.Infinite, token) };
        var held = new Model(holding) { AdaptiveConnections = AdaptiveConnections.Disabled };
        using var holdingCts = new CancellationTokenSource();
        var third = held.GenerateAsync("c", cancellationToken: holdingCts.Token);
        await WaitUntilAsync(() => holding.ActiveCalls == 1);
        holdingCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third);
        Assert.Equal(0, semaphore.InUse);
        Assert.Equal(0, holding.ActiveCalls);
        _ = holdGate;
    }

    // ---------------------------------------------------------------- sample scheduling and the runner

    [Fact]
    public void sample_semaphore_follows_the_python_derivation()
    {
        var api = new ScriptedModelApi { ConnectionLimit = 6 };
        var config = new GenerateConfig();

        Assert.Equal(3, Assert.IsType<ResizableLimiter>(SampleScheduler.CreateSampleSemaphore(3, config, null, api)).Limit);
        Assert.Equal(25, Assert.IsType<DynamicSampleLimiter>(SampleScheduler.CreateSampleSemaphore(null, config, null, api)).Limit);
        Assert.Equal(9, Assert.IsType<DynamicSampleLimiter>(SampleScheduler.CreateSampleSemaphore(null, config, AdaptiveConnections.From(AdaptiveConcurrency.Create(min: 1, start: 4, max: 8)), api)).Limit);
        Assert.Equal(4, Assert.IsType<ResizableLimiter>(SampleScheduler.CreateSampleSemaphore(null, new GenerateConfig { MaxConnections = 4 }, null, api)).Limit);
        Assert.Equal(6, Assert.IsType<ResizableLimiter>(SampleScheduler.CreateSampleSemaphore(null, config, AdaptiveConnections.Disabled, api)).Limit);
        Assert.Equal(SampleScheduler.DefaultMaxConnections, Assert.IsType<ResizableLimiter>(SampleScheduler.CreateSampleSemaphore(null, config, AdaptiveConnections.Disabled, null)).Limit);
        Assert.Equal(SampleScheduler.DefaultMaxConnectionsBatch, Assert.IsType<ResizableLimiter>(SampleScheduler.CreateSampleSemaphore(null, config, null, api, batch: true)).Limit);
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleScheduler.CreateSampleSemaphore(0, config, null, api));

        // task-scoped reuse across retry attempts, reset by Init
        var first = SampleScheduler.CreateSampleSemaphore(null, config, null, api, taskId: "t1");
        Assert.Same(first, SampleScheduler.CreateSampleSemaphore(2, config, null, api, taskId: "t1"));
        Assert.Same(first, Concurrency.TaskSampleSemaphore("t1"));
        Concurrency.Init();
        Assert.Null(Concurrency.TaskSampleSemaphore("t1"));
        Assert.NotSame(first, SampleScheduler.CreateSampleSemaphore(null, config, null, api, taskId: "t1"));

        // the dynamic limiter follows the model's controller under the shared key
        Concurrency.Init();
        var dynamic = Assert.IsType<DynamicSampleLimiter>(SampleScheduler.CreateSampleSemaphore(null, config, AdaptiveConnections.From(AdaptiveConcurrency.Create(min: 1, start: 4, max: 8)), api));
        var controller = (AdaptiveConcurrencyController)Concurrency.GetOrCreateSemaphore("scripted", 4, key: ModelConcurrency.Key(api), adaptive: AdaptiveConcurrency.Create(min: 1, start: 4, max: 8));
        Assert.Same(controller, dynamic.Controller);
        SaturatedSuccesses(controller);
        Assert.Equal(13, dynamic.Limit);
    }

    [Fact]
    public async Task the_runner_bounds_samples_in_flight_and_reports_stats()
    {
        var logDir = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var dataset = new MemoryDataset(Enumerable.Range(1, 6).Select(i => new Sample($"q{i}") { Target = "ok" }).ToList(), name: "six");
            var entered = 0;
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var api = new ScriptedModelApi(Enumerable.Range(0, 6).Select(_ => ScriptedTurn.Text("ok", new ModelUsage(1, 1, 2))))
            {
                Gate = async _ =>
                {
                    if (Interlocked.Increment(ref entered) == 2)
                    {
                        barrier.TrySetResult();
                    }

                    await barrier.Task;
                },
            };
            var task = new EvalTask { Name = "bounded", Dataset = dataset, Scorers = [Scorers.Includes()] };
            var reporter = new RecordingReporter();
            var retriesBefore = Concurrency.HttpRetriesCount;

            var log = await Eval.RunAsync(task, new EvalOptions { Model = new Model(api), LogDir = logDir, MaxSamples = 2, Reporter = reporter }).WaitAsync(Timeout);

            Assert.Equal(EvalStatus.Success, log.Status);
            Assert.Equal(2, api.PeakConcurrentCalls);
            Assert.Equal(2, log.Eval.Config.MaxSamples);
            Assert.Empty(log.Stats.ConnectionLimitHistory);
            Assert.Contains(reporter.Stats, stats => stats.Samples == new ConcurrencyStatus(2, 2));
            Assert.All(reporter.Stats, stats => Assert.Equal(retriesBefore, stats.HttpRetries));
            Assert.All(reporter.Stats, stats => Assert.Null(stats.OutputTokensPerSecond));
            Assert.Contains(reporter.Stats, stats => stats.Connections.ContainsKey("scripted"));
            Assert.True(reporter.Stats[^1].Throughput["scripted"].Requests == 6);

            // a derived sample limit is logged as unset, like Python's None
            var derived = await Eval.RunAsync(
                new EvalTask { Name = "derived", Dataset = new MemoryDataset([new Sample("q") { Target = "ok" }], name: "one"), Scorers = [Scorers.Includes()] },
                new EvalOptions { Model = new Model(new ScriptedModelApi(ScriptedTurn.Text("ok"))), LogDir = logDir }).WaitAsync(Timeout);
            Assert.Null(derived.Eval.Config.MaxSamples);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Eval.RunAsync(task, new EvalOptions { Model = new Model(api), LogDir = logDir, MaxSamples = 0 }));
        }
        finally
        {
            try
            {
                Directory.Delete(logDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task the_runner_captures_the_connection_limit_history_and_the_eval_level_setting()
    {
        var logDir = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var dataset = new MemoryDataset(Enumerable.Range(1, 4).Select(i => new Sample($"q{i}") { Target = "ok" }).ToList(), name: "four");
            var api = new ScriptedModelApi(Enumerable.Range(0, 4).Select(_ => ScriptedTurn.Text("ok", new ModelUsage(1, 1, 2))));
            var task = new EvalTask { Name = "history", Dataset = dataset, Scorers = [Scorers.Includes()] };
            var options = new EvalOptions
            {
                Model = new Model(api),
                LogDir = logDir,
                MaxSamples = 1,
                AdaptiveConnections = AdaptiveConnections.From(AdaptiveConcurrency.Create(min: 1, start: 1, max: 8)),
                LogFormat = LogFormat.Json,
            };

            var log = await Eval.RunAsync(task, options).WaitAsync(Timeout);

            var change = Assert.Single(log.Stats.ConnectionLimitHistory);
            Assert.Equal((LimitChangeReason.SlowStart, 1, 2, "scripted"), (change.Reason, change.OldLimit, change.NewLimit, change.Model));
            var read = EvalLogWriter.Read(log.Location!);
            Assert.Equal(change, Assert.Single(read.Stats.ConnectionLimitHistory));
            var json = JsonNode.Parse(File.ReadAllText(log.Location!))!;
            var entry = Assert.Single(json["stats"]!["connection_limit_history"]!.AsArray())!.AsObject();
            Assert.Equal("slow_start", (string)entry["reason"]!);
            Assert.Equal(1, (int)entry["old_limit"]!);
            Assert.Equal(2, (int)entry["new_limit"]!);
            Assert.Equal("scripted", (string)entry["model"]!);
            Assert.True((double)entry["timestamp"]! > 1_600_000_000);
        }
        finally
        {
            try
            {
                Directory.Delete(logDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void console_reporter_prints_stats_only_once_retries_have_occurred()
    {
        var writer = new StringWriter();
        IEvalReporter reporter = new ConsoleEvalReporter(writer);
        reporter.Stats(new EvalRunStats());
        Assert.Equal("", writer.ToString());

        var stats = new EvalRunStats { HttpRetries = 3, OutputTokensPerSecond = 41.7, Connections = new Dictionary<string, ConcurrencyStatus> { ["openai"] = new(5, 20) } };
        reporter.Stats(stats);
        reporter.Stats(stats);

        Assert.Equal("HTTP retries: 3  out tok/s: 42  openai: 5/20" + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public void limit_change_reason_round_trips_as_python_literals()
    {
        foreach (var reason in Enum.GetValues<LimitChangeReason>())
        {
            Assert.Equal(reason, LimitChangeReasonExtensions.ParseLimitChangeReason(reason.ToPythonString()));
        }

        Assert.Equal("steady_state_up", LimitChangeReason.SteadyStateUp.ToPythonString());
        Assert.Throws<System.Text.Json.JsonException>(() => LimitChangeReasonExtensions.ParseLimitChangeReason("bogus"));
    }
}

using Azure;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Hooks;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Testing;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tests;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Hooks = InspectAzureAI.Eval.Hooks.Hooks;
using Model = InspectAzureAI.Eval.Model.Model;
using Scorers = InspectAzureAI.Eval.Scorers.Scorers;
using Solvers = InspectAzureAI.Eval.Solvers.Solvers;

/// <summary>
/// Port of <c>tests/hooks/test_hooks.py</c> plus the sequence/payload checks of <c>hooks/_hooks.py</c>'s emit sites:
/// a recording hook asserts the exact event order and payload contents of an eval run with a model retry and a
/// cache hit, and the failure semantics (logged hook errors, propagated limits and cancellation).
/// </summary>
public sealed class HooksTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", Guid.NewGuid().ToString("N"));

    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", "cache-" + Guid.NewGuid().ToString("N"));

    private readonly EnvVarScope _env;

    public HooksTests()
    {
        HookRegistry.Clear();
        HookStartup.Reset();
        ProviderLogger.Reset();
        _env = new EnvVarScope().Set(CacheOps.CacheDirVar, _cacheDir).Set(HookStartup.RequiredHooksVar, null);
    }

    public void Dispose()
    {
        HookRegistry.Clear();
        HookStartup.Reset();
        ProviderLogger.Reset();
        _env.Dispose();
        foreach (var dir in new[] { _logDir, _cacheDir })
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ helpers

    /// <summary>Port of the test module's <c>MockHooks</c>: records every callback, in order, with its payload.</summary>
    private class RecordingHooks : Hooks
    {
        private readonly List<(string Name, object Data)> _sequence = [];

        public bool ShouldEnable { get; set; } = true;

        public override bool Enabled => ShouldEnable;

        /// <summary>Every callback in delivery order (sample events included).</summary>
        public IReadOnlyList<(string Name, object Data)> Sequence
        {
            get
            {
                lock (_sequence)
                {
                    return _sequence.ToArray();
                }
            }
        }

        /// <summary>The callback names without the sample events (whose interleaving with the lifecycle is not deterministic, as in Python).</summary>
        public IReadOnlyList<string> Lifecycle => Sequence.Where(e => e.Name != "sample_event").Select(e => e.Name).ToList();

        public IReadOnlyList<T> Of<T>() => Sequence.Select(e => e.Data).OfType<T>().ToList();

        public int IndexOf(string name) => Sequence.ToList().FindIndex(e => e.Name == name);

        public override Task OnEvalSetStartAsync(EvalSetStart data, CancellationToken cancellationToken) => Record("eval_set_start", data);

        public override Task OnEvalSetEndAsync(EvalSetEnd data, CancellationToken cancellationToken) => Record("eval_set_end", data);

        public override Task OnRunStartAsync(RunStart data, CancellationToken cancellationToken) => Record("run_start", data);

        public override Task OnRunEndAsync(RunEnd data, CancellationToken cancellationToken) => Record("run_end", data);

        public override Task OnTaskStartAsync(TaskStart data, CancellationToken cancellationToken) => Record("task_start", data);

        public override Task OnTaskEndAsync(TaskEnd data, CancellationToken cancellationToken) => Record("task_end", data);

        public override Task OnSampleInitAsync(SampleInit data, CancellationToken cancellationToken) => Record("sample_init", data);

        public override Task OnSampleStartAsync(SampleStart data, CancellationToken cancellationToken) => Record("sample_start", data);

        public override Task OnSampleEventAsync(SampleEvent data, CancellationToken cancellationToken) => Record("sample_event", data);

        public override Task OnSampleEndAsync(SampleEnd data, CancellationToken cancellationToken) => Record("sample_end", data);

        public override Task OnBeforeModelGenerateAsync(BeforeModelGenerate data, CancellationToken cancellationToken) => Record("before_model_generate", data);

        public override Task OnModelRetryAsync(ModelRetry data, CancellationToken cancellationToken) => Record("model_retry", data);

        public override Task OnSampleAttemptStartAsync(SampleAttemptStart data, CancellationToken cancellationToken) => Record("sample_attempt_start", data);

        public override Task OnSampleAttemptEndAsync(SampleAttemptEnd data, CancellationToken cancellationToken) => Record("sample_attempt_end", data);

        public override Task OnModelUsageAsync(ModelUsageData data, CancellationToken cancellationToken) => Record("model_usage", data);

        public override Task OnModelCacheUsageAsync(ModelCacheUsageData data, CancellationToken cancellationToken) => Record("model_cache_usage", data);

        public override Task OnSampleScoringAsync(SampleScoring data, CancellationToken cancellationToken) => Record("sample_scoring", data);

        private Task Record(string name, object data)
        {
            lock (_sequence)
            {
                _sequence.Add((name, data));
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>The recording hook plus Python's <c>MockHooks.override_api_key</c>.</summary>
    private sealed class ApiKeyHooks : RecordingHooks
    {
        public override string? OverrideApiKey(ApiKeyOverride data) => $"mocked-{data.EnvVarName}-{data.Value}";
    }

    /// <summary>A hook whose named callback throws the given exception (every other callback is a no-op).</summary>
    private sealed class ThrowingHooks(string callback, Func<Exception> error) : Hooks
    {
        public override Task OnSampleStartAsync(SampleStart data, CancellationToken cancellationToken) => Throw("sample_start");

        public override Task OnBeforeModelGenerateAsync(BeforeModelGenerate data, CancellationToken cancellationToken) => Throw("before_model_generate");

        public override Task OnSampleEventAsync(SampleEvent data, CancellationToken cancellationToken) => Throw("sample_event");

        private Task Throw(string name) => callback == name ? throw error() : Task.CompletedTask;
    }

    private sealed class ThrowingKeyHooks : Hooks
    {
        public override string? OverrideApiKey(ApiKeyOverride data) => throw new InvalidOperationException("vault down");
    }

    private sealed class CancellingHooks(CancellationTokenSource cts) : Hooks
    {
        public override Task OnSampleStartAsync(SampleStart data, CancellationToken cancellationToken)
        {
            cts.Cancel();
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingReporter : IEvalReporter
    {
        public List<string> Messages { get; } = [];

        public void SampleStarted(object id, int epoch)
        {
        }

        public void SampleCompleted(EvalSample sample)
        {
        }

        public void Message(string text) => Messages.Add(text);
    }

    private static RequestFailedException Http(int status) => new(CannedResponse.Error(status, $"http {status}"));

    /// <summary>A scripted text turn with usage, so the model usage hook fires (as it does for every real provider call).</summary>
    private static ScriptedTurn Turn(string text) => ScriptedTurn.Text(text, new ModelUsage(5, 1, 6));

    private static Solver FailingDeterministic(params bool[] failures)
    {
        var queue = new Queue<bool>(failures);
        return (state, _, _) => queue.Count > 0 && queue.Dequeue() ? throw new InvalidOperationException("Eval failed!") : Task.FromResult(state);
    }

    private static EvalTask Task1(string name = "task", Solver? solver = null, int samples = 1, Solver? setup = null) => new()
    {
        Name = name,
        Dataset = new MemoryDataset(Enumerable.Range(1, samples).Select(i => new Sample($"sample_{i}") { Target = "Paris" }), name: "ds"),
        Solver = solver ?? Solvers.Generate(),
        Setup = setup,
        Scorers = [Scorers.Includes()],
    };

    private EvalOptions Options(ScriptedModelApi? api = null, ModelRetryOptions? retry = null) =>
        new() { Model = new Model(api ?? new ScriptedModelApi(), retry: retry), LogDir = _logDir, MaxSamples = 1 };

    private static RecordingHooks Register(string name = "test_hooks")
    {
        var hook = new RecordingHooks();
        HookRegistry.Register(hook, name, $"{name}-description");
        return hook;
    }

    // ------------------------------------------------------------------------------------------ the sequence

    [Fact]
    public async Task a_run_with_a_model_retry_and_a_cache_hit_delivers_the_exact_sequence_and_payloads()
    {
        var hook = Register();
        var delays = new List<TimeSpan>();
        var api = new ScriptedModelApi(ScriptedTurn.Throw(Http(503)), ScriptedTurn.Text("Paris", new ModelUsage(5, 1, 6)));
        var model = new Model(api, retry: new ModelRetryOptions(MaxRetries: 3, Delay: (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        }));
        // two identical generate calls under the cache policy: the first retries a 503 then stores, the second hits
        // (the input is snapshotted because the hook payload aliases the list handed to generate, as in Python)
        Solver solver = async (state, _, ct) =>
        {
            await model.GenerateAsync(state.Messages.ToArray(), cache: CachePolicy.Default, cancellationToken: ct);
            var output = await model.GenerateAsync(state.Messages.ToArray(), cache: CachePolicy.Default, cancellationToken: ct);
            state.Output = output;
            state.Messages.Add(output.Message);
            return state;
        };
        var task = Task1("geo", solver);

        var log = await Eval.RunAsync(task, new EvalOptions { Model = model, LogDir = _logDir, MaxSamples = 1 });

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Equal(
            [
                "run_start", "task_start", "sample_init", "sample_start", "sample_attempt_start",
                "before_model_generate", "model_retry", "before_model_generate", "model_usage",
                "before_model_generate", "model_cache_usage",
                "sample_scoring", "sample_attempt_end", "sample_end", "task_end", "run_end",
            ],
            hook.Lifecycle);

        // run / task
        var runStart = Assert.Single(hook.Of<RunStart>());
        Assert.Null(runStart.EvalSetId);
        Assert.Equal(log.Eval.RunId, runStart.RunId);
        Assert.NotEmpty(runStart.RunId);
        Assert.Equal(["geo"], runStart.TaskNames);
        var taskStart = Assert.Single(hook.Of<TaskStart>());
        Assert.Equal(log.Eval.RunId, taskStart.RunId);
        Assert.Equal(log.Eval.EvalId, taskStart.EvalId);
        Assert.Same(log.Eval, taskStart.Spec);
        Assert.Same(log.Plan, taskStart.Plan);
        Assert.Equal(["solver"], taskStart.Plan.Steps.Select(step => step.Solver));
        Assert.Equal(model.Config, taskStart.Plan.Config);
        Assert.Same(log.Plan.Config, taskStart.Plan.Config);
        var taskEnd = Assert.Single(hook.Of<TaskEnd>());
        Assert.Same(log, taskEnd.Log);
        Assert.Equal((log.Eval.RunId, log.Eval.EvalId), (taskEnd.RunId, taskEnd.EvalId));
        var runEnd = Assert.Single(hook.Of<RunEnd>());
        Assert.Null(runEnd.Exception);
        Assert.Equal(log.Eval.RunId, runEnd.RunId);
        Assert.Same(log, Assert.Single(runEnd.Logs));

        // sample lifecycle: every payload carries the eval ids and the sample uuid
        var uuid = sample.Uuid!;
        var init = Assert.Single(hook.Of<SampleInit>());
        Assert.Equal((null, log.Eval.RunId, log.Eval.EvalId, uuid), (init.EvalSetId, init.RunId, init.EvalId, init.SampleId));
        Assert.Equal(1, init.Summary.Id);
        Assert.Equal(1, init.Summary.Epoch);
        Assert.Equal("sample_1", init.Summary.Input.Text);
        Assert.Equal("Paris", init.Summary.Target.Text);
        Assert.Equal(uuid, init.Summary.Uuid);
        var start = Assert.Single(hook.Of<SampleStart>());
        Assert.Equal((log.Eval.RunId, log.Eval.EvalId, uuid), (start.RunId, start.EvalId, start.SampleId));
        Assert.Equal(init.Summary, start.Summary);
        var attemptStart = Assert.Single(hook.Of<SampleAttemptStart>());
        Assert.Equal((log.Eval.RunId, log.Eval.EvalId, uuid, 1), (attemptStart.RunId, attemptStart.EvalId, attemptStart.SampleId, attemptStart.Attempt));
        var scoring = Assert.Single(hook.Of<SampleScoring>());
        Assert.Equal((log.Eval.RunId, log.Eval.EvalId, uuid), (scoring.RunId, scoring.EvalId, scoring.SampleId));
        var attemptEnd = Assert.Single(hook.Of<SampleAttemptEnd>());
        Assert.Equal((uuid, 1, false), (attemptEnd.SampleId, attemptEnd.Attempt, attemptEnd.WillRetry));
        Assert.Null(attemptEnd.Error);
        var end = Assert.Single(hook.Of<SampleEnd>());
        Assert.Equal((log.Eval.RunId, log.Eval.EvalId, uuid), (end.RunId, end.EvalId, end.SampleId));
        Assert.Same(sample, end.Sample);

        // model hooks: one per provider attempt (retry included) and the cache hit
        var befores = hook.Of<BeforeModelGenerate>();
        Assert.Equal(3, befores.Count);
        Assert.All(befores, before =>
        {
            Assert.Equal("scripted", before.ModelName);
            Assert.Equal("sample_1", Assert.IsType<ChatMessageUser>(Assert.Single(before.Input)).Text);
            Assert.Empty(before.Tools);
            Assert.Same(ToolChoice.None, before.ToolChoice);
            Assert.Equal(CacheMode.Write, before.Cache);
            Assert.Equal((null, log.Eval.RunId, log.Eval.EvalId, uuid, "geo"), (before.EvalSetId, before.RunId, before.EvalId, before.SampleId, before.TaskName));
        });
        var retry = Assert.Single(hook.Of<ModelRetry>());
        Assert.Equal("scripted", retry.ModelName);
        Assert.Equal(1, retry.Attempt);
        Assert.Equal(Assert.Single(delays).TotalSeconds, retry.WaitTime);
        Assert.Equal("RequestFailedException", retry.ExceptionType);
        Assert.Equal(503, retry.StatusCode);
        Assert.Equal((log.Eval.RunId, log.Eval.EvalId, uuid, "geo"), (retry.RunId, retry.EvalId, retry.SampleId, retry.TaskName));
        var usage = Assert.Single(hook.Of<ModelUsageData>());
        Assert.Equal("scripted", usage.ModelName);
        Assert.Equal((5, 1, 6), (usage.Usage.InputTokens, usage.Usage.OutputTokens, usage.Usage.TotalTokens));
        Assert.Equal(1, usage.Retries);
        Assert.True(usage.CallDuration >= 0);
        Assert.Equal((null, log.Eval.RunId, log.Eval.EvalId, "geo"), (usage.EvalSetId, usage.RunId, usage.EvalId, usage.TaskName));
        var cached = Assert.Single(hook.Of<ModelCacheUsageData>());
        Assert.Equal("scripted", cached.ModelName);
        Assert.Equal((5, 1, 6), (cached.Usage.InputTokens, cached.Usage.OutputTokens, cached.Usage.TotalTokens));

        // sample events: every event recorded from the solvers span to the end of scoring, in order, all before the
        // attempt end (Python drains the emitter first)
        var delivered = hook.Of<SampleEvent>();
        Assert.All(delivered, e => Assert.Equal((log.Eval.RunId, log.Eval.EvalId, uuid), (e.RunId, e.EvalId, e.SampleId)));
        var expected = sample.Events.SkipWhile(e => e is not SpanBeginEvent { Name: "solvers" }).ToList();
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Select(e => e.Uuid), delivered.Select(e => e.Event.Uuid));
        Assert.Equal("span_begin", delivered[0].Event.Event);
        var modelEvents = delivered.Select(e => e.Event).OfType<ModelEvent>().ToList();
        Assert.Equal(new CacheMode?[] { CacheMode.Write, CacheMode.Write, CacheMode.Read }, modelEvents.Select(e => e.Cache));
        Assert.Contains(delivered.Select(e => e.Event), e => e is ScoreEvent);
        Assert.All(delivered, e => Assert.NotEqual(true, e.Event.Pending));
        var lastEvent = hook.Sequence.ToList().FindLastIndex(e => e.Name == "sample_event");
        Assert.True(lastEvent < hook.IndexOf("sample_attempt_end"));
        Assert.True(hook.IndexOf("sample_attempt_end") < hook.IndexOf("sample_end"));
    }

    [Fact]
    public async Task a_run_with_no_hooks_registered_works()
    {
        var log = await Eval.RunAsync(Task1(), Options());

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Empty(HookRegistry.All);
    }

    [Fact]
    public async Task respects_enabled()
    {
        var hook = Register();
        hook.ShouldEnable = false;

        await Eval.RunAsync(Task1(), Options());
        Assert.Empty(hook.Sequence);

        hook.ShouldEnable = true;
        await Eval.RunAsync(Task1(), Options());
        Assert.Single(hook.Of<RunStart>());
    }

    [Fact]
    public async Task task_start_carries_the_plan_including_the_setup_step()
    {
        var hook = Register();

        var log = await Eval.RunAsync(Task1(setup: (state, _, _) => Task.FromResult(state)), Options());

        var taskStart = Assert.Single(hook.Of<TaskStart>());
        Assert.Equal(["setup", "solver"], taskStart.Plan.Steps.Select(step => step.Solver));
        Assert.Same(log.Plan, taskStart.Plan);
    }

    [Fact]
    public async Task multiple_registered_hooks_and_per_run_hooks_all_receive_every_event()
    {
        var first = Register("first");
        var second = Register("second");
        var perRun = new RecordingHooks();
        var api = new ScriptedModelApi(Turn("Paris"));

        await Eval.RunAsync(Task1(), Options(api) with { Hooks = [perRun] });

        foreach (var hook in new[] { first, second, perRun })
        {
            Assert.Single(hook.Of<RunStart>());
            Assert.Single(hook.Of<RunEnd>());
            Assert.Single(hook.Of<TaskStart>());
            Assert.Single(hook.Of<TaskEnd>());
            Assert.Single(hook.Of<SampleInit>());
            Assert.Single(hook.Of<SampleStart>());
            Assert.Single(hook.Of<SampleAttemptStart>());
            Assert.Single(hook.Of<SampleAttemptEnd>());
            Assert.Single(hook.Of<SampleEnd>());
            Assert.Single(hook.Of<ModelUsageData>());
            Assert.Single(hook.Of<BeforeModelGenerate>());
            Assert.NotEmpty(hook.Of<SampleEvent>());
        }

        Assert.Equal(first.Of<SampleEvent>().Count, perRun.Of<SampleEvent>().Count);
        Assert.Equal(first.Lifecycle, perRun.Lifecycle);
    }

    [Fact]
    public async Task per_run_hooks_are_notified_after_the_registry_hooks()
    {
        var order = new List<string>();
        var registry = new OrderRecordingHooks("registry", order);
        HookRegistry.Register(registry, "registry", "registry hook");
        var perRun = new OrderRecordingHooks("per-run", order);

        await Eval.RunAsync(Task1(), Options() with { Hooks = [perRun] });

        Assert.Equal(["registry", "per-run"], order.Take(2));
        // outside a sample only the registry applies; inside, the run's hooks (registry first)
        Assert.Equal([registry], HookEmitter.ActiveHooks);
    }

    private sealed class OrderRecordingHooks(string name, List<string> order) : Hooks
    {
        public override Task OnRunStartAsync(RunStart data, CancellationToken cancellationToken)
        {
            order.Add(name);
            return Task.CompletedTask;
        }
    }

    // ------------------------------------------------------------------------------------- samples / retries

    [Fact]
    public async Task sample_retries_fire_the_attempt_hooks_per_attempt_and_the_sample_hooks_once()
    {
        var hook = Register();
        var task = Task1(solver: FailingDeterministic(true, true, false));

        var log = await Eval.RunAsync(task, Options() with { RetryOnError = 10 });

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Single(hook.Of<SampleInit>());
        Assert.Single(hook.Of<SampleStart>());
        Assert.Single(hook.Of<SampleEnd>());
        var starts = hook.Of<SampleAttemptStart>();
        var ends = hook.Of<SampleAttemptEnd>();
        Assert.Equal([1, 2, 3], starts.Select(e => e.Attempt));
        Assert.Equal([1, 2, 3], ends.Select(e => e.Attempt));
        Assert.All(ends.Take(2), e =>
        {
            Assert.NotNull(e.Error);
            Assert.Equal("Eval failed!", e.Error!.Message);
            Assert.True(e.WillRetry);
        });
        Assert.Null(ends[2].Error);
        Assert.False(ends[2].WillRetry);

        // the uuid is stable across attempts and matches the logged sample
        var uuid = Assert.Single(log.Samples!).Uuid!;
        var ids = hook.Sequence.Select(e => e.Data).Select(SampleIdOf).OfType<string>().Distinct().ToList();
        Assert.Equal([uuid], ids);
        // attempts are paired and interleaved: start(1) end(1) start(2) end(2) start(3) end(3)
        Assert.Equal(
            ["sample_attempt_start", "sample_attempt_end", "sample_attempt_start", "sample_attempt_end", "sample_attempt_start", "sample_attempt_end"],
            hook.Lifecycle.Where(name => name.StartsWith("sample_attempt", StringComparison.Ordinal)));
    }

    private static string? SampleIdOf(object data) => data switch
    {
        SampleInit e => e.SampleId,
        SampleStart e => e.SampleId,
        SampleAttemptStart e => e.SampleId,
        SampleAttemptEnd e => e.SampleId,
        SampleEvent e => e.SampleId,
        SampleEnd e => e.SampleId,
        SampleScoring e => e.SampleId,
        BeforeModelGenerate e => e.SampleId,
        ModelRetry e => e.SampleId,
        _ => null,
    };

    [Fact]
    public async Task exhausted_retries_end_with_an_error_and_no_further_retry()
    {
        var hook = Register();
        var task = Task1(solver: FailingDeterministic(true, true, true, true)) with { FailOnError = FailOnError.Never };

        var log = await Eval.RunAsync(task, Options() with { RetryOnError = 2 });

        Assert.Equal(EvalStatus.Success, log.Status);
        var ends = hook.Of<SampleAttemptEnd>();
        Assert.Equal(3, hook.Of<SampleAttemptStart>().Count);
        Assert.Equal(3, ends.Count);
        Assert.All(ends.Take(2), e => Assert.True(e.Error is not null && e.WillRetry));
        Assert.NotNull(ends[2].Error);
        Assert.False(ends[2].WillRetry);
        Assert.Single(hook.Of<SampleEnd>());
        Assert.NotNull(Assert.Single(hook.Of<SampleEnd>()).Sample.Error);
        Assert.Null(Assert.Single(hook.Of<RunEnd>()).Exception);
    }

    [Fact]
    public async Task an_error_with_no_retries_fires_one_attempt_and_the_scoring_hook()
    {
        var hook = Register();
        var task = Task1(solver: FailingDeterministic(true)) with { FailOnError = FailOnError.Never };

        await Eval.RunAsync(task, Options() with { RetryOnError = 0 });

        Assert.Equal(
            ["run_start", "task_start", "sample_init", "sample_start", "sample_attempt_start", "sample_scoring", "sample_attempt_end", "sample_end", "task_end", "run_end"],
            hook.Lifecycle);
        var end = Assert.Single(hook.Of<SampleAttemptEnd>());
        Assert.Equal(1, end.Attempt);
        Assert.NotNull(end.Error);
        Assert.False(end.WillRetry);
    }

    [Fact]
    public async Task multiple_samples_and_epochs_fire_the_sample_hooks_once_per_sample_epoch()
    {
        var hook = Register();
        var api = new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.Text("Paris"), 4));

        await Eval.RunAsync(Task1(samples: 2), Options(api) with { Epochs = 2 });

        Assert.Single(hook.Of<RunStart>());
        Assert.Single(hook.Of<TaskStart>());
        Assert.Equal(4, hook.Of<SampleInit>().Count);
        Assert.Equal(4, hook.Of<SampleStart>().Count);
        Assert.Equal(4, hook.Of<SampleEnd>().Count);
        Assert.Equal(4, hook.Of<SampleAttemptStart>().Count);
        Assert.Equal(4, hook.Of<SampleAttemptEnd>().Count);
        var initIds = hook.Of<SampleInit>().Select(e => e.SampleId).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(4, initIds.Count);
        Assert.Equal(initIds, hook.Of<SampleEnd>().Select(e => e.SampleId).ToHashSet(StringComparer.Ordinal));
        Assert.Equal(4, hook.Of<SampleEvent>().Select(e => e.SampleId).Distinct().Count());
        Assert.Equal(
            hook.Of<SampleAttemptStart>().Select(e => (e.SampleId, e.Attempt)).Order(),
            hook.Of<SampleAttemptEnd>().Select(e => (e.SampleId, e.Attempt)).Order());
        Assert.Equal([(1, 1), (1, 2), (2, 1), (2, 2)], hook.Of<SampleInit>().Select(e => ((int)e.Summary.Id, e.Summary.Epoch)).Order());
    }

    [Fact]
    public async Task eval_set_id_flows_into_the_log_and_every_payload()
    {
        var hook = Register();

        var log = await Eval.RunAsync(Task1(), Options(new ScriptedModelApi(Turn("Paris"))) with { EvalSetId = "set-1" });

        Assert.Equal("set-1", log.Eval.EvalSetId);
        Assert.Equal("set-1", Assert.Single(hook.Of<RunStart>()).EvalSetId);
        Assert.Equal("set-1", Assert.Single(hook.Of<TaskStart>()).EvalSetId);
        Assert.Equal("set-1", Assert.Single(hook.Of<SampleInit>()).EvalSetId);
        Assert.Equal("set-1", Assert.Single(hook.Of<BeforeModelGenerate>()).EvalSetId);
        Assert.Equal("set-1", Assert.Single(hook.Of<ModelUsageData>()).EvalSetId);
        Assert.All(hook.Of<SampleEvent>(), e => Assert.Equal("set-1", e.EvalSetId));
        Assert.Equal("set-1", Assert.Single(hook.Of<RunEnd>()).EvalSetId);
    }

    // ------------------------------------------------------------------------------- failure semantics

    [Fact]
    public async Task a_failing_hook_is_logged_and_the_eval_and_the_other_hooks_are_unaffected()
    {
        HookRegistry.Register(new ThrowingHooks("sample_start", () => new InvalidOperationException("boom")), "throwing", "throws");
        var hook = Register();

        var log = await Eval.RunAsync(Task1(), Options());

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Null(Assert.Single(log.Samples!).Error);
        Assert.Contains("Exception calling hook 'ThrowingHooks': boom", ProviderLogger.Warnings);
        Assert.Single(hook.Of<SampleStart>());
        Assert.Single(hook.Of<SampleEnd>());
    }

    [Fact]
    public async Task a_failing_sample_event_hook_is_logged_and_later_events_still_arrive()
    {
        var calls = 0;
        var hook = Register();
        HookRegistry.Register(new ThrowingHooks("sample_event", () => calls++ == 0 ? new InvalidOperationException("event boom") : new LimitExceededException("custom", 1, 1, "late limit")), "throwing", "throws");

        var log = await Eval.RunAsync(Task1(), Options());

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Null(Assert.Single(log.Samples!).Limit);
        Assert.Contains("Exception calling hook 'ThrowingHooks': event boom", ProviderLogger.Warnings);
        // a limit raised from the background emitter cannot reach the sample: it is logged (Python's emit loop)
        Assert.Contains(ProviderLogger.Warnings, warning => warning.StartsWith("Exception in sample event emitter:", StringComparison.Ordinal));
        Assert.True(hook.Of<SampleEvent>().Count > 2);
    }

    [Fact]
    public async Task a_limit_exceeded_thrown_by_a_hook_propagates_and_ends_the_sample_with_that_limit()
    {
        HookRegistry.Register(new ThrowingHooks("before_model_generate", () => new LimitExceededException("custom", 1, 1, "hook says stop")), "limiting", "limits");
        var hook = Register();

        var log = await Eval.RunAsync(Task1(), Options());

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);
        Assert.Equal(("custom", 1.0, "hook says stop"), (sample.Limit!.Type, sample.Limit.Limit, sample.Limit.Reason));
        Assert.Empty(hook.Of<ModelUsageData>());
        Assert.Single(hook.Of<SampleScoring>());
        Assert.Single(hook.Of<SampleEnd>());
        Assert.DoesNotContain(ProviderLogger.Warnings, warning => warning.StartsWith("Exception calling hook", StringComparison.Ordinal));
    }

    [Fact]
    public async Task no_attempt_end_is_emitted_without_an_attempt_start()
    {
        HookRegistry.Register(new ThrowingHooks("sample_start", () => new LimitExceededException("custom", 1, 1, "pre-attempt failure")), "limiting", "limits");
        var hook = Register();

        var log = await Eval.RunAsync(Task1(), Options());

        var sample = Assert.Single(log.Samples!);
        Assert.Equal("custom", sample.Limit!.Type);
        Assert.Single(hook.Of<SampleInit>());
        Assert.Empty(hook.Of<SampleStart>());
        Assert.Empty(hook.Of<SampleAttemptStart>());
        Assert.Empty(hook.Of<SampleAttemptEnd>());
        Assert.Single(hook.Of<SampleScoring>());
        Assert.Single(hook.Of<SampleEnd>());
    }

    [Fact]
    public async Task a_hooks_own_cancellation_is_a_hook_failure_while_run_cancellation_propagates()
    {
        HookRegistry.Register(new ThrowingHooks("sample_start", () => new TaskCanceledException("hook timed out")), "timing-out", "times out");
        var hook = Register();

        var log = await Eval.RunAsync(Task1(), Options());

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Contains("Exception calling hook 'ThrowingHooks': hook timed out", ProviderLogger.Warnings);
        HookRegistry.Clear();

        using var cts = new CancellationTokenSource();
        HookRegistry.Register(new CancellingHooks(cts), "cancelling", "cancels the run");
        hook = Register();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Eval.RunAsync(Task1(), Options(), cts.Token));

        var taskEnd = Assert.Single(hook.Of<TaskEnd>());
        Assert.Equal(EvalStatus.Cancelled, taskEnd.Log.Status);
        var runEnd = Assert.Single(hook.Of<RunEnd>());
        Assert.IsAssignableFrom<OperationCanceledException>(runEnd.Exception);
        Assert.Same(taskEnd.Log, Assert.Single(runEnd.Logs));
        Assert.Single(hook.Of<SampleEnd>());
    }

    [Fact]
    public async Task run_end_carries_the_exception_when_the_run_throws_before_starting()
    {
        var hook = Register();
        _env.Set(HookStartup.RequiredHooksVar, "test_hooks,fake");

        var ex = await Assert.ThrowsAsync<PrerequisiteError>(() => Eval.RunAsync(Task1(), Options()));

        Assert.Contains("Required hook(s) missing: {'fake'}.", ex.Message);
        Assert.Contains("INSPECT_REQUIRED_HOOKS is set to 'test_hooks,fake'.", ex.Message);
        Assert.Contains("Installed hooks: {'test_hooks'}.", ex.Message);
        Assert.Equal(["run_end"], hook.Lifecycle);
        var runEnd = Assert.Single(hook.Of<RunEnd>());
        Assert.Same(ex, runEnd.Exception);
        Assert.Empty(runEnd.Logs);
        Assert.NotEmpty(runEnd.RunId);
    }

    // ------------------------------------------------------------------------------------------ startup

    [Fact]
    public async Task required_hooks_are_verified_once_and_the_enabled_hooks_announced()
    {
        Register("test_hooks");
        var disabled = Register("test_hooks_2");
        disabled.ShouldEnable = false;
        _env.Set(HookStartup.RequiredHooksVar, "test_hooks");
        var reporter = new CapturingReporter();

        var messages = HookStartup.InitHooks(reporter.Message);

        var message = Assert.Single(messages);
        Assert.Equal("hooks enabled: 1\n  test_hooks: test_hooks-description", message);
        var banner = Assert.Single(reporter.Messages);
        Assert.StartsWith("InspectAzureAI v", banner, StringComparison.Ordinal);
        Assert.EndsWith("\n- hooks enabled: 1\n  test_hooks: test_hooks-description", banner, StringComparison.Ordinal);

        // later calls (and the run's own call) do nothing more
        Assert.Empty(HookStartup.InitHooks(reporter.Message));
        var log = await Eval.RunAsync(Task1(), Options() with { Reporter = reporter });
        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Single(reporter.Messages, m => m.StartsWith("InspectAzureAI v", StringComparison.Ordinal));
    }

    [Fact]
    public void verify_required_hooks_ignores_empty_entries_and_reports_missing_names_sorted()
    {
        var hook = Register("b_hook");
        _env.Set(HookStartup.RequiredHooksVar, "");
        HookStartup.VerifyAllRequiredHooks([hook]);
        _env.Set(HookStartup.RequiredHooksVar, "b_hook,,");
        HookStartup.VerifyAllRequiredHooks([hook]);

        _env.Set(HookStartup.RequiredHooksVar, "z_hook,a_hook,b_hook");
        var ex = Assert.Throws<PrerequisiteError>(() => HookStartup.VerifyAllRequiredHooks([hook]));
        Assert.Contains("missing: {'a_hook', 'z_hook'}.", ex.Message);
        Assert.Contains("Installed hooks: {'b_hook'}.", ex.Message);
    }

    // ----------------------------------------------------------------------------------------- registry

    [Fact]
    public void registry_records_name_and_description_replaces_by_name_and_rejects_double_registration()
    {
        var a = new RecordingHooks();
        var b = new RecordingHooks();
        HookRegistry.Register(a, "n", "a's description");

        Assert.Equal(new HookInfo("n", "a's description"), HookRegistry.Info(a));
        Assert.Same(a, HookRegistry.Lookup("n"));
        Assert.Equal([a], HookRegistry.All);

        HookRegistry.Register(b, "n", "b's description");
        Assert.Equal([b], HookRegistry.All);
        Assert.Null(HookRegistry.Info(a));
        Assert.Equal("b's description", HookRegistry.Info(b)!.Description);

        var ex = Assert.Throws<InvalidOperationException>(() => HookRegistry.Register(b, "m", "again"));
        Assert.Contains("already registered as 'n'", ex.Message);
        HookRegistry.Register(b, "n", "re-registered under its own name is fine");

        Assert.False(HookRegistry.Unregister("m"));
        Assert.True(HookRegistry.Unregister("n"));
        Assert.Empty(HookRegistry.All);
        Assert.Null(HookRegistry.Lookup("n"));
    }

    [Fact]
    public async Task eval_set_entry_points_reach_the_registry_hooks()
    {
        var hook = Register();

        await HookEmitter.EmitEvalSetStartAsync("es-1", "/logs/dir");
        await HookEmitter.EmitEvalSetEndAsync("es-1", "/logs/dir");

        Assert.Equal(["eval_set_start", "eval_set_end"], hook.Lifecycle);
        Assert.Equal(new EvalSetStart("es-1", "/logs/dir"), Assert.Single(hook.Of<EvalSetStart>()));
        Assert.Equal(new EvalSetEnd("es-1", "/logs/dir"), Assert.Single(hook.Of<EvalSetEnd>()));
    }

    [Fact]
    public async Task model_retry_emitted_outside_a_sample_has_no_eval_ids()
    {
        var hook = Register();

        await HookEmitter.EmitModelRetryAsync("scripted", 2, 1.5);
        await HookEmitter.EmitModelRetryAsync("scripted", 1, 0.5, new RetryErrorInfo("RateLimitError", 429));

        var retries = hook.Of<ModelRetry>();
        Assert.Equal(2, retries.Count);
        Assert.Equal(("scripted", 2, 1.5), (retries[0].ModelName, retries[0].Attempt, retries[0].WaitTime));
        Assert.Null(retries[0].SampleId);
        Assert.Null(retries[0].EvalId);
        Assert.Null(retries[0].ExceptionType);
        Assert.Null(retries[0].StatusCode);
        Assert.Equal(("RateLimitError", 429), (retries[1].ExceptionType, retries[1].StatusCode));
    }

    [Fact]
    public async Task model_hooks_outside_a_run_use_the_registry_and_carry_no_eval_ids()
    {
        var hook = Register();
        var model = new Model(new ScriptedModelApi(ScriptedTurn.Text("ok", new ModelUsage(2, 1, 3))));

        await model.GenerateAsync("hi");

        Assert.Equal(["before_model_generate", "model_usage"], hook.Lifecycle);
        var before = Assert.Single(hook.Of<BeforeModelGenerate>());
        Assert.Null(before.RunId);
        Assert.Null(before.SampleId);
        Assert.Null(before.TaskName);
        Assert.Null(before.Cache);
        var usage = Assert.Single(hook.Of<ModelUsageData>());
        Assert.Null(usage.EvalId);
        Assert.Equal(0, usage.Retries);
        Assert.Equal(3, usage.Usage.TotalTokens);
    }

    [Fact]
    public async Task pending_events_are_not_delivered()
    {
        var hook = Register();

        await HookEmitter.EmitSampleEventAsync(null, "run", "eval", "sample", new InfoEvent("test", null) { Pending = true });
        Assert.Empty(hook.Sequence);

        await HookEmitter.EmitSampleEventAsync(null, "run", "eval", "sample", new InfoEvent("test", null));
        var delivered = Assert.Single(hook.Of<SampleEvent>());
        Assert.Equal(("run", "eval", "sample", "info"), (delivered.RunId, delivered.EvalId, delivered.SampleId, delivered.Event.Event));
    }

    // ---------------------------------------------------------------------------------- api key override

    [Fact]
    public void has_api_key_override_reflects_whether_any_registered_hook_overrides_it()
    {
        Assert.False(HookRegistry.HasApiKeyOverride);

        var minimal = Register("minimal");
        Assert.False(HookRegistry.HasApiKeyOverride);
        Assert.False(HookRegistry.OverridesApiKey(minimal));

        var keyed = new ApiKeyHooks();
        HookRegistry.Register(keyed, "keyed", "overrides api keys");
        Assert.True(HookRegistry.HasApiKeyOverride);
        Assert.True(HookRegistry.OverridesApiKey(keyed));
        // enabled or not, as in Python
        keyed.ShouldEnable = false;
        Assert.True(HookRegistry.HasApiKeyOverride);
    }

    [Fact]
    public void override_api_key_asks_the_enabled_hooks_in_order_and_logs_a_failing_one()
    {
        HookRegistry.Register(new ThrowingKeyHooks(), "throwing", "throws");
        var keyed = new ApiKeyHooks();
        HookRegistry.Register(keyed, "keyed", "overrides api keys");

        Assert.Equal("mocked-TEST_VAR-test_value", HookRegistry.OverrideApiKey("TEST_VAR", "test_value"));
        Assert.Contains("Exception calling override_api_key on hook 'ThrowingKeyHooks': vault down", ProviderLogger.Warnings);

        keyed.ShouldEnable = false;
        Assert.Null(HookRegistry.OverrideApiKey("TEST_VAR", "test_value"));
    }

    [Fact]
    public void apply_api_key_overrides_follows_the_three_python_branches()
    {
        const string key = "INSPECT_TEST_API_KEY";
        _env.Set(key, null);

        // no override hook: nothing is asked, the key and the environment are untouched
        Register("minimal");
        Assert.Equal("given", ApiKeyOverrides.Apply([key], "given"));
        Assert.Null(ApiKeyOverrides.Apply([key], null));
        Assert.Null(Environment.GetEnvironmentVariable(key));
        HookRegistry.Clear();
        HookRegistry.Register(new ApiKeyHooks(), "keyed", "overrides api keys");

        // an explicit key is offered to the hooks and replaced by their answer
        Assert.Equal($"mocked-{key}-given", ApiKeyOverrides.Apply([key], "given"));
        Assert.Null(Environment.GetEnvironmentVariable(key));

        // an environment value is offered and the environment updated with the answer
        _env.Set(key, "from-env");
        Assert.Null(ApiKeyOverrides.Apply([key], null));
        Assert.Equal($"mocked-{key}-from-env", Environment.GetEnvironmentVariable(key));

        // no key anywhere: the hook is still asked (with an empty value) so it can supply its own credentials
        _env.Set(key, null);
        Assert.Equal($"mocked-{key}-", ApiKeyOverrides.Apply([key], null));
        Assert.Null(Environment.GetEnvironmentVariable(key));
    }

    [Fact]
    public async Task an_auth_failure_is_retried_only_when_a_hook_overrides_api_keys()
    {
        var delays = new List<TimeSpan>();
        ModelRetryOptions retry = new(MaxRetries: 3, Delay: (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });

        // no override hook: a 401 is terminal (the scripted api's default decision)
        Register("minimal");
        var model = new Model(new ScriptedModelApi(ScriptedTurn.Throw(Http(401)), ScriptedTurn.Text("never")), retry: retry);
        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => model.GenerateAsync("hi"));
        Assert.Equal(401, ex.Status);
        Assert.Empty(delays);
        HookRegistry.Clear();

        // an override hook makes the auth failure retryable (Python's should_retry: has_api_key_override + is_auth_failure)
        var keyed = new ApiKeyHooks();
        HookRegistry.Register(keyed, "keyed", "overrides api keys");
        model = new Model(new ScriptedModelApi(ScriptedTurn.Throw(Http(401)), Turn("ok")), retry: retry);
        var output = await model.GenerateAsync("hi");

        Assert.Equal("ok", output.Completion);
        Assert.Single(delays);
        var modelRetry = Assert.Single(keyed.Of<ModelRetry>());
        Assert.Equal((1, "RequestFailedException", 401), (modelRetry.Attempt, modelRetry.ExceptionType, modelRetry.StatusCode));
        Assert.Equal(1, Assert.Single(keyed.Of<ModelUsageData>()).Retries);
    }
}

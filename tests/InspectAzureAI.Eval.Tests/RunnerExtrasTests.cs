using System.Text.Json;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;
using Scorers = InspectAzureAI.Eval.Scorers.Scorers;
using Solvers = InspectAzureAI.Eval.Solvers.Solvers;

/// <summary>
/// The runner extras: the <c>fail_on_error</c> policy (<c>_eval/task/error.py</c>), per-sample retries and early
/// stopping (<c>_eval/task/run.py</c>), the <c>sample_id</c> filter (<c>_eval/task/util.py</c>), the scoped limit
/// stack with turn and working limits (<c>util/_limit.py</c>), <c>SampleLimitEvent</c> and working-time accounting.
/// </summary>
public sealed class RunnerExtrasTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_logDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private EvalOptions Options(ScriptedModelApi? api = null, ModelRetryOptions? retry = null) =>
        new() { Model = new Model(api ?? new ScriptedModelApi(), retry: retry), LogDir = _logDir, MaxSamples = 1, LogFormat = LogFormat.Json };

    private static Solver Failing(Func<TaskState, bool> fail) =>
        (state, _, _) => fail(state) ? throw new InvalidOperationException("Eval failed!") : Task.FromResult(state);

    /// <summary>Port of <c>failing_solver_deterministic</c>: fails or succeeds per call in the given order (succeeds once exhausted).</summary>
    private static Solver FailingDeterministic(params bool[] failures)
    {
        var queue = new Queue<bool>(failures);
        return (state, _, _) => queue.Count > 0 && queue.Dequeue() ? throw new InvalidOperationException("Eval failed!") : Task.FromResult(state);
    }

    private static EvalTask FailingTask(int samples, FailOnError? failOnError, Func<TaskState, bool>? fail = null, bool? continueOnFail = null, int epochs = 1) => new()
    {
        Name = "failing",
        Dataset = new MemoryDataset(Enumerable.Range(1, samples).Select(_ => new Sample("Say hello.") { Target = "Hello" }), name: "hello"),
        Solver = Solvers.Chain(Failing(fail ?? (_ => true)), Solvers.Generate()),
        FailOnError = failOnError ?? FailOnError.Always,
        ContinueOnFail = continueOnFail,
        Epochs = epochs > 1 ? new Epochs(epochs) : null,
    };

    private static int Id(TaskState state) => (int)state.SampleId;

    // ---------------------------------------------------------------- fail_on_error

    [Fact]
    public async Task fail_on_error_true_fails_the_eval_on_the_first_error()
    {
        var log = await Eval.RunAsync(FailingTask(1, true), Options());

        Assert.Equal(EvalStatus.Error, log.Status);
        Assert.Equal("Eval failed!", log.Error!.Message);
        Assert.Equal(FailOnError.Always, log.Eval.Config.FailOnError);
    }

    [Fact]
    public async Task fail_on_error_false_never_fails_the_eval()
    {
        var log = await Eval.RunAsync(FailingTask(1, false), Options());

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Null(log.Error);
        var sample = Assert.Single(log.Samples!);
        Assert.Equal("Eval failed!", sample.Error!.Message);
        Assert.Empty(sample.ErrorRetries!);
        Assert.Equal(FailOnError.Never, log.Eval.Config.FailOnError);
    }

    [Fact]
    public async Task continue_on_fail_runs_every_sample_and_fails_the_log_at_the_end()
    {
        var log = await Eval.RunAsync(FailingTask(2, true, state => Id(state) == 1, continueOnFail: true), Options());

        Assert.Equal(EvalStatus.Error, log.Status);
        Assert.Null(log.Error);
        Assert.Equal(2, log.Samples!.Count);
        Assert.Equal(1, log.Results!.CompletedSamples);
        Assert.True(log.Eval.Config.ContinueOnFail);
    }

    [Fact]
    public async Task an_absolute_count_fails_the_eval_once_that_many_samples_error()
    {
        var failed = await Eval.RunAsync(FailingTask(10, 4, state => Id(state) < 5), Options());
        var passed = await Eval.RunAsync(FailingTask(10, 4, state => Id(state) < 3), Options());

        Assert.Equal(EvalStatus.Error, failed.Status);
        Assert.Equal("Eval failed!", failed.Error!.Message);
        Assert.Equal(EvalStatus.Success, passed.Status);
        Assert.Equal(8, passed.Results!.CompletedSamples);
        Assert.Equal(FailOnError.Count(4), passed.Eval.Config.FailOnError);
    }

    [Fact]
    public async Task an_absolute_count_with_continue_on_fail_finishes_the_run()
    {
        var failed = await Eval.RunAsync(FailingTask(10, 4, state => Id(state) < 5, continueOnFail: true), Options());
        var passed = await Eval.RunAsync(FailingTask(10, 4, state => Id(state) < 3, continueOnFail: true), Options());

        Assert.Equal(6, failed.Results!.CompletedSamples);
        Assert.Equal(EvalStatus.Error, failed.Status);
        Assert.Equal(8, passed.Results!.CompletedSamples);
        Assert.Equal(EvalStatus.Success, passed.Status);
    }

    [Fact]
    public async Task a_fraction_fails_the_eval_once_that_share_of_the_sample_runs_error()
    {
        var failed = await Eval.RunAsync(FailingTask(10, 0.35, state => Id(state) < 5), Options());
        var passed = await Eval.RunAsync(FailingTask(10, 0.7, state => Id(state) < 7), Options());

        Assert.Equal(EvalStatus.Error, failed.Status);
        Assert.Equal(EvalStatus.Success, passed.Status);
        Assert.Equal(4, passed.Results!.CompletedSamples);
    }

    [Fact]
    public async Task a_fraction_with_continue_on_fail_finishes_the_run()
    {
        var failed = await Eval.RunAsync(FailingTask(10, 0.35, state => Id(state) < 5, continueOnFail: true), Options());
        var passed = await Eval.RunAsync(FailingTask(10, 0.7, state => Id(state) < 7, continueOnFail: true), Options());

        Assert.Equal(6, failed.Results!.CompletedSamples);
        Assert.Equal(EvalStatus.Error, failed.Status);
        Assert.Equal(4, passed.Results!.CompletedSamples);
        Assert.Equal(EvalStatus.Success, passed.Status);
    }

    [Fact]
    public async Task a_fraction_counts_against_dataset_times_epochs()
    {
        // 2 samples × 10 epochs = 20 runs; sample 1 fails every epoch (10 errors = 50%), below the 60% threshold
        var log = await Eval.RunAsync(FailingTask(2, 0.6, state => Id(state) == 1, epochs: 10), Options());

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal(10, log.Results!.CompletedSamples);
        Assert.Equal(20, log.Results.TotalSamples);
    }

    [Fact]
    public async Task a_fraction_counts_against_the_sliced_dataset()
    {
        var log = await Eval.RunAsync(FailingTask(10, 0.5, state => Id(state) == 1), Options() with { Limit = 4 });

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal(3, log.Results!.CompletedSamples);
    }

    [Fact]
    public async Task the_eval_options_override_the_tasks_policy()
    {
        var overridden = await Eval.RunAsync(FailingTask(10, 0.7, state => Id(state) < 7), Options() with { FailOnError = 0.6 });
        var continued = await Eval.RunAsync(FailingTask(10, true, state => Id(state) < 7), Options() with { ContinueOnFail = true });

        Assert.Equal(EvalStatus.Error, overridden.Status);
        Assert.Equal(FailOnError.Threshold(0.6), overridden.Eval.Config.FailOnError);
        Assert.Equal(4, continued.Results!.CompletedSamples);
        Assert.Equal(EvalStatus.Error, continued.Status);
    }

    [Theory]
    [InlineData(1, true, true)]
    [InlineData(1, false, false)]
    [InlineData(0, true, false)]
    [InlineData(4, 4.0, true)]
    [InlineData(3, 4.0, false)]
    [InlineData(4, 0.35, true)]
    [InlineData(3, 0.35, false)]
    [InlineData(6, 0.7, false)]
    [InlineData(7, 0.7, true)]
    [InlineData(0, 0.0, true)]
    [InlineData(1, 1.0, true)]
    [InlineData(0, 1.0, false)]
    public void should_fail_matches_python(int errorCount, object policy, bool expected)
    {
        FailOnError failOnError = policy is bool flag ? flag : (double)policy;

        Assert.Equal(expected, failOnError.ShouldFail(errorCount, 10));
        Assert.Equal(expected, SampleErrorHandler.ShouldEvalFail(errorCount, 10, failOnError));
    }

    [Fact]
    public void an_unset_policy_fails_on_any_error_like_python_none()
    {
        Assert.True(SampleErrorHandler.ShouldEvalFail(1, 10, null));
        Assert.False(SampleErrorHandler.ShouldEvalFail(0, 10, null));
    }

    [Fact]
    public void fail_on_error_serialises_as_a_boolean_or_number()
    {
        var options = EvalLogWriter.Options;

        Assert.Equal("{\"fail_on_error\":true}", JsonSerializer.Serialize(new EvalConfig { FailOnError = true }, options).Replace("\n", "").Replace(" ", ""));
        Assert.Equal("{\"fail_on_error\":false}", JsonSerializer.Serialize(new EvalConfig { FailOnError = false }, options).Replace("\n", "").Replace(" ", ""));
        Assert.Equal("{\"fail_on_error\":0.5}", JsonSerializer.Serialize(new EvalConfig { FailOnError = 0.5 }, options).Replace("\n", "").Replace(" ", ""));
        Assert.Equal("{\"fail_on_error\":4}", JsonSerializer.Serialize(new EvalConfig { FailOnError = 4 }, options).Replace("\n", "").Replace(" ", ""));
        Assert.Equal("{}", JsonSerializer.Serialize(new EvalConfig(), options).Replace("\n", "").Replace(" ", ""));

        Assert.Equal(FailOnError.Threshold(0.5), JsonSerializer.Deserialize<EvalConfig>("{\"fail_on_error\": 0.5}", options)!.FailOnError);
        Assert.Equal(FailOnError.Count(4), JsonSerializer.Deserialize<EvalConfig>("{\"fail_on_error\": 4}", options)!.FailOnError);
        Assert.Equal(FailOnError.Never, JsonSerializer.Deserialize<EvalConfig>("{\"fail_on_error\": false}", options)!.FailOnError);
        Assert.Null(JsonSerializer.Deserialize<EvalConfig>("{}", options)!.FailOnError);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EvalConfig>("{\"fail_on_error\": \"yes\"}", options));
    }

    [Fact]
    public void fail_on_error_factories_reject_invalid_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FailOnError.Fraction(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FailOnError.Fraction(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => FailOnError.Count(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FailOnError.Threshold(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => FailOnError.Threshold(double.NaN));
        Assert.True(FailOnError.Fraction(0.25).IsFractional);
        Assert.False(FailOnError.Count(2).IsFractional);
        Assert.Equal("0.25", FailOnError.Fraction(0.25).ToString());
        Assert.Equal("True", FailOnError.Always.ToString());
    }

    // ---------------------------------------------------------------- retry_on_error

    [Fact]
    public async Task retry_on_error_reruns_the_sample_and_records_the_error()
    {
        var task = new EvalTask { Name = "retry", Dataset = new MemoryDataset([new Sample("x") { Target = "x" }]), Solver = FailingDeterministic(true, false) };

        var log = await Eval.RunAsync(task, Options() with { RetryOnError = 1 });

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);
        var retry = Assert.Single(sample.ErrorRetries!);
        Assert.Equal("Eval failed!", retry.Message);
        Assert.Contains("InvalidOperationException", retry.Traceback);
        Assert.Equal(1, log.Eval.Config.RetryOnError);
        Assert.Equal(1, log.Results!.CompletedSamples);
    }

    [Fact]
    public async Task without_retries_the_error_is_recorded_at_once()
    {
        var task = new EvalTask { Name = "retry", Dataset = new MemoryDataset([new Sample("x") { Target = "x" }]), Solver = FailingDeterministic(true, false), FailOnError = false };

        var log = await Eval.RunAsync(task, Options());

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.NotNull(sample.Error);
        Assert.Empty(sample.ErrorRetries!);
        Assert.Null(log.Eval.Config.RetryOnError);
    }

    [Fact]
    public async Task a_retry_starts_from_a_fresh_state()
    {
        var task = new EvalTask
        {
            Name = "retry",
            Dataset = new MemoryDataset([new Sample("x") { Target = "x" }]),
            Solver = Solvers.Chain(Solvers.SystemMessage("do your best!"), FailingDeterministic(true, false)),
        };

        var log = await Eval.RunAsync(task, Options() with { RetryOnError = 1 });

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal(["system", "user"], Assert.Single(log.Samples!).Messages.Select(m => m.Role));
    }

    [Fact]
    public async Task exhausted_retries_record_every_attempt_and_the_final_error_fails_the_eval()
    {
        var task = new EvalTask { Name = "retry", Dataset = new MemoryDataset([new Sample("x") { Target = "x" }]), Solver = FailingDeterministic(true, true, true, true) };

        var log = await Eval.RunAsync(task, Options() with { RetryOnError = 3 });

        Assert.Equal(EvalStatus.Error, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Equal(3, sample.ErrorRetries!.Count);
        Assert.Equal("Eval failed!", sample.Error!.Message);
        Assert.Single(sample.Events.OfType<ErrorEvent>());
        Assert.Equal(0, log.Results!.CompletedSamples);
    }

    [Fact]
    public async Task retries_keep_the_sample_uuid_and_epochs_get_their_own()
    {
        var uuids = new List<string>();
        var inner = FailingDeterministic(true, true, false, true, false);
        Solver solver = (state, generate, ct) =>
        {
            uuids.Add(state.Uuid);
            return inner(state, generate, ct);
        };
        var task = new EvalTask { Name = "retry", Dataset = new MemoryDataset([new Sample("x") { Target = "x" }]), Solver = solver, Epochs = new Epochs(2) };

        var log = await Eval.RunAsync(task, Options() with { RetryOnError = 3 });

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal(2, log.Samples!.Count);
        Assert.Equal(5, uuids.Count);
        Assert.Equal([2, 1], log.Samples.Select(s => s.ErrorRetries!.Count));
        Assert.Equal(log.Samples[0].Uuid, uuids[0]);
        Assert.Equal(uuids[0], uuids[1]);
        Assert.Equal(uuids[0], uuids[2]);
        Assert.Equal(log.Samples[1].Uuid, uuids[3]);
        Assert.Equal(uuids[3], uuids[4]);
        Assert.NotEqual(log.Samples[0].Uuid, log.Samples[1].Uuid);
    }

    [Fact]
    public async Task a_retry_error_carries_the_events_since_the_last_model_event()
    {
        static Solver FailingAfterGenerate()
        {
            var failures = new Queue<bool>([true, false]);
            return async (state, generate, ct) =>
            {
                state = await generate(state, cancellationToken: ct);
                SampleContext.Require().Transcript.Info("test", "about to check for failure");
                if (failures.Dequeue())
                {
                    throw new InvalidOperationException("Eval failed after generate!");
                }

                return state;
            };
        }

        var task = new EvalTask { Name = "retry", Dataset = new MemoryDataset([new Sample("x") { Target = "x" }]), Solver = FailingAfterGenerate() };

        var log = await Eval.RunAsync(task, Options(new ScriptedModelApi(ScriptedTurn.Text("first"), ScriptedTurn.Text("second"))));

        Assert.Equal(EvalStatus.Error, log.Status);
        var failed = Assert.Single(log.Samples!);
        Assert.Empty(failed.ErrorRetries!);

        var retried = await Eval.RunAsync(task with { Solver = FailingAfterGenerate() }, Options(new ScriptedModelApi(ScriptedTurn.Text("first"), ScriptedTurn.Text("second"))) with { RetryOnError = 1 });
        Assert.Equal(EvalStatus.Success, retried.Status);
        var sample = Assert.Single(retried.Samples!);
        var retry = Assert.Single(sample.ErrorRetries!);
        Assert.Contains("Eval failed after generate!", retry.Message);
        Assert.NotEqual("", retry.Traceback);
        var events = retry.Events!;
        Assert.True(events.Count > 2);
        var model = Assert.IsType<ModelEvent>(events[0]);
        Assert.Equal("first", model.Output.Completion);
        var info = Assert.Single(events.OfType<InfoEvent>());
        Assert.Equal("about to check for failure", info.Data!.GetValue<string>());
        // the logged sample holds only the successful attempt
        Assert.Equal("second", sample.Output.Completion);
        Assert.Single(sample.Events.OfType<ModelEvent>());

        var read = EvalLogWriter.Read(retried.Location!);
        var readRetry = Assert.Single(Assert.Single(read.Samples!).ErrorRetries!);
        Assert.Equal(retry.Message, readRetry.Message);
        Assert.Equal(events.Count, readRetry.Events!.Count);
        Assert.IsType<ModelEvent>(readRetry.Events[0]);
    }

    [Fact]
    public async Task a_cancelled_attempt_is_not_retried()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        Solver solver = async (state, _, ct) =>
        {
            attempts++;
            await cts.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return state;
        };
        var task = new EvalTask { Name = "retry", Dataset = new MemoryDataset([new Sample("x") { Target = "x" }]), Solver = solver };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Eval.RunAsync(task, Options() with { RetryOnError = 3 }, cts.Token));

        Assert.Equal(1, attempts);
        var log = EvalLogWriter.Read(Assert.Single(Directory.GetFiles(_logDir, "*.json")));
        Assert.Equal(EvalStatus.Cancelled, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.NotNull(sample.Error);
        Assert.Empty(sample.ErrorRetries!);
    }

    [Fact]
    public async Task a_negative_retry_count_is_rejected()
    {
        var task = new EvalTask { Name = "retry", Dataset = new MemoryDataset([new Sample("x")]) };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Eval.RunAsync(task, Options() with { RetryOnError = -1 }));
    }

    // ---------------------------------------------------------------- early stopping

    private sealed class RecordingEarlyStopping(Func<RecordingEarlyStopping, object, int, EarlyStop?> schedule) : IEarlyStopping
    {
        public List<(string Task, int Samples, int Epochs)> Started { get; } = [];

        public List<(object Id, int Epoch, string[] Scores)> Completed { get; } = [];

        public bool TaskCompleted { get; private set; }

        public Func<object, int, Task>? OnComplete { get; init; }

        public Task<string> StartTaskAsync(EvalSpec task, IReadOnlyList<Sample> samples, int epochs, CancellationToken cancellationToken)
        {
            Started.Add((task.Task, samples.Count, epochs));
            return Task.FromResult("recorder");
        }

        public Task<EarlyStop?> ScheduleSampleAsync(object id, int epoch, CancellationToken cancellationToken) => Task.FromResult(schedule(this, id, epoch));

        public async Task CompleteSampleAsync(object id, int epoch, IReadOnlyDictionary<string, SampleScore> scores, CancellationToken cancellationToken)
        {
            Completed.Add((id, epoch, scores.Keys.ToArray()));
            if (OnComplete is { } hook)
            {
                await hook(id, epoch);
            }
        }

        public Task<IReadOnlyDictionary<string, object?>> CompleteTaskAsync(CancellationToken cancellationToken)
        {
            TaskCompleted = true;
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["completed"] = Completed.Count });
        }
    }

    private static EvalTask StoppingTask(IEarlyStopping stopping, Solver? solver = null) => new()
    {
        Name = "stop",
        Dataset = new MemoryDataset(Enumerable.Range(1, 3).Select(i => new Sample($"q{i}") { Target = "a" })),
        Solver = solver ?? Solvers.Generate(),
        Scorers = [Scorers.Includes()],
        EarlyStopping = stopping,
        FailOnError = false,
    };

    [Fact]
    public async Task early_stopping_halts_a_scheduled_sample_without_logging_it()
    {
        var stopping = new RecordingEarlyStopping((_, id, epoch) => (int)id == 2 ? new EarlyStop(id, epoch) { Reason = "enough", Metadata = new Dictionary<string, object?> { ["why"] = "test" } } : null);
        var reporter = new RecordingReporter();
        var api = new ScriptedModelApi(ScriptedTurn.Text("a"), ScriptedTurn.Text("b"));

        var log = await Eval.RunAsync(StoppingTask(stopping), Options(api) with { Reporter = reporter });

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal([1, 3], log.Samples!.Select(s => (int)s.Id));
        Assert.Equal(3, log.Results!.TotalSamples);
        Assert.Equal(2, log.Results.CompletedSamples);
        Assert.Equal(2, api.Requests.Count);
        Assert.Equal([1, 3], reporter.Started.Select(id => (int)id));
        Assert.Equal([("stop", 3, 1)], stopping.Started);
        Assert.Equal([1, 3], stopping.Completed.Select(c => (int)c.Id));
        Assert.All(stopping.Completed, c => Assert.Equal(["includes"], c.Scores));
        Assert.True(stopping.TaskCompleted);

        var summary = log.Results.EarlyStopping!;
        Assert.Equal("recorder", summary.Manager);
        var stop = Assert.Single(summary.EarlyStops);
        Assert.Equal(2, stop.Id);
        Assert.Equal(1, stop.Epoch);
        Assert.Equal("enough", stop.Reason);
        Assert.Equal("test", stop.Metadata!["why"]);
        Assert.Equal(2, summary.Metadata["completed"]);
        Assert.Equal(0.5, Assert.Single(log.Results.Scores).Metrics["accuracy"].Value);

        var read = EvalLogWriter.Read(log.Location!);
        var readSummary = read.Results!.EarlyStopping!;
        Assert.Equal("recorder", readSummary.Manager);
        Assert.Equal(2, Assert.Single(readSummary.EarlyStops).Id);
        Assert.Equal("enough", readSummary.EarlyStops[0].Reason);
        Assert.Equal(2, readSummary.Metadata["completed"]);
    }

    [Fact]
    public async Task early_stopping_halts_the_remaining_samples_once_it_has_seen_enough()
    {
        var stopping = new RecordingEarlyStopping((self, id, epoch) => self.Completed.Count >= 1 ? new EarlyStop(id, epoch) { Reason = "done" } : null);

        var log = await Eval.RunAsync(StoppingTask(stopping), Options());

        Assert.Equal(1, Assert.Single(log.Samples!).Id);
        Assert.Equal(1, log.Results!.CompletedSamples);
        Assert.Equal([2, 3], log.Results.EarlyStopping!.EarlyStops.Select(s => (int)s.Id));
        Assert.All(log.Results.Scores, s => Assert.Equal(1, s.ScoredSamples));
    }

    [Fact]
    public async Task an_errored_sample_without_scores_is_not_reported_to_early_stopping()
    {
        var stopping = new RecordingEarlyStopping((_, _, _) => null);

        var log = await Eval.RunAsync(StoppingTask(stopping, Failing(_ => true)), Options());

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal(3, log.Samples!.Count);
        Assert.Empty(stopping.Completed);
        Assert.Empty(log.Results!.EarlyStopping!.EarlyStops);
    }

    [Fact]
    public async Task a_raising_early_stopping_hook_fails_the_eval()
    {
        var stopping = new RecordingEarlyStopping((_, _, _) => null) { OnComplete = (_, _) => throw new InvalidOperationException("hook failure") };

        var log = await Eval.RunAsync(StoppingTask(stopping), Options());

        Assert.Equal(EvalStatus.Error, log.Status);
        Assert.Contains("hook failure", log.Error!.Message);
        Assert.False(stopping.TaskCompleted);
    }

    // ---------------------------------------------------------------- sample_id filter

    private static EvalTask NamedTask(params object[] ids) => new()
    {
        Name = "select",
        Dataset = new MemoryDataset(ids.Select(id => new Sample($"q{id}") { Id = id, Target = "a" }), name: "named"),
    };

    [Fact]
    public async Task sample_ids_are_glob_patterns_over_normalised_ids()
    {
        var task = NamedTask("alpha-1", "alpha-2", "beta-1");

        var alphas = await Eval.RunAsync(task, Options() with { SampleIds = ["alpha-*"] });
        var beta = await Eval.RunAsync(task, Options() with { SampleIds = ["beta-?"] });
        var ints = await Eval.RunAsync(NamedTask(1, 2, 3), Options() with { SampleIds = ["003", 2] });

        Assert.Equal(["alpha-1", "alpha-2"], alphas.Samples!.Select(s => (string)s.Id));
        Assert.Equal(["alpha-*"], alphas.Eval.Config.SampleId!.Select(id => (string)id));
        Assert.Equal("beta-1", Assert.Single(beta.Samples!).Id);
        Assert.Equal([2, 3], ints.Samples!.Select(s => (int)s.Id));
    }

    [Fact]
    public async Task task_scoped_ids_apply_to_the_named_task_only()
    {
        var log = await Eval.RunAsync(NamedTask("alpha-1", "beta-1"), Options() with { SampleIds = ["Select:beta-1", "other:alpha-1"] });

        Assert.Equal("beta-1", Assert.Single(log.Samples!).Id);
    }

    [Fact]
    public async Task an_unmatched_id_warns_and_no_match_at_all_is_an_error()
    {
        var reporter = new RecordingReporter();

        var log = await Eval.RunAsync(NamedTask("alpha-1", "beta-1"), Options() with { SampleIds = ["alpha-1", "gamma"], Reporter = reporter });
        var error = await Assert.ThrowsAsync<PrerequisiteError>(() => Eval.RunAsync(NamedTask("alpha-1", "beta-1"), Options() with { SampleIds = ["gamma"] }));

        Assert.Equal("alpha-1", Assert.Single(log.Samples!).Id);
        Assert.Contains("sample id 'gamma' not found in dataset 'named'.", reporter.Messages);
        Assert.StartsWith("No matches in dataset 'named' for sample_id filter 'gamma'\n(named ids: ['alpha-1', 'beta-1'])", error.Message);
    }

    [Theory]
    [InlineData("7", "00000000000000000007")]
    [InlineData("007", "00000000000000000007")]
    [InlineData(7, "00000000000000000007")]
    [InlineData("abc", "abc")]
    [InlineData("a1", "a1")]
    [InlineData(-3, "-0000000000000000003")]
    [InlineData("0", "00000000000000000000")]
    [InlineData("12345678901234567890123", "12345678901234567890123")]
    public void sample_ids_normalise_like_python(object id, string expected) => Assert.Equal(expected, SampleIdFilter.Normalise(id));

    [Theory]
    [InlineData("alpha-*", "alpha-1", true)]
    [InlineData("alpha-*", "beta-1", false)]
    [InlineData("q?", "q1", true)]
    [InlineData("q?", "q12", false)]
    [InlineData("[ab]x", "ax", true)]
    [InlineData("[!ab]x", "ax", false)]
    [InlineData("[!ab]x", "cx", true)]
    [InlineData("a.b", "axb", false)]
    [InlineData("a.b", "a.b", true)]
    [InlineData("*", "", true)]
    [InlineData("[", "[", true)]
    [InlineData("a[]]b", "a]b", true)]
    [InlineData("s*e", "s\ne", true)]
    [InlineData("Alpha", "alpha", false)]
    public void glob_patterns_match_like_python_fnmatch(string pattern, string text, bool expected) =>
        Assert.Equal(expected, SampleIdFilter.GlobToRegex(pattern).IsMatch(text));

    // ---------------------------------------------------------------- scoped limits

    [Fact]
    public void a_token_limit_raises_once_exceeded_with_itself_as_the_source()
    {
        var limit = new TokenLimit(10);
        using (limit.Enter())
        {
            TokenLimit.RecordModelUsage(new ModelUsage(5, 5, 10));
            TokenLimit.CheckTokenLimit();
            TokenLimit.RecordModelUsage(new ModelUsage(1, 0, 1));
            var ex = Assert.Throws<LimitExceededException>(TokenLimit.CheckTokenLimit);

            Assert.Equal("token", ex.Type);
            Assert.Equal(11, ex.Value);
            Assert.Equal(10, ex.Limit);
            Assert.Same(limit, ex.SourceLimit);
            Assert.Equal("Token limit exceeded. value: 11; limit: 10", ex.Message);
            Assert.Equal(11, limit.Usage);
            Assert.Equal(-1, limit.Remaining);
        }

        // out of scope: nothing is checked any more
        TokenLimit.RecordModelUsage(new ModelUsage(0, 0, 1000));
        TokenLimit.CheckTokenLimit();
        Assert.Null(TokenLimit.Current);
    }

    [Fact]
    public void nested_token_limits_check_from_the_root_so_the_outer_one_wins()
    {
        using (new TokenLimit(10).Enter())
        {
            using (new TokenLimit(100).Enter())
            {
                TokenLimit.RecordModelUsage(new ModelUsage(0, 0, 11));
                Assert.Equal(10, Assert.Throws<LimitExceededException>(TokenLimit.CheckTokenLimit).Limit);
            }
        }

        using (new TokenLimit(100).Enter())
        {
            using (new TokenLimit(10).Enter())
            {
                TokenLimit.RecordModelUsage(new ModelUsage(0, 0, 11));
                Assert.Equal(10, Assert.Throws<LimitExceededException>(TokenLimit.CheckTokenLimit).Limit);
            }

            // usage recorded in the child counted against the parent too
            Assert.Equal(11, TokenLimit.Current!.Usage);
            TokenLimit.RecordModelUsage(new ModelUsage(0, 0, 90));
            Assert.Equal(100, Assert.Throws<LimitExceededException>(TokenLimit.CheckTokenLimit).Limit);
        }

        var both = Assert.Throws<LimitExceededException>(() =>
        {
            using (new TokenLimit(1).Enter())
            using (new TokenLimit(2).Enter())
            {
                TokenLimit.RecordModelUsage(new ModelUsage(0, 0, 10));
                TokenLimit.CheckTokenLimit();
            }
        });
        Assert.Equal(1, both.Limit);
    }

    [Fact]
    public void limits_validate_reuse_arguments_and_suspension()
    {
        var limit = new TokenLimit(10);
        using (limit.Enter())
        {
            Assert.Throws<InvalidOperationException>(() => limit.Enter());
        }

        Assert.Throws<InvalidOperationException>(() => limit.Enter());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenLimit(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TurnLimit(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MessageLimit(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeLimit(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkingLimit(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentException>(() => new TokenLimit(10, "(input * 0.1) + output"));

        using (new TokenLimit(10).Enter())
        {
            using (TokenLimit.SuspendTokenLimit())
            {
                TokenLimit.RecordModelUsage(new ModelUsage(0, 0, 100));
                TokenLimit.CheckTokenLimit();
                using (new TokenLimit(1).Enter())
                {
                    TokenLimit.RecordModelUsage(new ModelUsage(0, 0, 100));
                    TokenLimit.CheckTokenLimit();
                }
            }

            Assert.Equal(0, TokenLimit.Current!.Usage);
            TokenLimit.RecordModelUsage(new ModelUsage(0, 0, 11));
            Assert.Throws<LimitExceededException>(TokenLimit.CheckTokenLimit);
        }

        var limitToPop = new TokenLimit(1);
        var stray = new TokenLimit(2);
        using (limitToPop.Enter())
        {
            stray.Enter();
            Assert.Throws<InvalidOperationException>(limitToPop.Dispose);
            stray.Dispose();
        }
    }

    [Fact]
    public void an_output_token_limit_meters_output_tokens()
    {
        var limit = new TokenLimit(5, "output");
        using (limit.Enter())
        {
            TokenLimit.RecordModelUsage(new ModelUsage(100, 3, 103));
            TokenLimit.CheckTokenLimit();
            TokenLimit.RecordModelUsage(new ModelUsage(0, 3, 3));
            var ex = Assert.Throws<LimitExceededException>(TokenLimit.CheckTokenLimit);
            Assert.Equal("Output token limit exceeded. value: 6; limit: 5", ex.Message);
            Assert.Equal(6, limit.Usage);
            Assert.Equal(106, limit.RecordedUsage.TotalTokens);
        }
    }

    [Fact]
    public void a_turn_limit_allows_exactly_n_generations()
    {
        var limit = new TurnLimit(10);
        using (limit.Enter())
        {
            for (var i = 0; i < 10; i++)
            {
                TurnLimit.RecordTurn();
            }

            Assert.Equal(10, TurnLimit.TurnCount());
            var ex = Assert.Throws<LimitExceededException>(TurnLimit.RecordTurn);
            Assert.Equal("turn", ex.Type);
            Assert.Equal(11, ex.Value);
            Assert.Equal(10, ex.Limit);
            Assert.Same(limit, ex.SourceLimit);
            Assert.Equal("Turn limit exceeded. value: 11; limit: 10", ex.Message);
            Assert.Equal(12, Assert.Throws<LimitExceededException>(TurnLimit.RecordTurn).Value);
            Assert.Equal(12, TurnLimit.TurnCount());
        }

        TurnLimit.RecordTurn();
        Assert.Null(TurnLimit.TurnCount());
    }

    [Fact]
    public void nested_turn_limits_trip_in_python_order()
    {
        using (new TurnLimit(10).Enter())
        {
            Turns(6);
            using (new TurnLimit(11).Enter())
            {
                Assert.Equal(10, Assert.Throws<LimitExceededException>(() => Turns(5)).Limit);
            }
        }

        using (new TurnLimit(10).Enter())
        {
            Turns(1);
            using (new TurnLimit(5).Enter())
            {
                Assert.Equal(5, Assert.Throws<LimitExceededException>(() => Turns(6)).Limit);
            }
        }

        using (new TurnLimit(10).Enter())
        {
            Turns(5);
            using (new TurnLimit(5).Enter())
            {
                Turns(1);
            }

            var ex = Assert.Throws<LimitExceededException>(() => Turns(5));
            Assert.Equal(10, ex.Limit);
            Assert.Equal(11, ex.Value);
        }

        var both = Assert.Throws<LimitExceededException>(() =>
        {
            using (new TurnLimit(1).Enter())
            using (new TurnLimit(2).Enter())
            {
                Turns(10);
            }
        });
        Assert.Equal(1, both.Limit);

        using (new TurnLimit(1).Enter())
        {
            using (TurnLimit.SuspendTurnLimit())
            {
                Turns(10);
            }

            Assert.Equal(0, TurnLimit.TurnCount());
        }

        static void Turns(int count)
        {
            for (var i = 0; i < count; i++)
            {
                TurnLimit.RecordTurn();
            }
        }
    }

    /// <summary>A clock the tests advance by hand (Python patches <c>anyio.current_time</c>).</summary>
    private sealed class FakeTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(double seconds) => _ticks += (long)(seconds * TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void a_working_limit_subtracts_waiting_time()
    {
        var time = new FakeTime();
        var limit = new WorkingLimit(TimeSpan.FromSeconds(1), time);
        using (limit.Enter())
        {
            Assert.Null(WorkingLimit.WorkingLimitExceeded());
            time.Advance(5);
            var ex = WorkingLimit.WorkingLimitExceeded()!;
            Assert.Equal("working", ex.Type);
            Assert.Equal(5, ex.Value);
            Assert.Equal(1, ex.Limit);
            Assert.Same(limit, ex.SourceLimit);
            Assert.Equal("Working time limit exceeded. limit: 1 seconds", ex.Message);

            WorkingLimit.RecordActiveWaitingTime(TimeSpan.FromSeconds(4.5));
            Assert.Equal(0.5, limit.Usage, 6);
            Assert.Null(WorkingLimit.WorkingLimitExceeded());
            WorkingLimit.CheckWorkingLimit();
            time.Advance(1);
            Assert.Throws<LimitExceededException>(WorkingLimit.CheckWorkingLimit);
        }

        Assert.Equal(6 - 4.5, limit.Usage, 6);
        WorkingLimit.RecordActiveWaitingTime(TimeSpan.FromSeconds(10));
        WorkingLimit.CheckWorkingLimit();
    }

    [Fact]
    public void nested_working_limits_trip_in_python_order()
    {
        var time = new FakeTime();
        using (new WorkingLimit(TimeSpan.FromSeconds(1), time).Enter())
        {
            using (new WorkingLimit(TimeSpan.FromSeconds(10), time).Enter())
            {
                time.Advance(2);
                Assert.Equal(1, WorkingLimit.WorkingLimitExceeded()!.Limit);
            }
        }

        using (new WorkingLimit(TimeSpan.FromSeconds(10), time).Enter())
        {
            using (new WorkingLimit(TimeSpan.FromSeconds(1), time).Enter())
            {
                time.Advance(2);
                Assert.Equal(1, WorkingLimit.WorkingLimitExceeded()!.Limit);
            }

            using (new WorkingLimit(TimeSpan.FromSeconds(100), time).Enter())
            {
                // waiting recorded on the child reaches the parent
                WorkingLimit.RecordActiveWaitingTime(TimeSpan.FromSeconds(2));
            }

            Assert.Equal(0, WorkingLimit.Current!.Usage, 6);
            time.Advance(11);
            var ex = WorkingLimit.WorkingLimitExceeded()!;
            Assert.Equal(10, ex.Limit);
            Assert.Equal(11, ex.Value, 6);
        }
    }

    [Fact]
    public void a_message_limit_checks_only_the_innermost_scope()
    {
        using (new MessageLimit(2).Enter())
        {
            using (new MessageLimit(10).Enter())
            {
                MessageLimit.CheckMessageLimit(5, raiseForEqual: false);
                var reached = Assert.Throws<LimitExceededException>(() => MessageLimit.CheckMessageLimit(10, raiseForEqual: true));
                Assert.Equal("Message limit reached. count: 10; limit: 10", reached.Message);
                Assert.Throws<NotSupportedException>(() => MessageLimit.Current!.Usage);
            }

            var exceeded = Assert.Throws<LimitExceededException>(() => MessageLimit.CheckMessageLimit(5, raiseForEqual: false));
            Assert.Equal("Message limit exceeded. count: 5; limit: 2", exceeded.Message);
            Assert.Equal(2, exceeded.Limit);
            Assert.Equal(5, exceeded.Value);
        }

        MessageLimit.CheckMessageLimit(int.MaxValue, raiseForEqual: true);
    }

    [Fact]
    public async Task apply_async_records_only_the_errors_of_its_own_limits()
    {
        LimitScope? child = null;
        var parent = await Limit.ApplyAsync([new TokenLimit(10)], async _ =>
        {
            child = Limit.Apply(new TokenLimit(100));
            await child.RunAsync(_ =>
            {
                TokenLimit.RecordModelUsage(new ModelUsage(0, 0, 11));
                TokenLimit.CheckTokenLimit();
                return Task.CompletedTask;
            }, catchErrors: true);
        }, catchErrors: true);

        Assert.NotNull(parent.LimitError);
        Assert.Same(parent.Limits[0], parent.LimitError.SourceLimit);
        Assert.Null(child!.LimitError);
        await Assert.ThrowsAsync<InvalidOperationException>(() => child.RunAsync(_ => Task.CompletedTask));

        child = null;
        parent = await Limit.ApplyAsync([new TokenLimit(100)], async _ =>
        {
            child = await Limit.ApplyAsync([new TokenLimit(10)], _ =>
            {
                TokenLimit.RecordModelUsage(new ModelUsage(0, 0, 11));
                TokenLimit.CheckTokenLimit();
                return Task.CompletedTask;
            }, catchErrors: true);
        }, catchErrors: true);

        Assert.Null(parent.LimitError);
        Assert.NotNull(child!.LimitError);

        // not catching still propagates; an error without a source belongs to no scope
        await Assert.ThrowsAsync<LimitExceededException>(() => Limit.ApplyAsync([new TokenLimit(10)], _ =>
        {
            TokenLimit.RecordModelUsage(new ModelUsage(0, 0, 11));
            TokenLimit.CheckTokenLimit();
            return Task.CompletedTask;
        }));
        await Assert.ThrowsAsync<LimitExceededException>(() => Limit.ApplyAsync([new TokenLimit(10)], _ => throw new LimitExceededException("token", 11, 10), catchErrors: true));

        var closed = await Limit.ApplyAsync([new TokenLimit(10), new MessageLimit(10)], _ => Task.CompletedTask);
        TokenLimit.RecordModelUsage(new ModelUsage(0, 0, 11));
        TokenLimit.CheckTokenLimit();
        MessageLimit.CheckMessageLimit(11, raiseForEqual: false);
        Assert.Null(closed.LimitError);
        Assert.Null(TokenLimit.Current);
        Assert.Null(MessageLimit.Current);
    }

    [Fact]
    public async Task apply_async_turns_an_elapsed_time_limit_into_a_limit_error()
    {
        var scope = await Limit.ApplyAsync([new TimeLimit(TimeSpan.FromMilliseconds(50))], ct => Task.Delay(TimeSpan.FromSeconds(10), ct), catchErrors: true);

        var error = scope.LimitError!;
        Assert.Equal("time", error.Type);
        Assert.Equal(0.05, error.Limit);
        Assert.Equal("Time limit exceeded. limit: 0.05 seconds", error.Message);
        Assert.InRange(error.Value, 0.04, 5);
        Assert.Same(scope.Limits[0], error.SourceLimit);
        Assert.Null(TimeLimit.Current);

        // the caller's own cancellation is not a limit
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Limit.ApplyAsync([new TimeLimit(TimeSpan.FromSeconds(10))], ct => Task.Delay(TimeSpan.FromSeconds(10), ct), catchErrors: true, cts.Token));
    }

    [Fact]
    public void every_trip_emits_a_sample_limit_event()
    {
        using var scope = new SampleContextScope();
        var time = new FakeTime();

        using (new TokenLimit(10).Enter())
        {
            TokenLimit.RecordModelUsage(new ModelUsage(0, 0, 11));
            Assert.Throws<LimitExceededException>(TokenLimit.CheckTokenLimit);
        }

        using (new MessageLimit(1).Enter())
        {
            Assert.Throws<LimitExceededException>(() => MessageLimit.CheckMessageLimit(1, raiseForEqual: true));
        }

        using (new TurnLimit(0).Enter())
        {
            Assert.Throws<LimitExceededException>(TurnLimit.RecordTurn);
        }

        using (new WorkingLimit(TimeSpan.FromSeconds(1), time).Enter())
        {
            time.Advance(2);
            Assert.Throws<LimitExceededException>(WorkingLimit.CheckWorkingLimit);
        }

        Assert.Throws<LimitExceededException>(() => new Limits { MessageLimit = 1 }.CheckMessageLimit(5));
        Assert.Throws<LimitExceededException>(() => new Limits { TokenLimit = 1 }.AddUsage(new ModelUsage(0, 0, 5)));
        Assert.Throws<LimitExceededException>(new Limits { TimeLimit = TimeSpan.Zero, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1) }.CheckTimeLimit);

        var events = scope.Transcript.Events.OfType<SampleLimitEvent>().ToList();
        Assert.Equal(["token", "message", "turn", "working", "message", "token", "time"], events.Select(e => e.Type));
        Assert.Equal([10, 1, 0, 1, 1, 1, 0], events.Select(e => e.Limit));
        Assert.Equal("Token limit exceeded. value: 11; limit: 10", events[0].Message);
        Assert.Equal("Working time limit exceeded. limit: 1 seconds", events[3].Message);
    }

    [Fact]
    public void sample_limits_require_a_running_sample() =>
        Assert.Contains("Is there a running sample?", Assert.Throws<InvalidOperationException>(() => SampleLimits.Current()).Message);

    [Fact]
    public async Task sample_limits_expose_the_root_limits_during_and_after_the_solvers()
    {
        Solver solver = async (state, generate, ct) =>
        {
            state = await generate(state, cancellationToken: ct);
            var limits = SampleLimits.Current();
            Assert.Equal(10, limits.Message.LimitValue);
            Assert.Equal(100, limits.Token.LimitValue);
            Assert.Equal(60, limits.Token.Remaining);
            Assert.Equal(40, limits.Token.Usage);
            Assert.Equal(1000, limits.Time.LimitValue);
            Assert.True(limits.Time.Usage > 0);
            Assert.Equal(10000, limits.Working.LimitValue);
            Assert.True(limits.Working.Usage > 0);
            Assert.Null(limits.Turn.LimitValue);
            Assert.Equal(1, limits.Turn.Usage);
            Assert.Null(limits.Cost);
            Assert.Equal(40, TokenLimit.TokenLimitUsage());
            Assert.Equal(1, TurnLimit.TurnCount());

            // a scoped limit does not hide the sample-level ones
            using (new TokenLimit(1).Enter())
            {
                Assert.Equal(100, SampleLimits.Current().Token.LimitValue);
            }

            return state;
        };
        var probe = Scorers.Custom(
            "probe",
            (state, _, _) =>
            {
                // the scopes are closed: the snapshot answers, with the message usage supplied
                var limits = SampleLimits.Current();
                Assert.Equal(40, limits.Token.Usage);
                Assert.Equal(60, limits.Token.Remaining);
                Assert.Equal(state.Messages.Count, limits.Message.Usage);
                Assert.Equal(1, limits.Turn.Usage);
                Assert.Equal(40, TokenLimit.TokenLimitUsage());
                Assert.Null(TokenLimit.Current);
                return Task.FromResult(new Score(ScoreConstants.Correct));
            },
            Metrics.Accuracy());
        var task = new EvalTask
        {
            Name = "limits",
            Dataset = new MemoryDataset([new Sample("Say Hello") { Target = "x" }]),
            Solver = solver,
            Scorers = [probe],
            MessageLimit = 10,
            TokenLimit = 100,
            TimeLimit = TimeSpan.FromSeconds(1000),
            WorkingLimit = TimeSpan.FromSeconds(10000),
        };

        var log = await Eval.RunAsync(task, Options(new ScriptedModelApi(ScriptedTurn.Text("hi", new ModelUsage(30, 10, 40)))));

        Assert.True(log.Status == EvalStatus.Success, log.Error?.Message);
        Assert.Equal("C", Assert.Single(log.Samples!).Scores!["probe"].Text);
        Assert.Equal(1000, log.Eval.Config.TimeLimit);
        Assert.Equal(10000, log.Eval.Config.WorkingLimit);
    }

    // ---------------------------------------------------------------- limits in the runner

    private static Solver Looping(int generations) => async (state, generate, ct) =>
    {
        for (var i = 0; i < generations; i++)
        {
            state = await generate(state, cancellationToken: ct);
            state.Messages.Add(new ChatMessageUser("again"));
        }

        return state;
    };

    [Fact]
    public async Task a_turn_limit_ends_the_solver_and_the_sample_is_still_scored()
    {
        var api = new ScriptedModelApi(Enumerable.Range(0, 10).Select(i => ScriptedTurn.Text($"turn {i}")));
        var task = new EvalTask
        {
            Name = "turns",
            Dataset = new MemoryDataset([new Sample("loop") { Target = "never" }]),
            Solver = Looping(10),
            Scorers = [Scorers.Includes()],
            TurnLimit = 2,
        };

        var log = await Eval.RunAsync(task, Options(api));

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);
        Assert.Equal("turn", sample.Limit!.Type);
        Assert.Equal(2, sample.Limit.Limit);
        Assert.Equal("Turn limit exceeded. value: 3; limit: 2", sample.Limit.Reason);
        Assert.Equal(3, api.Requests.Count);
        Assert.Equal("I", sample.Scores!["includes"].Text);
        Assert.Equal(2, log.Eval.Config.TurnLimit);
        var limitEvent = Assert.Single(sample.Events.OfType<SampleLimitEvent>());
        Assert.Equal("turn", limitEvent.Type);
        Assert.Equal(2, limitEvent.Limit);
        Assert.Equal(sample.Limit.Reason, limitEvent.Message);

        var read = EvalLogWriter.Read(log.Location!);
        var readEvent = Assert.Single(read.Samples![0].Events.OfType<SampleLimitEvent>());
        Assert.Equal(limitEvent, readEvent with { Timestamp = limitEvent.Timestamp });
        Assert.Contains("\"event\": \"sample_limit\"", File.ReadAllText(log.Location!));
    }

    [Fact]
    public async Task a_working_limit_ends_the_solver_and_the_sample_is_still_scored()
    {
        Solver solver = async (state, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return state;
        };
        var task = new EvalTask
        {
            Name = "working",
            Dataset = new MemoryDataset([new Sample("slow") { Target = "x" }]),
            Solver = solver,
            Scorers = [Scorers.Includes()],
            WorkingLimit = TimeSpan.FromMilliseconds(100),
        };

        var log = await Eval.RunAsync(task, Options());

        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);
        Assert.Equal("working", sample.Limit!.Type);
        Assert.Equal(0.1, sample.Limit.Limit, 3);
        Assert.Equal("Working time limit exceeded. limit: 0.1 seconds", sample.Limit.Reason);
        Assert.Equal("I", sample.Scores!["includes"].Text);
        Assert.InRange(sample.TotalTime!.Value, 0.1, 10);
        var limitEvent = Assert.Single(sample.Events.OfType<SampleLimitEvent>());
        Assert.Equal("working", limitEvent.Type);
        Assert.Equal(0, log.Eval.Config.WorkingLimit);
    }

    [Fact]
    public async Task a_time_limit_emits_the_sample_limit_event()
    {
        Solver solver = async (state, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return state;
        };
        var task = new EvalTask
        {
            Name = "time",
            Dataset = new MemoryDataset([new Sample("slow") { Target = "x" }]),
            Solver = solver,
            TimeLimit = TimeSpan.FromMilliseconds(200),
        };

        var log = await Eval.RunAsync(task, Options());

        var sample = Assert.Single(log.Samples!);
        Assert.Equal("time", sample.Limit!.Type);
        Assert.Equal("Time limit exceeded. limit: 0.2 seconds", sample.Limit.Reason);
        var limitEvent = Assert.Single(sample.Events.OfType<SampleLimitEvent>());
        Assert.Equal("time", limitEvent.Type);
        Assert.Equal(0.2, limitEvent.Limit);
    }

    [Fact]
    public async Task the_states_message_limit_writes_through_to_the_sample_limit()
    {
        Solver solver = async (state, generate, ct) =>
        {
            Assert.Null(state.MessageLimit);
            state.MessageLimit = 2;
            Assert.Equal(2, MessageLimit.Current!.Limit);
            return await Looping(10)(state, generate, ct);
        };
        var task = new EvalTask
        {
            Name = "messages",
            Dataset = new MemoryDataset([new Sample("loop") { Target = "x" }]),
            Solver = solver,
            Scorers = [Scorers.Includes()],
        };

        var log = await Eval.RunAsync(task, Options());

        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);
        Assert.Equal("message", sample.Limit!.Type);
        Assert.Equal(2, sample.Limit.Limit);
        Assert.Equal("Message limit exceeded. count: 3; limit: 2", sample.Limit.Reason);
        Assert.Equal(3, sample.Messages.Count);
        Assert.Null(log.Eval.Config.MessageLimit);
        Assert.Equal("message", Assert.Single(sample.Events.OfType<SampleLimitEvent>()).Type);
    }

    [Fact]
    public async Task a_limit_scoped_inside_the_solver_ends_it_like_a_sample_limit()
    {
        Solver solver = async (state, generate, ct) =>
        {
            using (new TokenLimit(5).Enter())
            {
                return await generate(state, cancellationToken: ct);
            }
        };
        var task = new EvalTask
        {
            Name = "scoped",
            Dataset = new MemoryDataset([new Sample("q") { Target = "x" }]),
            Solver = solver,
            Scorers = [Scorers.Includes()],
            TokenLimit = 1000,
        };

        var log = await Eval.RunAsync(task, Options(new ScriptedModelApi(ScriptedTurn.Text("x", new ModelUsage(5, 5, 10)))));

        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);
        Assert.Equal("token", sample.Limit!.Type);
        Assert.Equal(5, sample.Limit.Limit);
        Assert.Equal("Token limit exceeded. value: 10; limit: 5", sample.Limit.Reason);
        Assert.Equal("I", sample.Scores!["includes"].Text);
        Assert.Single(sample.Messages);
        Assert.Equal(10, sample.ModelUsage["scripted"].TotalTokens);
    }

    // ---------------------------------------------------------------- working time

    [Fact]
    public async Task working_time_excludes_model_retry_waits()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Throw(new InvalidOperationException("busy")), ScriptedTurn.Text("4"))
        {
            ShouldRetry = _ => RetryDecision.Transient(),
        };
        var retry = new ModelRetryOptions(MaxRetries: 2, Delay: (_, ct) => Task.Delay(TimeSpan.FromMilliseconds(250), ct));
        var task = new EvalTask { Name = "waits", Dataset = new MemoryDataset([new Sample("2+2") { Target = "4" }]), Scorers = [Scorers.Includes()] };

        var log = await Eval.RunAsync(task, Options(api, retry));

        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);
        Assert.Equal("4", sample.Output.Completion);
        Assert.InRange(sample.TotalTime!.Value, 0.25, 10);
        Assert.True(sample.WorkingTime!.Value <= sample.TotalTime.Value - 0.2, $"working {sample.WorkingTime} vs total {sample.TotalTime}");
    }

    [Fact]
    public async Task working_time_and_total_time_exclude_sandbox_setup()
    {
        var task = new EvalTask
        {
            Name = "setup",
            Dataset = new MemoryDataset([new Sample("x") { Target = "x", Setup = "sleep 0.4" }]),
            Sandbox = new SandboxSpec("local"),
            Scorers = [Scorers.Includes()],
        };

        var log = await Eval.RunAsync(task, Options());

        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);
        Assert.InRange(sample.TotalTime!.Value, 0, 0.3);
        Assert.InRange(sample.WorkingTime!.Value, 0, sample.TotalTime.Value);
        Assert.True(sample.CompletedAt - sample.StartedAt >= TimeSpan.FromMilliseconds(350));
        Assert.Contains(sample.Events, e => e is SpanBeginEvent { Type: "init" });
    }

    [Fact]
    public async Task a_sample_that_never_started_working_has_no_times()
    {
        var task = new EvalTask
        {
            Name = "setup",
            Dataset = new MemoryDataset([new Sample("x") { Target = "x", Setup = "exit 3" }]),
            Sandbox = new SandboxSpec("local"),
            FailOnError = false,
        };

        var log = await Eval.RunAsync(task, Options());

        var sample = Assert.Single(log.Samples!);
        Assert.NotNull(sample.Error);
        Assert.Null(sample.TotalTime);
        Assert.Null(sample.WorkingTime);
    }

    private sealed class RecordingReporter : IEvalReporter
    {
        public List<object> Started { get; } = [];

        public List<string> Messages { get; } = [];

        public void SampleStarted(object id, int epoch) => Started.Add(id);

        public void SampleCompleted(EvalSample sample)
        {
        }

        public void Message(string text) => Messages.Add(text);
    }
}

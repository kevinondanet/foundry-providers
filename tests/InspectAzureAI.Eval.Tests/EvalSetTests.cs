using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Concurrency;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Runner.EvalSet;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using EvalSet = InspectAzureAI.Eval.Runner.EvalSet.EvalSet;
using Model = InspectAzureAI.Eval.Model.Model;
using Scorers = InspectAzureAI.Eval.Scorers.Scorers;

/// <summary>
/// Eval sets, resume and retry (<c>_eval/evalset.py</c>, <c>eval_retry</c>, the sample-source reuse of
/// <c>_eval/task/run.py</c>) with scripted models over a temporary log directory.
/// </summary>
public sealed class EvalSetTests : IDisposable
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

    private EvalOptions Options(ScriptedModelApi api, GenerateConfig? config = null) => new() { Model = new Model(api, config), LogDir = _logDir, MaxSamples = 1 };

    private static EvalTask QuizTask(string name = "quiz", int samples = 2, IReadOnlyDictionary<string, object?>? taskArgs = null) => new()
    {
        Name = name,
        Dataset = new MemoryDataset(Enumerable.Range(1, samples).Select(i => new Sample($"q{i}") { Target = $"answer{i}" }).ToList(), name: "quiz"),
        Scorers = [Scorers.Includes()],
        TaskArgs = taskArgs,
    };

    private static Exception Boom(string what) => new InvalidOperationException($"provider exploded on {what}");

    private IReadOnlyList<string> LogFiles() => Directory.GetFiles(_logDir).Where(EvalSetLogs.IsLogFile).Select(Path.GetFileName).Select(name => name!).ToList();

    // ---- task identifier ------------------------------------------------------------------------------------

    [Fact]
    public void task_identifier_is_stable_and_changes_with_the_inputs_python_hashes()
    {
        var task = QuizTask(taskArgs: new Dictionary<string, object?> { ["n"] = 1 }) with { Version = "2", MessageLimit = 10 };
        var model = new Model(new ScriptedModelApi(), new GenerateConfig { Temperature = 0.5 });
        var args = new EvalSetArgsInTaskIdentifier { Config = model.Config };

        var identifier = TaskIdentifier.Compute(task, model, null, args);
        Assert.Equal(identifier, TaskIdentifier.Compute(task, model, null, args));
        Assert.Matches(new Regex("^quiz#[0-9a-f]{64}/scripted/[0-9a-f]{64}$"), identifier);
        var argsHash = identifier.Split('#')[1].Split('/')[0];
        Assert.Equal(TaskIdentifier.TaskArgsHash(task.TaskArgs!), argsHash);

        // runtime knobs are excluded, everything else is identity
        var runtime = model.WithConfig(model.Config with { MaxRetries = 7, MaxConnections = 3, Timeout = 60 });
        Assert.Equal(identifier, TaskIdentifier.Compute(task, runtime, null, args with { Config = runtime.Config }));
        var warmer = model.WithConfig(model.Config with { Temperature = 0.9 });
        Assert.NotEqual(identifier, TaskIdentifier.Compute(task, warmer, null, args with { Config = warmer.Config }));
        Assert.NotEqual(identifier, TaskIdentifier.Compute(task with { Version = "3" }, model, null, args));
        Assert.NotEqual(identifier, TaskIdentifier.Compute(task, model, null, args with { MessageLimit = 20 }));
        Assert.NotEqual(identifier, TaskIdentifier.Compute(task with { Config = new GenerateConfig { TopP = 0.1 } }, model, null, args));

        var otherArgs = TaskIdentifier.Compute(task with { TaskArgs = new Dictionary<string, object?> { ["n"] = 2 } }, model, null, args);
        Assert.NotEqual(argsHash, otherArgs.Split('#')[1].Split('/')[0]);
        Assert.NotEqual(identifier, TaskIdentifier.Compute(task, new Model(new ScriptedModelApi([], "other"), model.Config), null, args));
    }

    [Fact]
    public async Task task_identifier_of_the_written_log_matches_the_task_it_ran()
    {
        var task = QuizTask(taskArgs: new Dictionary<string, object?> { ["n"] = 1, ["label"] = "a" }) with
        {
            Version = "3",
            MessageLimit = 10,
            TimeLimit = TimeSpan.FromSeconds(30),
            Config = new GenerateConfig { TopP = 0.9, Temperature = 0.1 },
        };
        var api = new ScriptedModelApi(ScriptedTurn.Text("answer1"), ScriptedTurn.Text("answer2"));
        var options = Options(api, new GenerateConfig { Temperature = 0.5, MaxRetries = 3 }) with { TokenLimit = 500 };

        var log = await Eval.RunAsync(task, options);
        var read = EvalLogWriter.Read(log.Location!);

        var expected = TaskIdentifier.Compute(task, options.Model, null, EvalSetArgsInTaskIdentifier.FromOptions(options));
        Assert.Equal(expected, TaskIdentifier.Compute(log));
        Assert.Equal(expected, TaskIdentifier.Compute(read));
        Assert.Equal(0.5, read.Eval.ModelGenerateConfig.Temperature);
        Assert.Equal(0.9, read.Plan.Config.TopP);
        Assert.Equal(0.5, read.Plan.Config.Temperature);
        Assert.Equal(1, read.Eval.TaskArgs["n"] is int n ? n : Convert.ToInt32(read.Eval.TaskArgs["n"]));
        Assert.Equal(30, read.Eval.Config.TimeLimit);
    }

    [PythonFact]
    public async Task task_identifier_matches_python_for_a_written_log()
    {
        var task = QuizTask(taskArgs: new Dictionary<string, object?> { ["n"] = 1, ["label"] = "a b" }) with
        {
            Version = "3",
            MessageLimit = 10,
            Config = new GenerateConfig { TopP = 0.9 },
        };
        var api = new ScriptedModelApi(ScriptedTurn.Text("answer1"), ScriptedTurn.Text("answer2"));
        var options = Options(api, new GenerateConfig { Temperature = 0.5, MaxRetries = 3 }) with { TokenLimit = 500 };
        var log = await Eval.RunAsync(task, options);

        var python = PythonReference.Run($$"""
            from inspect_ai.log import read_eval_log
            from inspect_ai._eval.evalset import task_identifier
            print(task_identifier(read_eval_log(r"{{log.Location}}"), None))
            """);

        Assert.Equal(python, TaskIdentifier.Compute(EvalLogWriter.Read(log.Location!)));
        Assert.Equal(python, TaskIdentifier.Compute(task, options.Model, null, EvalSetArgsInTaskIdentifier.FromOptions(options)));
    }

    // ---- eval set --------------------------------------------------------------------------------------------

    [Fact]
    public async Task eval_set_retries_a_task_whose_model_fails_on_the_first_pass_and_succeeds_on_retry()
    {
        var api = new ScriptedModelApi(
            ScriptedTurn.Text("answer1", new ModelUsage(1, 1, 2)),
            ScriptedTurn.Throw(Boom("q2")),
            ScriptedTurn.Text("answer2", new ModelUsage(3, 1, 4)));
        var messages = new List<string>();
        var options = new EvalSetOptions { Eval = Options(api) with { Reporter = new RecordingReporter(messages) } };

        var result = await EvalSet.RunAsync([QuizTask()], options);

        Assert.True(result.Success);
        var log = Assert.Single(result.Logs);
        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal(3, api.Requests.Count);
        var samples = log.Samples!;
        Assert.Equal(2, samples.Count);
        Assert.Equal("answer1", samples[0].Output.Completion);
        Assert.Empty(samples[0].ErrorRetries ?? []);
        Assert.Equal("answer2", samples[1].Output.Completion);
        var retry = Assert.Single(samples[1].ErrorRetries!);
        Assert.Contains("provider exploded on q2", retry.Message);
        Assert.Equal(2, log.Results!.CompletedSamples);
        Assert.Equal(1.0, log.Results.Scores[0].Metrics["accuracy"].Value);
        // usage is cumulative across attempts: the reused sample's tokens come from the previous log's totals
        Assert.Equal(6, log.Stats.ModelUsage["scripted"].TotalTokens);
        Assert.Contains(messages, message => message.StartsWith("Retrying task 'quiz'", StringComparison.Ordinal));

        Assert.Single(LogFiles());
        var info = EvalSetInfo.Read(_logDir)!;
        Assert.Equal(log.Eval.EvalSetId, info.EvalSetId);
        Assert.Equal(log.Eval.TaskId, Assert.Single(info.Tasks).TaskId);
        Assert.Equal(log.Eval.EvalSetId, File.ReadAllText(Path.Combine(_logDir, EvalSetInfo.IdFileName)));
    }

    [Fact]
    public async Task eval_set_resume_reuses_completed_samples_and_reruns_only_the_failed_ones()
    {
        var first = new ScriptedModelApi(ScriptedTurn.Text("answer1"), ScriptedTurn.Throw(Boom("q2")));
        var failed = await EvalSet.RunAsync([QuizTask()], new EvalSetOptions { Eval = Options(first), RetryAttempts = 0 });
        Assert.False(failed.Success);
        Assert.Equal(EvalStatus.Error, Assert.Single(failed.Logs).Status);
        Assert.Single(LogFiles());

        var second = new ScriptedModelApi(ScriptedTurn.Text("answer2"));
        var resumed = await EvalSet.RunAsync([QuizTask()], new EvalSetOptions { Eval = Options(second) });

        Assert.True(resumed.Success);
        var log = Assert.Single(resumed.Logs);
        Assert.Equal(failed.Logs[0].Eval.TaskId, log.Eval.TaskId);
        Assert.Equal(failed.Logs[0].Eval.EvalSetId, log.Eval.EvalSetId);
        Assert.Single(second.Requests);
        Assert.Equal("answer1", log.Samples![0].Output.Completion);
        Assert.Equal(failed.Logs[0].Samples![0].Uuid, log.Samples[0].Uuid);
        Assert.Equal("answer2", log.Samples[1].Output.Completion);
        Assert.Single(log.Samples[1].ErrorRetries!);
        Assert.Single(LogFiles());

        // a third run has nothing to do: the complete log comes back as a header and no model is called
        var idle = new ScriptedModelApi { ThrowWhenExhausted = true };
        var done = await EvalSet.RunAsync([QuizTask()], new EvalSetOptions { Eval = Options(idle) });
        Assert.True(done.Success);
        Assert.Null(Assert.Single(done.Logs).Samples);
        Assert.Equal(log.Eval.TaskId, done.Logs[0].Eval.TaskId);
        Assert.Empty(idle.Requests);
    }

    [Fact]
    public async Task eval_set_runs_every_task_against_every_model_and_records_the_manifest()
    {
        var one = new ScriptedModelApi([ScriptedTurn.Text("answer1"), ScriptedTurn.Text("answer2")], "m1");
        var two = new ScriptedModelApi([ScriptedTurn.Text("answer1"), ScriptedTurn.Text("answer2")], "m2");
        var options = new EvalSetOptions { Eval = Options(one), Models = [new Model(one), new Model(two)] };

        var result = await EvalSet.RunAsync([QuizTask()], options);

        Assert.True(result.Success);
        Assert.Equal(["m1", "m2"], result.Logs.Select(log => log.Eval.Model));
        Assert.Equal(2, LogFiles().Count);
        var info = EvalSetInfo.Read(_logDir)!;
        Assert.Equal([0, 1], info.Tasks.Select(task => task.Sequence));
        Assert.Equal(["m1", "m2"], info.Tasks.Select(task => task.Model));
        Assert.All(info.Tasks, task => Assert.Equal("quiz", task.Name));
    }

    [Fact]
    public async Task eval_set_reruns_a_complete_log_when_the_epochs_change()
    {
        await EvalSet.RunAsync([QuizTask()], new EvalSetOptions { Eval = Options(new ScriptedModelApi(ScriptedTurn.Text("answer1"), ScriptedTurn.Text("answer2"))) });
        Assert.Single(LogFiles());

        // the complete log no longer covers the plan; it is retried as a previous task, so its epoch-1 samples are
        // reused and only the new epoch runs (Python's test_eval_set_epochs_changed)
        var api = new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.Text("answer"), 4));
        var result = await EvalSet.RunAsync([QuizTask()], new EvalSetOptions { Eval = Options(api) with { Epochs = 2 } });

        Assert.True(result.Success);
        Assert.Equal(2, api.Requests.Count);
        Assert.Equal(2, result.Logs[0].Eval.Config.Epochs);
        Assert.Equal(4, result.Logs[0].Results!.TotalSamples);
        Assert.Equal(4, result.Logs[0].Results!.CompletedSamples);
        Assert.Equal([1, 1, 2, 2], result.Logs[0].Samples!.Select(sample => sample.Epoch).Order());
        Assert.Single(LogFiles());
    }

    [Fact]
    public async Task eval_set_rejects_indistinct_tasks_and_foreign_logs_unless_the_directory_may_be_dirty()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("answer1"), ScriptedTurn.Text("answer2"));
        var twins = await Assert.ThrowsAsync<PrerequisiteError>(() => EvalSet.RunAsync([QuizTask(), QuizTask()], new EvalSetOptions { Eval = Options(api) }));
        Assert.Contains("not distinct", twins.Message);
        // distinct task args make the same name two tasks
        var distinct = EvalSet.ResolveTasks([QuizTask(taskArgs: new Dictionary<string, object?> { ["n"] = 1 }), QuizTask(taskArgs: new Dictionary<string, object?> { ["n"] = 2 })], [new Model(api)], Options(api));
        Assert.NotEqual(distinct[0].Identifier, distinct[1].Identifier);

        await Eval.RunAsync(QuizTask("other"), Options(new ScriptedModelApi(ScriptedTurn.Text("x"), ScriptedTurn.Text("y"))));
        var foreign = await Assert.ThrowsAsync<PrerequisiteError>(() => EvalSet.RunAsync([QuizTask()], new EvalSetOptions { Eval = Options(api) }));
        Assert.Contains("not associated with a task", foreign.Message);

        var tolerant = await EvalSet.RunAsync([QuizTask()], new EvalSetOptions { Eval = Options(api), LogDirAllowDirty = true });
        Assert.True(tolerant.Success);
        Assert.Equal(2, LogFiles().Count);
    }

    [Fact]
    public async Task eval_set_rejects_an_empty_task_list_and_a_negative_retry_budget()
    {
        var options = new EvalSetOptions { Eval = Options(new ScriptedModelApi()) };
        await Assert.ThrowsAsync<PrerequisiteError>(() => EvalSet.RunAsync([], options));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => EvalSet.RunAsync([QuizTask()], options with { RetryAttempts = -1 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => EvalSet.RunAsync([QuizTask()], options with { RetryConnections = 0 }));
    }

    [Fact]
    public async Task eval_set_hooks_fire_at_start_and_end_with_the_directory_pinned_id()
    {
        var hooks = new RecordingHooks();
        var api = new ScriptedModelApi(ScriptedTurn.Text("answer1"), ScriptedTurn.Text("answer2"));

        var result = await EvalSet.RunAsync([QuizTask()], new EvalSetOptions { Eval = Options(api), Hooks = hooks, EvalSetId = "set-1" });

        Assert.True(result.Success);
        Assert.Equal(["start:set-1", "end:set-1"], hooks.Events);
        Assert.Equal("set-1", result.Logs[0].Eval.EvalSetId);
        Assert.Equal("set-1", EvalSetInfo.EvalSetIdForLogDir(_logDir));
        var mismatch = Assert.Throws<PrerequisiteError>(() => EvalSetInfo.EvalSetIdForLogDir(_logDir, "set-2"));
        Assert.Contains("set-2", mismatch.Message);
    }

    [Fact]
    public async Task batch_mode_backs_off_exponentially_and_decays_connections_on_a_fake_clock()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Throw(Boom("1")), ScriptedTurn.Throw(Boom("2")), ScriptedTurn.Throw(Boom("3"))) { ConnectionLimit = 8 };
        var clock = new FakeTimeProvider();
        var options = new EvalSetOptions
        {
            Eval = Options(api) with { AdaptiveConnections = AdaptiveConnections.Disabled },
            RetryImmediate = false,
            RetryAttempts = 3,
            RetryWait = TimeSpan.FromSeconds(10),
            RetryConnections = 0.5,
            TimeProvider = clock,
        };

        var result = await EvalSet.RunAsync([QuizTask(samples: 1)], options);

        Assert.False(result.Success);
        Assert.Equal(3, api.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)], clock.Delays);
        Assert.Equal(2, result.Logs[0].Eval.ModelGenerateConfig.MaxConnections);
        Assert.Single(LogFiles());
    }

    [Fact]
    public void retry_wait_doubles_from_the_base_and_caps_at_an_hour()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), EvalSet.RetryWait(EvalSet.DefaultRetryWait, 1));
        Assert.Equal(TimeSpan.FromSeconds(60), EvalSet.RetryWait(EvalSet.DefaultRetryWait, 2));
        Assert.Equal(TimeSpan.FromSeconds(240), EvalSet.RetryWait(EvalSet.DefaultRetryWait, 4));
        Assert.Equal(EvalSet.MaxRetryWait, EvalSet.RetryWait(EvalSet.DefaultRetryWait, 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => EvalSet.RetryWait(EvalSet.DefaultRetryWait, 0));
    }

    // ---- eval retry --------------------------------------------------------------------------------------------

    [Fact]
    public async Task eval_retry_reruns_the_failed_samples_of_a_log_and_keeps_its_identity()
    {
        var task = QuizTask() with { Config = new GenerateConfig { TopP = 0.9 } };
        var first = new ScriptedModelApi(ScriptedTurn.Text("answer1", new ModelUsage(1, 1, 2)), ScriptedTurn.Throw(Boom("q2")));
        var failed = await Eval.RunAsync(task, Options(first, new GenerateConfig { Temperature = 0.5 }) with { TaskId = "task-1", EvalSetId = "set-1", MessageLimit = 12 });
        Assert.Equal(EvalStatus.Error, failed.Status);

        var second = new ScriptedModelApi(ScriptedTurn.Text("answer2", new ModelUsage(3, 1, 4)));
        var logs = await EvalRetry.RunAsync([failed.Location!], new EvalRetryOptions { Tasks = [task], ResolveModel = _ => new Model(second), MaxRetries = 2 });

        var log = Assert.Single(logs);
        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal("task-1", log.Eval.TaskId);
        Assert.Null(log.Eval.EvalSetId);
        Assert.Equal(_logDir, Path.GetDirectoryName(log.Location));
        Assert.Single(second.Requests);
        Assert.Equal("answer1", log.Samples![0].Output.Completion);
        Assert.Equal("answer2", log.Samples[1].Output.Completion);
        Assert.Single(log.Samples[1].ErrorRetries!);
        Assert.Equal(12, log.Eval.Config.MessageLimit);
        Assert.Equal(1, log.Eval.Config.MaxSamples);
        Assert.Equal(0.5, log.Eval.ModelGenerateConfig.Temperature);
        Assert.Equal(2, log.Eval.ModelGenerateConfig.MaxRetries);
        Assert.Equal(6, log.Stats.ModelUsage["scripted"].TotalTokens);
        Assert.Equal(TaskIdentifier.Compute(failed), TaskIdentifier.Compute(log));

        var missing = await Assert.ThrowsAsync<PrerequisiteError>(() => EvalRetry.RunAsync([failed], new EvalRetryOptions { Tasks = [QuizTask("other")] }));
        Assert.Contains("'quiz' not found", missing.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => EvalRetry.RunAsync([failed], new EvalRetryOptions()));
    }

    // ---- sample source and carry-forward ----------------------------------------------------------------------

    [Fact]
    public async Task an_aborted_attempt_carries_forward_the_error_history_of_samples_it_never_ran()
    {
        var task = QuizTask(samples: 3) with { FailOnError = FailOnError.Never };
        var previous = await Eval.RunAsync(task, Options(new ScriptedModelApi(ScriptedTurn.Text("answer1"), ScriptedTurn.Throw(Boom("q2")), ScriptedTurn.Throw(Boom("q3")))));
        Assert.Equal(EvalStatus.Success, previous.Status);
        Assert.NotNull(previous.Samples![1].Error);
        Assert.NotNull(previous.Samples[2].Error);

        // attempt 2: sample 1 is reused; sample 2 re-runs, fails again and aborts the run; sample 3 never gets to run
        var aborted = await Eval.RunAsync(
            task with { FailOnError = FailOnError.Always },
            Options(new ScriptedModelApi(ScriptedTurn.Throw(Boom("q2 again")))) with { SampleSource = EvalSampleSource.FromLog(previous, task.Dataset), TaskId = previous.Eval.TaskId });

        Assert.Equal(EvalStatus.Error, aborted.Status);
        Assert.Equal(3, aborted.Samples!.Count);
        Assert.Equal("answer1", aborted.Samples[0].Output.Completion);
        Assert.Equal(previous.Samples[0].Uuid, aborted.Samples[0].Uuid);
        Assert.Contains("q2 again", aborted.Samples[1].Error!.Message);
        Assert.Contains("q2", Assert.Single(aborted.Samples[1].ErrorRetries!).Message);
        Assert.Equal(previous.Samples[2].Uuid, aborted.Samples[2].Uuid);
        Assert.Contains("q3", aborted.Samples[2].Error!.Message);
        Assert.Equal(1, aborted.Results!.CompletedSamples);

        // attempt 3: the clean sample is reused again; sample 2 re-runs with two prior errors, sample 3 with the one it carried
        var recovered = await Eval.RunAsync(
            task,
            Options(new ScriptedModelApi(ScriptedTurn.Text("answer2"), ScriptedTurn.Text("answer3"))) with { SampleSource = EvalSampleSource.FromLog(aborted, task.Dataset), TaskId = previous.Eval.TaskId });

        Assert.Equal(EvalStatus.Success, recovered.Status);
        Assert.Equal(3, recovered.Results!.CompletedSamples);
        Assert.Empty(recovered.Samples![0].ErrorRetries ?? []);
        Assert.Equal(["provider exploded on q2", "provider exploded on q2 again"], recovered.Samples[1].ErrorRetries!.Select(retry => retry.Message));
        Assert.Contains("q3", Assert.Single(recovered.Samples[2].ErrorRetries!).Message);
        Assert.Equal(1.0, recovered.Results.Scores[0].Metrics["accuracy"].Value);
    }

    [Fact]
    public async Task sample_source_refuses_a_changed_dataset_and_ignores_cancellations()
    {
        var log = await Eval.RunAsync(QuizTask(), Options(new ScriptedModelApi(ScriptedTurn.Text("answer1"), ScriptedTurn.Text("answer2"))));
        var warnings = new List<string>();

        Assert.Equal(2, EvalSampleSource.FromLog(log, QuizTask().Dataset).Count);
        Assert.Equal(0, EvalSampleSource.FromLog(log, QuizTask(samples: 3).Dataset, warnings.Add).Count);
        Assert.Contains(warnings, warning => warning.Contains("dataset size changed"));
        Assert.Null(EvalSampleSource.FromLog(log, QuizTask().Dataset).Lookup(3, 1));
        Assert.IsType<PreviousSample.Reusable>(EvalSampleSource.FromLog(log, QuizTask().Dataset).Lookup(1, 1));

        var errored = log.Samples![0] with { Error = new EvalError("CancelledError()") };
        Assert.Empty(EvalSampleSource.SeedErrorRetries(errored));
        Assert.True(EvalSampleSource.IsCancellationError(new EvalError("Cancelled(")));
        Assert.True(EvalSampleSource.IsCancellationError(new EvalError("x", typeof(OperationCanceledException).FullName!)));
        Assert.False(EvalSampleSource.IsCancellationError(new EvalError("RuntimeError('boom')")));
        var genuine = log.Samples[0] with { Error = new EvalError("RuntimeError('boom')") };
        Assert.Equal("RuntimeError('boom')", Assert.Single(EvalSampleSource.SeedErrorRetries(genuine)).Message);
    }

    // ---- log directory predicates ------------------------------------------------------------------------------

    [Fact]
    public void epochs_changed_treats_the_default_reducer_and_an_explicit_mean_alike()
    {
        Assert.False(EvalSetLogs.EpochsChanged(null, null, new EvalConfig { Epochs = 3 }));
        Assert.False(EvalSetLogs.EpochsChanged(2, null, new EvalConfig { Epochs = 2 }));
        Assert.False(EvalSetLogs.EpochsChanged(2, null, new EvalConfig { Epochs = 2, EpochsReducer = ["mean"] }));
        Assert.False(EvalSetLogs.EpochsChanged(2, ["mean"], new EvalConfig { Epochs = 2 }));
        Assert.True(EvalSetLogs.EpochsChanged(2, ["max"], new EvalConfig { Epochs = 2 }));
        Assert.True(EvalSetLogs.EpochsChanged(2, null, new EvalConfig { Epochs = 2, EpochsReducer = ["max"] }));
        Assert.True(EvalSetLogs.EpochsChanged(2, null, new EvalConfig { Epochs = 3 }));
        Assert.True(EvalSetLogs.EpochsChanged(2, null, new EvalConfig()));
        Assert.False(EvalSetLogs.ShuffleChanged(null, new EvalConfig { SampleShuffle = true }, limit: null));
        Assert.True(EvalSetLogs.ShuffleChanged(null, new EvalConfig { SampleShuffle = true }, limit: 5));
    }

    [Fact]
    public void latest_completed_task_eval_logs_keeps_the_newest_per_task_and_deletes_the_older_ones()
    {
        Directory.CreateDirectory(_logDir);
        var a1 = WriteLog("2024-01-01T10-00-00_quiz_aaaaaa.json", "task-a", EvalStatus.Error, new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc));
        var a2 = WriteLog("2024-01-01T11-00-00_quiz_bbbbbb.json", "task-a", EvalStatus.Success, new DateTime(2024, 1, 1, 11, 0, 0, DateTimeKind.Utc));
        var a0 = WriteLog("2024-01-01T09-00-00_quiz_cccccc.json", "task-a", EvalStatus.Started, new DateTime(2024, 1, 1, 9, 0, 0, DateTimeKind.Utc));
        var b1 = WriteLog("2024-01-01T10-30-00_other_dddddd.json", "task-b", EvalStatus.Error, new DateTime(2024, 1, 1, 10, 30, 0, DateTimeKind.Utc));

        var logs = EvalSetLogs.ListAllEvalLogs(_logDir);
        Assert.Equal(4, logs.Count);
        var latest = EvalSetLogs.LatestCompletedTaskEvalLogs(logs);
        Assert.Equal([a2, b1], latest.Select(log => log.Path));
        Assert.True(File.Exists(a1));

        EvalSetLogs.LatestCompletedTaskEvalLogs(logs, cleanupOlder: true);
        Assert.False(File.Exists(a1));
        Assert.True(File.Exists(a0), "a started log is kept for post-mortem debugging");
        Assert.True(File.Exists(a2));
        Assert.True(File.Exists(b1));

        Assert.True(EvalSetLogs.IsLogFile("2024-01-01T10-00-00_quiz_aaaaaa.json"));
        Assert.False(EvalSetLogs.IsLogFile("2024-01-01T10-00-00_quiz_aaaaaa.txt"));
        Assert.True(EvalSetLogs.IsLogFile("2024-01-01T10:00:00+00:00_quiz.json"));
        Assert.False(EvalSetLogs.IsLogFile("eval-set.json"));
        Assert.True(EvalSetLogs.IsLogFile("2024-01-01T10-00-00_quiz.eval"));
    }

    private string WriteLog(string name, string taskId, EvalStatus status, DateTime modified)
    {
        var path = Path.Combine(_logDir, name);
        var log = new EvalLog
        {
            Status = status,
            Eval = new EvalSpec { Task = "quiz", TaskId = taskId, Model = "scripted", Dataset = new EvalDataset { Samples = 1 } },
            Results = new EvalResults { TotalSamples = 1, CompletedSamples = 1 },
        };
        EvalLogWriter.Write(log, path);
        File.SetLastWriteTimeUtc(path, modified);
        return path;
    }

    // ---- fakes -----------------------------------------------------------------------------------------------------

    private sealed class RecordingReporter(List<string> messages) : IEvalReporter
    {
        public void SampleStarted(object id, int epoch)
        {
        }

        public void SampleCompleted(EvalSample sample)
        {
        }

        public void Message(string text) => messages.Add(text);
    }

    private sealed class RecordingHooks : IEvalSetHooks
    {
        public List<string> Events { get; } = [];

        public Task OnEvalSetStartAsync(EvalSetStart data, CancellationToken cancellationToken)
        {
            Events.Add($"start:{data.EvalSetId}");
            return Task.CompletedTask;
        }

        public Task OnEvalSetEndAsync(EvalSetEnd data, CancellationToken cancellationToken)
        {
            Events.Add($"end:{data.EvalSetId}");
            return Task.CompletedTask;
        }
    }

    /// <summary>A clock whose timers record their due time and fire at once, so a backoff wait costs no wall time.</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        public List<TimeSpan> Delays { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Delays.Add(dueTime);
            return new ImmediateTimer(callback, state);
        }

        private sealed class ImmediateTimer : ITimer
        {
            public ImmediateTimer(TimerCallback callback, object? state)
            {
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

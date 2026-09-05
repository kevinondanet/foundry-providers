using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Sandbox.Local;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;
using Scorers = InspectAzureAI.Eval.Scorers.Scorers;
using Solvers = InspectAzureAI.Eval.Solvers.Solvers;

/// <summary>End-to-end behaviour of <c>Eval.RunAsync</c> (<c>_eval/task/run.py</c>) with a scripted model and the local sandbox.</summary>
public sealed class EvalRunnerTests : IDisposable
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

    private EvalOptions Options(ScriptedModelApi api) => new() { Model = new Model(api), LogDir = _logDir, MaxSamples = 1 };

    [Fact]
    public async Task run_scores_every_sample_and_writes_a_readable_log()
    {
        var dataset = new MemoryDataset(
        [
            new Sample("What is the capital of France?") { Target = "Paris" },
            new Sample("What is 2 + 2?") { Target = "4" },
        ], name: "capitals");
        var api = new ScriptedModelApi(ScriptedTurn.Text("Paris", new ModelUsage(5, 1, 6)), ScriptedTurn.Text("5", new ModelUsage(5, 1, 6)));
        var task = new EvalTask { Name = "geo quiz", Dataset = dataset, Scorers = [Scorers.Includes()], Version = "3" };

        var log = await Eval.RunAsync(task, Options(api));

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Null(log.Error);
        Assert.Equal("geo quiz", log.Eval.Task);
        Assert.Equal("3", log.Eval.TaskVersion);
        Assert.Equal("scripted", log.Eval.Model);
        Assert.Equal("capitals", log.Eval.Dataset.Name);
        Assert.Equal([1, 2], log.Eval.Dataset.SampleIds!.Select(id => (int)id));
        Assert.Equal(1, log.Eval.Config.Epochs);
        Assert.True(log.Eval.Config.FailOnError);

        var results = log.Results!;
        Assert.Equal(2, results.TotalSamples);
        Assert.Equal(2, results.CompletedSamples);
        var score = Assert.Single(results.Scores);
        Assert.Equal("includes", score.Name);
        Assert.Equal("includes", score.Scorer);
        Assert.Null(score.Reducer);
        Assert.Equal(2, score.ScoredSamples);
        Assert.Equal(0, score.UnscoredSamples);
        Assert.Equal(0.5, score.Metrics["accuracy"].Value);
        Assert.Equal(0.5, score.Metrics["stderr"].Value, 6);
        Assert.Equal(12, log.Stats.ModelUsage["scripted"].TotalTokens);
        Assert.NotNull(log.Stats.StartedAt);
        Assert.NotNull(log.Stats.CompletedAt);

        var samples = log.Samples!;
        Assert.Equal(2, samples.Count);
        Assert.Equal("C", samples[0].Scores!["includes"].Text);
        Assert.Equal("I", samples[1].Scores!["includes"].Text);
        Assert.Equal(["user", "assistant"], samples[0].Messages.Select(m => m.Role));
        Assert.Equal("Paris", samples[0].Output.Completion);
        Assert.Equal(6, samples[0].ModelUsage["scripted"].TotalTokens);
        Assert.NotNull(samples[0].TotalTime);
        Assert.NotNull(samples[0].Uuid);
        Assert.Contains(samples[0].Events, e => e is ModelEvent);
        Assert.Contains(samples[0].Events, e => e is StepEvent { Name: "solver", Type: "solver", Action: "begin" });
        Assert.Contains(samples[0].Events, e => e is StepEvent { Name: "includes", Type: "scorer", Action: "end" });
        Assert.Contains(samples[0].Events, e => e is ScoreEvent { Intermediate: false } scored && scored.Score.Text == "C");
        Assert.Contains(samples[0].Events, e => e is SpanBeginEvent { Name: "solvers" });

        Assert.NotNull(log.Location);
        Assert.StartsWith(_logDir, log.Location);
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}_geo-quiz_[0-9a-f]{6}\.json$"), Path.GetFileName(log.Location));
        var read = EvalLogWriter.Read(log.Location);
        Assert.Equal(EvalStatus.Success, read.Status);
        Assert.Equal(log.Location, read.Location);
        Assert.Equal(0.5, read.Results!.Scores[0].Metrics["accuracy"].Value);
        Assert.Equal(2, read.Samples!.Count);
        Assert.Equal("Paris", read.Samples[0].Output.Completion);
        Assert.Equal("C", read.Samples[0].Scores!["includes"].Text);
        Assert.Equal(12, read.Stats.ModelUsage["scripted"].TotalTokens);
    }

    [Fact]
    public async Task duplicate_scorer_names_get_numeric_suffixes()
    {
        var task = new EvalTask
        {
            Name = "dupes",
            Dataset = new MemoryDataset([new Sample("q") { Target = "paris" }]),
            Scorers = [Scorers.Includes(), Scorers.Includes(ignoreCase: false)],
        };

        var log = await Eval.RunAsync(task, Options(new ScriptedModelApi(ScriptedTurn.Text("Paris"))));

        var sample = Assert.Single(log.Samples!);
        Assert.Equal(["includes", "includes1"], sample.Scores!.Keys);
        Assert.Equal("C", sample.Scores["includes"].Text);
        Assert.Equal("I", sample.Scores["includes1"].Text);
        Assert.Equal(["includes", "includes1"], log.Results!.Scores.Select(s => s.Name));
    }

    [Fact]
    public async Task files_are_copied_and_the_setup_script_runs_before_the_solver()
    {
        Directory.CreateDirectory(_logDir);
        var hostFile = Path.Combine(_logDir, "source.txt");
        File.WriteAllText(hostFile, "from host");
        var sample = new Sample("read the files")
        {
            Target = "ok",
            Files = new Dictionary<string, string>
            {
                ["data/inline.txt"] = "inline text",
                ["encoded.bin"] = "data:application/octet-stream;base64," + Convert.ToBase64String("decoded"u8.ToArray()),
                ["copied.txt"] = hostFile,
            },
            Setup = "echo setup-ran > setup.txt",
        };
        var contents = new Dictionary<string, string>();
        Solver solver = async (state, _, ct) =>
        {
            var sandbox = SampleContext.Require().Sandbox();
            foreach (var path in new[] { "data/inline.txt", "encoded.bin", "copied.txt", "setup.txt" })
            {
                contents[path] = await sandbox.ReadFileAsync(path, ct);
            }

            state.Output = ModelOutput.FromContent("scripted", "ok");
            return state;
        };
        var task = new EvalTask
        {
            Name = "files",
            Dataset = new MemoryDataset([sample]),
            Sandbox = new SandboxSpec("local"),
            Solver = solver,
            Scorers = [Scorers.Includes()],
        };

        var log = await Eval.RunAsync(task, Options(new ScriptedModelApi()));

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal("inline text", contents["data/inline.txt"]);
        Assert.Equal("decoded", contents["encoded.bin"]);
        Assert.Equal("from host", contents["copied.txt"]);
        Assert.Equal("setup-ran\n", contents["setup.txt"]);
        var logged = Assert.Single(log.Samples!);
        Assert.Equal(["data/inline.txt", "encoded.bin", "copied.txt"], logged.Files!);
        Assert.Equal("echo setup-ran > setup.txt", logged.Setup);
        Assert.Equal(new SandboxSpec("local"), logged.Sandbox);
        Assert.Equal("C", logged.Scores!["includes"].Text);
        Assert.Contains(logged.Events, e => e is SpanBeginEvent { Name: "init", Type: "init" });
    }

    [Fact]
    public async Task a_failing_setup_script_fails_the_sample()
    {
        var task = new EvalTask
        {
            Name = "setup",
            Dataset = new MemoryDataset([new Sample("x") { Target = "x", Setup = "echo nope >&2\nexit 3" }]),
            Sandbox = new SandboxSpec("local"),
            FailOnError = false,
        };

        var log = await Eval.RunAsync(task, Options(new ScriptedModelApi()));

        var sample = Assert.Single(log.Samples!);
        Assert.StartsWith("Failed to execute setup script for sample: nope", sample.Error!.Message);
        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal(0, log.Results!.CompletedSamples);
    }

    [Fact]
    public async Task epochs_run_each_sample_repeatedly_and_reduce_scores_with_mean()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("yes"), ScriptedTurn.Text("no"));
        var task = new EvalTask
        {
            Name = "epochs",
            Dataset = new MemoryDataset([new Sample("answer yes") { Id = "q1", Target = "yes" }]),
            Scorers = [Scorers.Includes()],
            Epochs = new Epochs(2),
        };

        var log = await Eval.RunAsync(task, Options(api));

        Assert.Equal(2, log.Samples!.Count);
        Assert.Equal([1, 2], log.Samples.Select(s => s.Epoch));
        Assert.All(log.Samples, s => Assert.Equal("q1", s.Id));
        Assert.Equal(2, log.Results!.TotalSamples);
        Assert.Equal(2, log.Eval.Config.Epochs);
        var score = Assert.Single(log.Results.Scores);
        Assert.Null(score.Reducer);
        Assert.Equal(1, score.ScoredSamples);
        Assert.Equal(0.5, score.Metrics["accuracy"].Value);
    }

    [Fact]
    public async Task explicit_epoch_reducers_each_produce_a_named_view()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("yes"), ScriptedTurn.Text("no"));
        var task = new EvalTask
        {
            Name = "reducers",
            Dataset = new MemoryDataset([new Sample("answer yes") { Target = "yes" }]),
            Scorers = [Scorers.Includes()],
            Epochs = new Epochs(2, [Reducers.Max(), Reducers.Mean()]),
        };

        var log = await Eval.RunAsync(task, Options(api) with { Epochs = 2 });

        Assert.Equal(["max", "mean"], log.Results!.Scores.Select(s => s.Reducer));
        Assert.Equal(1.0, log.Results.Scores[0].Metrics["accuracy"].Value);
        Assert.Equal(0.5, log.Results.Scores[1].Metrics["accuracy"].Value);
    }

    [Fact]
    public async Task a_message_limit_ends_the_solver_and_the_sample_is_still_scored()
    {
        var noop = new ToolDef("noop", "Does nothing.", new ToolParams(), (_, _) => Task.FromResult<ToolResult>("done"));
        var turns = Enumerable.Range(0, 10).Select(i => ScriptedTurn.ToolCall("noop", new { }, id: $"c{i}")).ToArray();
        var task = new EvalTask
        {
            Name = "limits",
            Dataset = new MemoryDataset([new Sample("loop") { Target = "never" }]),
            Solver = Solvers.Chain(Solvers.UseTools(noop), Solvers.Generate()),
            Scorers = [Scorers.Includes()],
            MessageLimit = 4,
        };

        var log = await Eval.RunAsync(task, Options(new ScriptedModelApi(turns)));

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);
        Assert.Equal("message", sample.Limit!.Type);
        Assert.Equal(4, sample.Limit.Limit);
        Assert.Contains("Message limit", sample.Limit.Reason);
        Assert.Equal("I", sample.Scores!["includes"].Text);
        Assert.InRange(sample.Messages.Count, 4, 5);
        Assert.Equal(4, log.Eval.Config.MessageLimit);
    }

    [Fact]
    public async Task a_time_limit_ends_the_solver_and_the_sample_is_still_scored()
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
            Scorers = [Scorers.Includes()],
            TimeLimit = TimeSpan.FromMilliseconds(200),
        };

        var log = await Eval.RunAsync(task, Options(new ScriptedModelApi()));

        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);
        Assert.Equal("time", sample.Limit!.Type);
        Assert.Equal(0.2, sample.Limit.Limit, 3);
        Assert.Equal("I", sample.Scores!["includes"].Text);
    }

    [Fact]
    public async Task fail_on_error_false_records_the_failing_sample_and_the_eval_succeeds()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Throw(new InvalidOperationException("provider exploded")), ScriptedTurn.Text("4"));
        var task = new EvalTask
        {
            Name = "errors",
            Dataset = new MemoryDataset([new Sample("boom") { Target = "x" }, new Sample("2+2") { Target = "4" }]),
            Scorers = [Scorers.Includes()],
            FailOnError = false,
        };

        var log = await Eval.RunAsync(task, Options(api));

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Null(log.Error);
        Assert.Equal(2, log.Samples!.Count);
        var failed = log.Samples[0];
        Assert.Equal("provider exploded", failed.Error!.Message);
        Assert.Empty(failed.Scores!);
        Assert.Null(failed.Limit);
        Assert.Contains(failed.Events, e => e is ErrorEvent { Message: "provider exploded" });
        Assert.Null(log.Samples[1].Error);
        Assert.Equal("C", log.Samples[1].Scores!["includes"].Text);
        Assert.Equal(1, log.Results!.CompletedSamples);
        var score = Assert.Single(log.Results.Scores);
        Assert.Equal(1, score.ScoredSamples);
        Assert.Equal(1.0, score.Metrics["accuracy"].Value);
    }

    [Fact]
    public async Task fail_on_error_aborts_the_eval_with_error_status()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Throw(new InvalidOperationException("provider exploded")), ScriptedTurn.Text("4"));
        var task = new EvalTask
        {
            Name = "errors",
            Dataset = new MemoryDataset([new Sample("boom") { Target = "x" }, new Sample("2+2") { Target = "4" }]),
            Scorers = [Scorers.Includes()],
        };

        var log = await Eval.RunAsync(task, Options(api));

        Assert.Equal(EvalStatus.Error, log.Status);
        Assert.Equal("provider exploded", log.Error!.Message);
        var sample = Assert.Single(log.Samples!);
        Assert.Equal("provider exploded", sample.Error!.Message);
        Assert.Single(api.Requests);
        Assert.True(File.Exists(log.Location));
        Assert.Equal(EvalStatus.Error, EvalLogWriter.Read(log.Location!).Status);
    }

    [Fact]
    public async Task cancellation_cleans_up_the_sandbox_and_propagates()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? workingDirectory = null;
        Solver solver = async (state, _, ct) =>
        {
            workingDirectory = ((LocalSandboxEnvironment)SampleContext.Require().Sandbox()).WorkingDirectory;
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return state;
        };
        var task = new EvalTask
        {
            Name = "cancel",
            Dataset = new MemoryDataset([new Sample("wait") { Target = "x" }]),
            Sandbox = new SandboxSpec("local"),
            Solver = solver,
            Scorers = [Scorers.Includes()],
        };

        var run = Eval.RunAsync(task, Options(new ScriptedModelApi()), cts.Token);
        await started.Task;
        Assert.True(Directory.Exists(workingDirectory));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(Directory.Exists(workingDirectory));
        var logFile = Assert.Single(Directory.GetFiles(_logDir, "*.json"));
        var log = EvalLogWriter.Read(logFile);
        Assert.Equal(EvalStatus.Cancelled, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.NotNull(sample.Error);
    }

    [Fact]
    public async Task limit_and_sample_ids_select_the_samples_to_run()
    {
        var dataset = new MemoryDataset(Enumerable.Range(1, 3).Select(i => new Sample($"q{i}") { Target = "a" }));
        var task = new EvalTask { Name = "select", Dataset = dataset };

        var limited = await Eval.RunAsync(task, Options(new ScriptedModelApi()) with { Limit = 2 });
        var picked = await Eval.RunAsync(task, Options(new ScriptedModelApi()) with { SampleIds = ["3"], Limit = 1 });

        Assert.Equal([1, 2], limited.Samples!.Select(s => (int)s.Id));
        Assert.Equal(2, limited.Eval.Dataset.Samples);
        Assert.Equal(2, limited.Eval.Config.Limit);
        Assert.Equal(3, (int)Assert.Single(picked.Samples!).Id);
        Assert.Equal(["3"], picked.Eval.Config.SampleId!.Select(id => (string)id));
        Assert.Empty(picked.Results!.Scores);
    }

    [Fact]
    public async Task the_score_callback_scores_agent_states_against_the_samples_own_state()
    {
        var seen = new List<TaskState>();
        var probe = Scorers.Custom(
            "probe",
            (state, target, _) =>
            {
                seen.Add(state);
                return Task.FromResult(new Score(state.Output.Completion == target.Text ? ScoreConstants.Correct : ScoreConstants.Incorrect));
            },
            Metrics.Accuracy());
        Solver solver = async (state, _, _) =>
        {
            var foreign = new TaskState("other", 0, 9, "ignored", [new ChatMessageUser("agent prompt")], output: ModelOutput.FromContent("scripted", "Paris"));
            var scores = await SampleContext.Require().Scorer!(foreign);
            state.Output = ModelOutput.FromContent("scripted", scores[0].Text == ScoreConstants.Correct ? "Paris" : "Rome");
            return state;
        };
        var task = new EvalTask
        {
            Name = "score",
            Dataset = new MemoryDataset([new Sample("capital?") { Id = "fr", Target = "Paris", Metadata = new Dictionary<string, object?> { ["country"] = "France" } }]),
            Solver = solver,
            Scorers = [probe],
        };

        var log = await Eval.RunAsync(task, Options(new ScriptedModelApi()));

        Assert.Equal(2, seen.Count);
        var intermediate = seen[0];
        Assert.Equal("fr", intermediate.SampleId);
        Assert.Equal(1, intermediate.Epoch);
        Assert.Equal("scripted", intermediate.Model);
        Assert.Equal("Paris", intermediate.Target.Text);
        Assert.Equal("France", intermediate.Metadata["country"]);
        Assert.Equal("Paris", intermediate.Output.Completion);
        Assert.Equal("agent prompt", Assert.Single(intermediate.Messages).Text);
        var sample = Assert.Single(log.Samples!);
        Assert.Equal("C", sample.Scores!["probe"].Text);
        var scoreEvents = sample.Events.OfType<ScoreEvent>().ToList();
        Assert.Equal([true, false], scoreEvents.Select(e => e.Intermediate));
        Assert.Equal(1.0, log.Results!.Scores[0].Metrics["accuracy"].Value);
    }

    [Fact]
    public async Task a_token_limit_ends_the_solver_and_a_model_graded_scorer_still_grades()
    {
        var task = new EvalTask
        {
            Name = "tokens",
            Dataset = new MemoryDataset([new Sample("How many cores?") { Target = "4" }]),
            Scorers = [Scorers.ModelGradedQa()],
            TokenLimit = 100,
        };
        var api = new ScriptedModelApi(ScriptedTurn.Text("4 cores", new ModelUsage(150, 50, 200)), ScriptedTurn.Text("Correct.\n\nGRADE: C", new ModelUsage(10, 5, 15)));

        var log = await Eval.RunAsync(task, Options(api));

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);
        Assert.Equal("token", sample.Limit!.Type);
        Assert.Equal("C", sample.Scores!["model_graded_qa"].Text);
        Assert.Equal(2, api.Requests.Count);
        Assert.Equal(215, sample.ModelUsage["scripted"].TotalTokens);
    }

    [Fact]
    public async Task an_int_id_and_a_string_id_with_the_same_text_are_distinct_samples()
    {
        var dataset = new MemoryDataset(
        [
            new Sample("first") { Id = 1, Target = "a" },
            new Sample("second") { Id = "1", Target = "b" },
        ]);
        var task = new EvalTask { Name = "ids", Dataset = dataset, Scorers = [Scorers.Includes()], Epochs = new Epochs(2) };
        var api = new ScriptedModelApi(ScriptedTurn.Text("a"), ScriptedTurn.Text("b"), ScriptedTurn.Text("a"), ScriptedTurn.Text("b"));

        var log = await Eval.RunAsync(task, Options(api));

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal(4, log.Samples!.Count);
        Assert.Equal(2, Assert.Single(log.Results!.Scores).ScoredSamples);
        Assert.Contains(log.Samples, s => s.Id is int);
        Assert.Contains(log.Samples, s => s.Id is string);
    }
}

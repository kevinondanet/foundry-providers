using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Hooks;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Hooks;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.Hooks;

// Declared inside the namespace so that `Eval` names the runner rather than the InspectAzureAI.Eval namespace.
using Eval = InspectAzureAI.Eval.Runner.Eval;

/// <summary>
/// Tests for the port of <c>examples/hooks</c> (<see cref="HooksExample"/>, <see cref="MlflowTrackingHooks"/>,
/// <see cref="MlflowTracingHooks"/>, <see cref="TrackioHooks"/>, <see cref="WeaveHooks"/>): the task's shape, the
/// pure helpers, and <c>mlflow_tracing_example.py</c> run end to end without a network — the scripted model answers
/// the five questions and an in-memory <see cref="FakeMlflowServer"/> records what the hooks logged: the parent and
/// nested runs, their params, metrics and artifacts, and the trace's span tree.
/// </summary>
public sealed class HooksTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "hooks-" + Guid.NewGuid().ToString("N"));

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

    private static ExampleContext Context(bool fake = true, params (string Key, string Value)[] taskArgs) =>
        new(Path.Combine(AppContext.BaseDirectory, "hooks"), null, fake, taskArgs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), null, null, TextWriter.Null);

    private static MlflowSettings Settings => new("http://fake-mlflow", "inspect-mlflow-demo", Tracing: true);

    /// <summary>Runs the example's task with the five hooks over a fake server, returning the log, the server and the report output.</summary>
    private async Task<(EvalLog Log, FakeMlflowServer Server, string Report)> RunAsync(MlflowSettings? settings = null)
    {
        var server = new FakeMlflowServer();
        var output = new StringWriter();
        var log = await Eval.RunAsync(
            HooksExample.Build(),
            new EvalOptions
            {
                Model = HooksExample.CreateFakeModel(),
                LogDir = _logDir,
                LogFormat = LogFormat.Eval,
                Hooks = HooksExample.CreateHooks(settings ?? Settings, server, output),
            },
            CancellationToken.None);
        return (log, server, output.ToString());
    }

    private static string SpanType(JsonObject span) => JsonSerializer.Deserialize<string>(span["attributes"]!["mlflow.spanType"]!.GetValue<string>())!;

    // ----------------------------------------------------------------------------------------------------------
    // the task (port of the unnamed Task of mlflow_tracing_example.py)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_task_is_named_task_and_holds_the_five_arithmetic_samples()
    {
        var task = HooksExample.ArithmeticTask();

        Assert.Equal("task", task.Name);
        Assert.Equal(
            [("What is 2 + 2?", "4"), ("What is 3 * 5?", "15"), ("What is 10 - 7?", "3"), ("What is 8 / 2?", "4"), ("What is 6 + 9?", "15")],
            task.Dataset.Select(sample => (sample.Input.Text!, sample.Target.Text)));
        Assert.Equal("match", Assert.Single(task.Scorers).Name);
        Assert.Null(task.Sandbox);
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli_as_task()
    {
        var method = typeof(HooksExample).GetMethod(nameof(HooksExample.ArithmeticTask), BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.Equal("task", method!.GetCustomAttribute<TaskAttribute>()!.Name);
    }

    [Fact]
    public void the_example_is_registered_without_a_sandbox_and_names_the_hooks()
    {
        var example = Assert.IsType<HooksExample>(ExampleRegistry.Default.Get("hooks"));

        Assert.Equal("hooks", example.Name);
        Assert.Equal(["task"], example.Tasks.Select(task => task.Name));
        Assert.Equal("none", example.Defaults.Sandbox);
        Assert.Null(example.FakeSandbox(Context()));
        Assert.NotEmpty(example.Deviations);
        Assert.Equal(["mlflow_tracking", "mlflow_tracing", "trackio_tracking", "weave_hooks", "hooks_example_report"], HooksExample.HookNames);
        Assert.Equal("2 + 2 = 4", HooksExample.FakeAnswer("What is 2 + 2?"));
        Assert.Equal("10 - 7 = 3", HooksExample.FakeAnswer("What is 10 - 7?"));
    }

    [Fact]
    public void the_settings_follow_the_python_environment_checks()
    {
        var previous = new[] { "MLFLOW_TRACKING_URI", "MLFLOW_EXPERIMENT_NAME", "MLFLOW_INSPECT_TRACING", "MLFLOW_INSPECT_LOG_ARTIFACTS" }
            .ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        try
        {
            foreach (var name in previous.Keys)
            {
                Environment.SetEnvironmentVariable(name, null);
            }

            Assert.Null(MlflowSettings.FromEnvironment());
            Assert.False(new MlflowTrackingHooks().Enabled);
            Assert.False(new MlflowTracingHooks().Enabled);

            Environment.SetEnvironmentVariable("MLFLOW_TRACKING_URI", "http://localhost:5000");
            var minimal = MlflowSettings.FromEnvironment()!;
            Assert.Equal(new MlflowSettings("http://localhost:5000", "inspect_ai", Tracing: false, LogArtifacts: true), minimal);
            Assert.True(new MlflowTrackingHooks().Enabled);
            Assert.False(new MlflowTracingHooks().Enabled);

            Environment.SetEnvironmentVariable("MLFLOW_EXPERIMENT_NAME", "inspect-evals");
            Environment.SetEnvironmentVariable("MLFLOW_INSPECT_TRACING", "TRUE");
            Environment.SetEnvironmentVariable("MLFLOW_INSPECT_LOG_ARTIFACTS", "false");
            Assert.Equal(new MlflowSettings("http://localhost:5000", "inspect-evals", Tracing: true, LogArtifacts: false), MlflowSettings.FromEnvironment());
            Assert.True(new MlflowTracingHooks().Enabled);

            // the example prefers the environment over its hard-coded server
            var resolved = HooksExample.ResolveSettings(Context());
            Assert.Equal("http://localhost:5000", resolved.TrackingUri);
            Assert.Equal("inspect-evals", resolved.ExperimentName);
            Assert.True(resolved.Tracing);
            Assert.False(resolved.LogArtifacts);
        }
        finally
        {
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        Assert.Equal(new MlflowSettings("http://127.0.0.1:5556", "inspect-mlflow-demo", Tracing: true), HooksExample.ResolveSettings(Context()) with { LogArtifacts = true });
        Assert.Equal("http://x", HooksExample.ResolveSettings(Context(taskArgs: ("mlflow_uri", "http://x"))).TrackingUri);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the helpers (ports of _score_to_numeric, _truncate, _safe_log_params)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void score_to_numeric_maps_numbers_and_the_letter_grades()
    {
        Assert.Equal(1.0, MlflowTrackingHooks.ScoreToNumeric(new ScoreValue.Str("C")));
        Assert.Equal(0.0, MlflowTrackingHooks.ScoreToNumeric(new ScoreValue.Str("I")));
        Assert.Equal(0.5, MlflowTrackingHooks.ScoreToNumeric(new ScoreValue.Str("P")));
        Assert.Equal(1.0, MlflowTrackingHooks.ScoreToNumeric(new ScoreValue.Str("correct")));
        Assert.Equal(0.0, MlflowTrackingHooks.ScoreToNumeric(new ScoreValue.Str("incorrect")));
        Assert.Null(MlflowTrackingHooks.ScoreToNumeric(new ScoreValue.Str("maybe")));
        Assert.Equal(0.25, MlflowTrackingHooks.ScoreToNumeric(new ScoreValue.Num(0.25)));
        Assert.Null(MlflowTrackingHooks.ScoreToNumeric(new ScoreValue.List([new ScoreValue.Num(1)])));
    }

    [Fact]
    public void truncation_keeps_the_limit_and_ends_with_an_ellipsis()
    {
        var text = new string('x', 600);

        Assert.Equal(500, MlflowTrackingHooks.Truncate(text).Length);
        Assert.EndsWith("...", MlflowTrackingHooks.Truncate(text), StringComparison.Ordinal);
        Assert.Equal("short", MlflowTrackingHooks.Truncate("short"));
        Assert.Equal("", MlflowTrackingHooks.Truncate(null));
        Assert.Equal(new string('x', 497) + "...", MlflowTrackingHooks.TruncateParam(text));
        Assert.Equal(200, MlflowTracingHooks.Truncate(text).Length);
        Assert.Equal(300, MlflowTrackingHooks.Truncate(text, 300).Length);
    }

    // ----------------------------------------------------------------------------------------------------------
    // end to end (port of mlflow_tracing_example.py, offline)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_tracking_hook_logs_a_parent_run_and_a_nested_task_run_with_params_metrics_and_artifacts()
    {
        var (log, server, _) = await RunAsync();

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal(5, log.Results!.CompletedSamples);
        Assert.Equal("1", server.Experiments["inspect-mlflow-demo"]);

        // one parent run per eval invocation, one nested run per task
        Assert.Equal(2, server.Runs.Count);
        var parent = server.Runs[0];
        var task = server.Runs[1];
        var runId = log.Eval.RunId;
        Assert.Equal($"inspect-{runId[..8]}", parent.RunName);
        Assert.Equal("FINISHED", parent.Status);
        Assert.Null(parent.ParentRunId);
        Assert.Equal(runId, parent.Tags["inspect.run_id"]);
        Assert.Equal("1", parent.Tags["inspect.task_count"]);
        Assert.Equal("task", parent.Tags["inspect.tasks"]);
        Assert.Equal("task", task.RunName);
        Assert.Equal("FINISHED", task.Status);
        Assert.Equal(parent.RunId, task.ParentRunId);
        Assert.Equal(log.Eval.EvalId, task.Tags["inspect.eval_id"]);
        Assert.Equal("task", task.Tags["inspect.task"]);
        Assert.Equal(HooksExample.FakeModelName, task.Tags["inspect.model"]);

        // task configuration as params
        Assert.Equal("task", task.Params["task"]);
        Assert.Equal(HooksExample.FakeModelName, task.Params["model"]);
        Assert.Equal("0", task.Params["task_version"]);
        Assert.Equal("5", task.Params["dataset.samples"]);
        Assert.True(task.Params.ContainsKey("dataset.name"));
        Assert.True(task.Params.ContainsKey("solver"));
        Assert.Empty(parent.Params);

        // per-sample step metrics, event metrics, aggregate results
        var sampleScores = task.MetricSeries("sample/match");
        Assert.Equal([0L, 1L, 2L, 3L, 4L], sampleScores.Select(metric => metric.Step));
        Assert.All(sampleScores, metric => Assert.Equal(1.0, metric.Value));
        Assert.Equal(5, task.MetricSeries("sample/total_time").Count);
        Assert.Equal([0L, 1L, 2L, 3L, 4L], task.MetricSeries("event/model_call").Select(metric => metric.Step));
        Assert.Equal(1.0, task.Metric("match/accuracy"));
        Assert.Equal(0.0, task.Metric("match/stderr"));
        Assert.Equal(5.0, task.Metric("total_samples"));
        Assert.Equal(5.0, task.Metric("completed_samples"));
        Assert.Equal(5.0, task.Metric("total_model_calls"));
        Assert.Equal(0.0, task.Metric("total_tool_calls"));
        Assert.Empty(parent.Metrics);

        // artifacts: the sample table and the eval log without its samples
        var table = Assert.Single(server.ArtifactPaths($"{task.ExperimentId}/{task.RunId}/artifacts/sample_results/"));
        Assert.EndsWith($"sample_results_{log.Eval.EvalId}.json", table, StringComparison.Ordinal);
        var rows = JsonNode.Parse(server.ArtifactText(table)!)!.AsArray();
        Assert.Equal(5, rows.Count);
        Assert.Equal("What is 2 + 2?", rows[0]!["input"]!.GetValue<string>());
        Assert.Equal("4", rows[0]!["target"]!.GetValue<string>());
        Assert.Equal("2 + 2 = 4", rows[0]!["output"]!.GetValue<string>());
        Assert.Equal("C", rows[0]!["score/match"]!.GetValue<string>());
        Assert.Null(rows[0]!["error"]);
        var evalLog = Assert.Single(server.ArtifactPaths($"{task.ExperimentId}/{task.RunId}/artifacts/eval_logs/"));
        var header = JsonNode.Parse(server.ArtifactText(evalLog)!)!.AsObject();
        Assert.False(header.ContainsKey("samples"));
        Assert.Equal("success", header["status"]!.GetValue<string>());
        Assert.Equal("task", header["eval"]!["task"]!.GetValue<string>());

        // the REST calls, in the order Python's client would make them
        var routes = server.RouteCounts().Select(route => route.Route).ToList();
        Assert.Equal("GET /api/2.0/mlflow/experiments/get-by-name", routes[0]);
        Assert.Contains("POST /api/2.0/mlflow/experiments/create", routes);
        Assert.Contains("POST /api/2.0/mlflow/runs/create", routes);
        Assert.Contains("POST /api/2.0/mlflow/runs/log-batch", routes);
        Assert.Contains("POST /api/2.0/mlflow/runs/update", routes);
    }

    [Fact]
    public async Task the_tracing_hook_logs_one_trace_whose_spans_mirror_the_eval_hierarchy()
    {
        var (log, server, _) = await RunAsync();

        var trace = Assert.Single(server.Traces);
        var traceId = trace["trace_id"]!.GetValue<string>();
        Assert.StartsWith("tr-", traceId, StringComparison.Ordinal);
        Assert.Equal("OK", trace["state"]!.GetValue<string>());
        Assert.Equal($"eval_run:{log.Eval.RunId[..8]}", trace["tags"]!["mlflow.traceName"]!.GetValue<string>());
        Assert.Equal("1", trace["trace_location"]!["mlflow_experiment"]!["experiment_id"]!.GetValue<string>());
        Assert.Contains("task_names", trace["trace_metadata"]!["mlflow.traceInputs"]!.GetValue<string>());

        var spans = server.TraceSpans(traceId);
        Assert.NotEmpty(spans);
        Assert.All(spans, span => Assert.NotNull(span["end_time_unix_nano"]));
        Assert.All(spans, span => Assert.Equal("STATUS_CODE_OK", span["status"]!["code"]!.GetValue<string>()));
        Assert.All(spans, span => Assert.Equal(JsonSerializer.Serialize(traceId), span["attributes"]!["mlflow.traceRequestId"]!.GetValue<string>()));

        var root = Assert.Single(spans, span => span["parent_span_id"] is null);
        Assert.Equal($"eval_run:{log.Eval.RunId[..8]}", root["name"]!.GetValue<string>());
        Assert.Equal("CHAIN", SpanType(root));
        var rootId = root["span_id"]!.GetValue<string>();

        var task = Assert.Single(spans, span => span["parent_span_id"]?.GetValue<string>() == rootId);
        Assert.Equal("task:task", task["name"]!.GetValue<string>());
        Assert.Equal("CHAIN", SpanType(task));
        var taskId = task["span_id"]!.GetValue<string>();
        Assert.Contains("\"completed_samples\":5", task["attributes"]!["mlflow.spanOutputs"]!.GetValue<string>());

        var samples = spans.Where(span => span["parent_span_id"]?.GetValue<string>() == taskId).ToList();
        Assert.Equal(5, samples.Count);
        foreach (var sample in samples)
        {
            Assert.StartsWith("sample:", sample["name"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal("CHAIN", SpanType(sample));
            var descendants = Descendants(spans, sample["span_id"]!.GetValue<string>());
            var model = Assert.Single(descendants, span => span["name"]!.GetValue<string>() == $"model:{HooksExample.FakeModelName}");
            Assert.Equal("LLM", SpanType(model));
            Assert.Contains("\"response\"", model["attributes"]!["mlflow.spanOutputs"]!.GetValue<string>());
            var score = Assert.Single(descendants, span => span["name"]!.GetValue<string>() == "score");
            Assert.Equal("EVALUATOR", SpanType(score));
            Assert.Contains("\"value\":\"C\"", score["attributes"]!["mlflow.spanOutputs"]!.GetValue<string>());
            Assert.Contains("\"target\"", score["attributes"]!["mlflow.spanInputs"]!.GetValue<string>());
            // the transcript's own spans (solvers/scorers) sit between the sample and its model/score spans
            Assert.Contains(descendants, span => span["name"]!.GetValue<string>() == "solvers");
            Assert.Contains(descendants, span => span["name"]!.GetValue<string>() == "scorers");
        }

        Assert.Contains("POST /api/3.0/mlflow/traces", server.RouteCounts().Select(route => route.Route));
        Assert.Single(server.ArtifactPaths("1/traces/"), path => path.EndsWith("/traces.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task the_report_prints_the_python_scripts_verification()
    {
        var (log, _, report) = await RunAsync();

        Assert.Contains("Eval status: success", report);
        Assert.Contains("  match/accuracy: 1", report);
        Assert.Contains("  Total samples: 5", report);
        Assert.Contains("Verifying MLflow runs (tracking hook)...", report);
        Assert.Contains("  Runs found: 2", report);
        Assert.Contains($"    inspect-{log.Eval.RunId[..8]}: FINISHED", report);
        Assert.Contains("    task: FINISHED", report);
        Assert.Contains("Verifying MLflow traces (tracing hook)...", report);
        Assert.Contains("  Traces found: 1", report);
        Assert.Contains("  Spans in trace: ", report);
        Assert.Contains($"    eval_run:{log.Eval.RunId[..8]} (CHAIN)", report);
        Assert.Contains("      task:task (CHAIN)", report);
        Assert.Contains($"model:{HooksExample.FakeModelName} (LLM)", report);
        Assert.Contains("score (EVALUATOR)", report);
        Assert.Contains("Recorded MLflow requests (fake server): ", report);
        Assert.Contains("  run task (FINISHED, nested)", report);
        Assert.Contains("Done. Open http://fake-mlflow to see runs and traces.", report);
    }

    [Fact]
    public async Task the_tracing_hook_stays_quiet_when_tracing_is_off_and_artifacts_can_be_disabled()
    {
        var (_, server, report) = await RunAsync(new MlflowSettings("http://fake-mlflow", "quiet", Tracing: false, LogArtifacts: false));

        Assert.Empty(server.Traces);
        Assert.Equal(2, server.Runs.Count);
        Assert.Empty(server.ArtifactPaths());
        Assert.Contains("  Traces found: 0", report);
    }

    [Fact]
    public async Task the_disabled_hooks_never_touch_the_server()
    {
        var server = new FakeMlflowServer();
        var log = await Eval.RunAsync(
            HooksExample.Build(),
            new EvalOptions
            {
                Model = HooksExample.CreateFakeModel(),
                LogDir = _logDir,
                LogFormat = LogFormat.Eval,
                Hooks = [new MlflowTrackingHooks(() => null, server), new MlflowTracingHooks(() => null, server), new TrackioHooks(), new WeaveHooks()],
            },
            CancellationToken.None);

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Empty(server.Requests);
        Assert.False(new TrackioHooks().Enabled);
        Assert.False(new WeaveHooks().Enabled);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the stubs' payload builders (ports of trackio_tracking.py and wandb_weave.py data shaping)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task trackio_builds_the_trace_record_python_would_log()
    {
        var (log, _, _) = await RunAsync();
        var sample = log.Samples![0];
        var trace = TrackioHooks.BuildTrace(new SampleEnd(null, log.Eval.RunId, log.Eval.EvalId, sample.Uuid ?? "sample-uuid", sample));

        // the conversation, then the completion appended as Python does
        Assert.Equal(["user", "assistant", "assistant"], trace.Messages.Select(message => message.Role));
        Assert.Equal("What is 2 + 2?", trace.Messages[0].Content);
        Assert.Equal("2 + 2 = 4", trace.Messages[^1].Content);
        Assert.Equal("1", trace.Metadata["sample_id"]!.GetValue<string>());
        Assert.Equal(1, trace.Metadata["epoch"]!.GetValue<int>());
        Assert.Equal("4", trace.Metadata["target"]!.GetValue<string>());
        Assert.Equal(log.Eval.EvalId, trace.Metadata["eval_id"]!.GetValue<string>());
        Assert.Equal(log.Eval.RunId, trace.Metadata["run_id"]!.GetValue<string>());
        Assert.Equal("C", trace.Metadata["score/match"]!.GetValue<string>());
        Assert.Equal("2 + 2 = 4", trace.Metadata["score/match/explanation"]!.GetValue<string>());

        Assert.Equal("a\nb", TrackioHooks.ContentToText(MessageContent.FromItems([new ContentText("a"), new ContentText("b")])));
        Assert.Equal("", TrackioHooks.ContentToText(null));
        Assert.Equal([], TrackioHooks.ScoresToMetadata(null));
    }

    [Fact]
    public async Task weave_builds_the_thread_id_and_sample_complete_payloads_python_would_send()
    {
        var (log, _, _) = await RunAsync();
        var sample = log.Samples![0];
        var spec = log.Eval with { Model = "openai/gpt-4o-mini" };

        Assert.Equal("task-1[1]-openai-gpt-4o-mini-abc", WeaveHooks.ThreadId(spec, sample.Id, sample.Epoch, "abc"));
        Assert.Equal("task-1[1]-abc", WeaveHooks.ThreadId(null, sample.Id, sample.Epoch, "abc"));

        var inputs = WeaveHooks.SampleCompleteInputs(sample, spec);
        Assert.Equal(1, inputs["id"]!.GetValue<int>());
        Assert.Equal(1, inputs["epoch"]!.GetValue<int>());
        Assert.Equal("openai/gpt-4o-mini", inputs["model"]!.GetValue<string>());
        Assert.NotNull(inputs["input"]);

        var output = WeaveHooks.SampleCompleteOutput(sample);
        Assert.Equal("2 + 2 = 4", output["output"]!["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        Assert.Equal("C", output["scores"]!["match"]!["value"]!.GetValue<string>());
        Assert.Null(output["error"]);
        Assert.NotNull(output["total_time"]);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the runner
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_runner_runs_the_example_offline_registers_the_hooks_for_the_run_and_exits_0()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["hooks", "--fake", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("mlflow    : in-memory fake server", text);
        Assert.Contains("trackio   : disabled", text);
        Assert.Contains("weave     : disabled", text);
        Assert.Contains("task      : task", text);
        Assert.Contains("dataset   : 5 samples", text);
        Assert.Contains("  Runs found: 2", text);
        Assert.Contains("  Traces found: 1", text);
        Assert.Contains("Recorded MLflow requests (fake server): ", text);
        Assert.Contains("status    : success (5/5 samples completed)", text);
        Assert.Contains($"{"match",-24} {"accuracy",-20} {"1.000",10}", text);
        // the hooks were registered for the run and removed at its end
        Assert.All(HooksExample.HookNames, name => Assert.Null(HookRegistry.Lookup(name)));
    }

    private static List<JsonObject> Descendants(IReadOnlyList<JsonObject> spans, string parentId)
    {
        var result = new List<JsonObject>();
        foreach (var span in spans.Where(span => span["parent_span_id"]?.GetValue<string>() == parentId))
        {
            result.Add(span);
            result.AddRange(Descendants(spans, span["span_id"]!.GetValue<string>()));
        }

        return result;
    }
}

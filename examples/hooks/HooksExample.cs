using System.Globalization;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Hooks;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Hooks;

using Hooks = InspectAzureAI.Eval.Hooks.Hooks;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/hooks/</c> as an <see cref="IExample"/>: the five-sample arithmetic eval of
/// <c>mlflow_tracing_example.py</c> (<c>generate()</c> + <c>match()</c>) run with the ported lifecycle hooks registered
/// the way Python's <c>@hooks</c> decorator registers them at import — <see cref="MlflowTrackingHooks"/> and
/// <see cref="MlflowTracingHooks"/> over the MLflow REST API, and the <see cref="TrackioHooks"/> and
/// <see cref="WeaveHooks"/> stubs — followed by the script's verification printout (runs, then the span tree).
/// Deviation: under <c>--fake</c> (with no <c>MLFLOW_TRACKING_URI</c>) the hooks talk to an in-memory
/// <see cref="FakeMlflowServer"/> whose recorded requests the example prints; Python needs a real
/// <c>mlflow server</c>.
/// </summary>
public sealed class HooksExample : IExample, IExampleHooks
{
    private IReadOnlyList<Hooks>? _runHooks;

    /// <summary>The Python <c>Task(...)</c> of <c>mlflow_tracing_example.py</c> has no name, so inspect calls it <c>task</c>.</summary>
    public const string TaskName = "task";

    /// <summary>The script's hard-coded tracking server (<c>mlflow server --port 5556</c>).</summary>
    public const string DefaultTrackingUri = "http://127.0.0.1:5556";

    /// <summary>The script's experiment name.</summary>
    public const string DefaultExperimentName = "inspect-mlflow-demo";

    /// <summary>The name the example's report hook is registered under.</summary>
    public const string ReportHookName = "hooks_example_report";

    public const string FakeModelName = "hooks-scripted";

    /// <summary>The samples of <c>mlflow_tracing_example.py</c>, verbatim.</summary>
    public static readonly IReadOnlyList<(string Input, string Target)> Samples =
    [
        ("What is 2 + 2?", "4"),
        ("What is 3 * 5?", "15"),
        ("What is 10 - 7?", "3"),
        ("What is 8 / 2?", "4"),
        ("What is 6 + 9?", "15"),
    ];

    public string Name => "hooks";

    public string Description => "Lifecycle hooks: MLflow tracking (runs, params, metrics, artifacts) and MLflow tracing (a span tree) over the MLflow REST API, plus the Trackio and W&B Weave hooks, on the five-question arithmetic eval of mlflow_tracing_example.py";

    public HooksExample()
    {
        Tasks = [new ExampleTask(TaskName, Build, "mlflow_tracing_example.py: five arithmetic questions, generate() scored by match()")];
    }

    public IReadOnlyList<ExampleTask> Tasks { get; }

    public ExampleDefaults Defaults { get; } = new(Sandbox: "none");

    public IReadOnlyList<string> Deviations { get; } =
    [
        "The mlflow, trackio and weave Python clients have no .NET equivalents: the two MLflow hooks talk to the tracking server's REST API through MlflowClient (experiments, runs/create with mlflow.runName + mlflow.parentRunId tags, runs/log-batch, runs/update, proxied mlflow-artifacts uploads, runs/search); mlflow.start_span_no_context has no REST twin, so spans are buffered in an MlflowTrace and logged when the run ends (POST api/3.0/mlflow/traces + the spans as the trace's traces.json artifact), which is also when the Python exporter ships them. Only the in-memory fake server was exercised; a real MLflow 3 server was not.",
        "TrackioHooks is not portable (trackio is a Python-only local library with no HTTP API) and WeaveHooks is a stub (the weave client's trace-server protocol is documented in neither repository): both are registered, always disabled, and only their payload builders (Trace messages/metadata, thread id, sample_complete inputs/output) are ported.",
        "Per-sample and per-event metrics are logged to the task's run by eval_id rather than to MLflow's ambient 'active run'; artifact files are named sample_results_<eval_id>.json and eval_log_<eval_id>.json (no mkstemp suffix); param values longer than 500 characters are truncated as in Python.",
        "Registration: Python registers the hooks at import (@hooks); here HooksExample hands them to the runner for the run (IExampleHooks), so they never enter the process-wide HookRegistry and cannot leak into other runs of the same process (Register puts them in the registry for hosts that want that). The CLI can also load them by type name (--hooks MlflowTrackingHooks,MlflowTracingHooks), which reads the same environment variables as Python.",
        "The verification printout (runs, then the span tree) runs from a hook at run end rather than after eval() returns, so it precedes the runner's summary. Under --fake with no MLFLOW_TRACKING_URI the hooks use an in-memory fake MLflow server and the example prints the requests it recorded; set MLFLOW_TRACKING_URI to use a real server even with the scripted model.",
        "The Python script targets openai/gpt-4o-mini and logs to /tmp/inspect-mlflow-demo-logs; here the model is the runner's Foundry deployment (or the scripted one) and --log-dir chooses the log directory.",
    ];

    /// <summary>Port of the unnamed <c>Task(dataset=..., solver=generate(), scorer=match())</c> of <c>mlflow_tracing_example.py</c>.</summary>
    [Task(TaskName)]
    public static EvalTask ArithmeticTask() => Build();

    public static EvalTask Build() => new()
    {
        Name = TaskName,
        Dataset = new MemoryDataset(Samples.Select(sample => new Sample(sample.Input) { Target = sample.Target })),
        Solver = Solvers.Generate(),
        Scorers = [Scorers.Match()],
    };

    /// <summary>
    /// The hooks of one run, in the order Python imports them plus the example's report: tracking, tracing (both on
    /// <paramref name="settings"/> over <paramref name="handler"/>), the Trackio and Weave stubs, and
    /// <see cref="HooksReport"/> printing to <paramref name="output"/>. With <paramref name="unregister"/> the report
    /// removes all five from <see cref="HookRegistry"/> when the run ends.
    /// </summary>
    public static IReadOnlyList<Hooks> CreateHooks(MlflowSettings? settings, HttpMessageHandler? handler, TextWriter output, bool unregister = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        Func<MlflowSettings?> provider = settings is null ? MlflowSettings.FromEnvironment : () => settings;
        return
        [
            new MlflowTrackingHooks(provider, handler),
            new MlflowTracingHooks(provider, handler),
            new TrackioHooks(),
            new WeaveHooks(),
            new HooksReport(provider, handler, output, unregister),
        ];
    }

    /// <summary>Port of the <c>@hooks</c> registrations: names and descriptions as in Python.</summary>
    public static void Register(IReadOnlyList<Hooks> hooks)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        foreach (var hook in hooks)
        {
            var (name, description) = hook switch
            {
                MlflowTrackingHooks => (MlflowTrackingHooks.HookName, MlflowTrackingHooks.HookDescription),
                MlflowTracingHooks => (MlflowTracingHooks.HookName, MlflowTracingHooks.HookDescription),
                TrackioHooks => (TrackioHooks.HookName, TrackioHooks.HookDescription),
                WeaveHooks => (WeaveHooks.HookName, WeaveHooks.HookDescription),
                HooksReport => (ReportHookName, "Prints the MLflow verification of mlflow_tracing_example.py"),
                _ => (hook.GetType().Name, hook.GetType().Name),
            };
            HookRegistry.Register(hook, name, description);
        }
    }

    /// <summary>The names <see cref="Register"/> uses.</summary>
    public static IReadOnlyList<string> HookNames { get; } =
    [
        MlflowTrackingHooks.HookName,
        MlflowTracingHooks.HookName,
        TrackioHooks.HookName,
        WeaveHooks.HookName,
        ReportHookName,
    ];

    /// <summary>
    /// The settings of a run: the environment when <c>MLFLOW_TRACKING_URI</c> is set (Python's <c>os.getenv</c>), else
    /// the script's hard-coded values (<see cref="DefaultTrackingUri"/>, <see cref="DefaultExperimentName"/>,
    /// tracing on) — which under <c>--fake</c> point at the in-memory server.
    /// </summary>
    public static MlflowSettings ResolveSettings(ExampleContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var fromEnvironment = MlflowSettings.FromEnvironment();
        var uri = ctx.TaskArg("mlflow_uri") ?? fromEnvironment?.TrackingUri ?? DefaultTrackingUri;
        var experiment = ctx.TaskArg("experiment") ?? fromEnvironment?.ExperimentName ?? DefaultExperimentName;
        return new MlflowSettings(uri, experiment, Tracing: true, LogArtifacts: fromEnvironment?.LogArtifacts ?? true);
    }

    public Model CreateFakeModel(ExampleContext ctx) => CreateFakeModel();

    /// <summary>A scripted model answering each arithmetic question (<c>"2 + 2 = 4"</c>; <c>match()</c> looks at the end).</summary>
    public static Model CreateFakeModel() => new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(Respond), Samples.Count * 2), FakeModelName));

    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;

    /// <summary>The answer the scripted model gives to <paramref name="question"/> (the expression restated with its result).</summary>
    public static string FakeAnswer(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var match = Samples.FirstOrDefault(sample => sample.Input == question);
        if (match.Input is null)
        {
            return "I only know the five questions of mlflow_tracing_example.py.";
        }

        var expression = question["What is ".Length..].TrimEnd('?');
        return $"{expression} = {match.Target}";
    }

    private static ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> _)
    {
        var question = messages.OfType<ChatMessageUser>().LastOrDefault()?.Text ?? "";
        return ModelOutput.FromContent(FakeModelName, FakeAnswer(question));
    }

    private EvalTask Build(ExampleContext ctx)
    {
        var settings = ResolveSettings(ctx);
        var useFakeServer = ctx.Fake && MlflowSettings.FromEnvironment() is null && ctx.TaskArg("mlflow_uri") is null;
        var handler = useFakeServer ? new FakeMlflowServer() : null;
        ctx.Out.WriteLine(useFakeServer
            ? $"mlflow    : in-memory fake server (set {MlflowSettings.TrackingUriVariable} to use a real one); experiment {settings.ExperimentName}; tracing on"
            : $"mlflow    : {settings.TrackingUri}; experiment {settings.ExperimentName}; tracing on");
        ctx.Out.WriteLine($"trackio   : disabled ({TrackioHooks.NotPortable})");
        ctx.Out.WriteLine($"weave     : disabled ({WeaveHooks.NotPorted})");
        _runHooks = CreateHooks(settings, handler, ctx.Out);
        return Build();
    }

    /// <summary>The hooks built with the task (<see cref="IExampleHooks"/>); empty before the task is built.</summary>
    public IReadOnlyList<Hooks> Hooks(ExampleContext ctx) => _runHooks ?? [];
}

/// <summary>
/// The tail of <c>mlflow_tracing_example.py</c>'s <c>main()</c> as a hook: at run end prints the eval status and
/// metrics, then verifies the tracking hook's runs (<c>search_runs</c>) and the tracing hook's trace
/// (<c>search_traces</c> + <c>get_trace</c>, printed as an indented span tree). Over a <see cref="FakeMlflowServer"/> it
/// also lists the requests the server recorded and the params/metrics of each run. Optionally unregisters the
/// example's hooks from <see cref="HookRegistry"/> afterwards.
/// </summary>
public sealed class HooksReport(Func<MlflowSettings?> settings, HttpMessageHandler? handler, TextWriter output, bool unregister = false) : Hooks
{
    private readonly Func<MlflowSettings?> _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    private readonly TextWriter _output = output ?? throw new ArgumentNullException(nameof(output));

    public override async Task OnRunEndAsync(RunEnd data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            if (data.Logs.Count > 0)
            {
                PrintEval(data.Logs[0]);
            }

            if (_settings() is { } settings)
            {
                await VerifyAsync(settings, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _output.WriteLine();
                _output.WriteLine($"MLflow hooks were disabled ({MlflowSettings.TrackingUriVariable} is not set); nothing to verify.");
            }

            if (handler is FakeMlflowServer fake)
            {
                PrintRecorded(fake);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _output.WriteLine($"MLflow verification failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (unregister)
            {
                foreach (var name in HooksExample.HookNames)
                {
                    HookRegistry.Unregister(name);
                }
            }
        }
    }

    private void PrintEval(EvalLog log)
    {
        _output.WriteLine();
        _output.WriteLine($"Eval status: {log.Status.ToString().ToLowerInvariant()}");
        if (log.Results is { } results)
        {
            foreach (var score in results.Scores)
            {
                foreach (var (metricName, metric) in score.Metrics)
                {
                    _output.WriteLine($"  {score.Name}/{metricName}: {metric.Value.ToString(CultureInfo.InvariantCulture)}");
                }
            }

            _output.WriteLine($"  Total samples: {results.TotalSamples}");
            _output.WriteLine($"  Completed: {results.CompletedSamples}");
        }
    }

    private async Task VerifyAsync(MlflowSettings settings, CancellationToken cancellationToken)
    {
        using var client = new MlflowClient(settings.TrackingUri, handler);
        var experimentId = await client.GetOrCreateExperimentAsync(settings.ExperimentName, cancellationToken).ConfigureAwait(false);

        // Verify MLflow runs (from tracking hook)
        _output.WriteLine();
        _output.WriteLine("Verifying MLflow runs (tracking hook)...");
        var runs = await client.SearchRunsAsync(experimentId, cancellationToken).ConfigureAwait(false);
        _output.WriteLine($"  Runs found: {runs.Count}");
        foreach (var run in runs)
        {
            _output.WriteLine($"    {run.RunName}: {run.Status}");
        }

        // Verify MLflow traces (from tracing hook)
        _output.WriteLine();
        _output.WriteLine("Verifying MLflow traces (tracing hook)...");
        var traces = await client.SearchTracesAsync(experimentId, cancellationToken).ConfigureAwait(false);
        _output.WriteLine($"  Traces found: {traces.Count}");
        if (traces.Count > 0)
        {
            var spans = await client.GetTraceSpansAsync(traces[^1], cancellationToken).ConfigureAwait(false);
            _output.WriteLine($"  Spans in trace: {spans.Count}");
            var byId = spans.ToDictionary(span => span.SpanId, StringComparer.Ordinal);
            foreach (var span in spans)
            {
                var depth = 0;
                var parentId = span.ParentSpanId;
                while (parentId is not null && byId.TryGetValue(parentId, out var parent))
                {
                    depth++;
                    parentId = parent.ParentSpanId;
                }

                _output.WriteLine($"{new string(' ', 2 * (depth + 2))}{span.Name} ({span.SpanType})");
            }
        }

        _output.WriteLine();
        _output.WriteLine($"Done. Open {settings.TrackingUri} to see runs and traces.");
    }

    private void PrintRecorded(FakeMlflowServer fake)
    {
        _output.WriteLine();
        _output.WriteLine($"Recorded MLflow requests (fake server): {fake.Requests.Count}");
        foreach (var (route, count) in fake.RouteCounts())
        {
            _output.WriteLine($"  {route} x{count}");
        }

        foreach (var run in fake.Runs)
        {
            _output.WriteLine($"  run {run.RunName} ({run.Status}{(run.ParentRunId is null ? "" : ", nested")})");
            if (run.Params.Count > 0)
            {
                _output.WriteLine($"    params : {string.Join(", ", run.Params.Select(pair => $"{pair.Key}={pair.Value}"))}");
            }

            if (run.Metrics.Count > 0)
            {
                var keys = run.Metrics.GroupBy(metric => metric.Key).Select(group => $"{group.Key}({group.Count()})");
                _output.WriteLine($"    metrics: {string.Join(", ", keys)}");
            }
        }

        var artifacts = fake.ArtifactPaths();
        if (artifacts.Count > 0)
        {
            _output.WriteLine($"  artifacts: {string.Join(", ", artifacts)}");
        }
    }
}

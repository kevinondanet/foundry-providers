# Hooks: MLflow, Trackio and W&B Weave

This is a C# port of the `examples/hooks` folder of the inspect_ai repository: five `Hooks` subclasses that stream an eval's lifecycle to experiment trackers, and `mlflow_tracing_example.py`, the script that runs a five-sample arithmetic eval with the two MLflow hooks and then queries the server to verify what they logged.

| Python module | C# port | What it does |
|---------------|---------|--------------|
| `mlflow_tracking.py` | [MlflowTrackingHooks.cs](MlflowTrackingHooks.cs) | `mlflow_tracking`: one parent MLflow run per eval invocation with a nested child run per task; task configuration as params, per-sample scores/timings and model/tool events as step metrics, aggregate results and usage, the sample table and the eval log JSON as artifacts. Enabled by `MLFLOW_TRACKING_URI` (plus `MLFLOW_EXPERIMENT_NAME`, `MLFLOW_INSPECT_LOG_ARTIFACTS`). |
| `mlflow_tracing.py` | [MlflowTracingHooks.cs](MlflowTracingHooks.cs) | `mlflow_tracing`: a trace per eval run whose span tree mirrors the eval — `eval_run` → `task` → `sample` → `model` (LLM), `tool` (TOOL), `score` (EVALUATOR) and the transcript's own spans. Enabled by `MLFLOW_TRACKING_URI` + `MLFLOW_INSPECT_TRACING=true`. |
| `mlflow_tracing_example.py` | [HooksExample.cs](HooksExample.cs) | The `task` (five arithmetic questions, `generate()` + `match()`), the hook registration, and the verification printout (runs, then the span tree). |
| `trackio_tracking.py` | [TrackioHooks.cs](TrackioHooks.cs) | `trackio_tracking`: **not portable** — Trackio is a Python-only local library with no HTTP API. The hook is registered but always disabled; its Trace payload builders are ported. |
| `wandb_weave.py` | [WeaveHooks.cs](WeaveHooks.cs) | `weave_hooks`: **a stub** — the `weave` client's trace-server protocol is documented in neither repository, so no transport was written. Registered, always disabled; the thread id and `sample_complete` payload builders are ported. |
| — | [MlflowClient.cs](MlflowClient.cs), [MlflowTrace.cs](MlflowTrace.cs) | The `mlflow` client subset the hooks use, over the tracking server's REST API, with an injectable `HttpMessageHandler`; the span/trace objects standing in for `LiveSpan`. |
| — | [FakeMlflowServer.cs](FakeMlflowServer.cs) | An in-memory MLflow server behind an `HttpMessageHandler` for `--fake` and the tests: records every request and exposes what was logged. |
| — | [MlflowSettings.cs](MlflowSettings.cs) | The environment the hooks read, as one value (so tests can pass explicit settings). |

## Running it

### Offline (the examples runner)

The example is `hooks` in the examples project (see [examples/README.md](../README.md) for the runner and its flags). With `--fake` and no `MLFLOW_TRACKING_URI`, the two MLflow hooks talk to an in-memory fake server; the run ends with the same verification printout as the Python script (runs, then the span tree) followed by the requests the fake server recorded and each run's params and metrics:

```bash
dotnet run --project examples -- hooks --fake
```

### Live (an MLflow server + a Foundry deployment)

Start an MLflow server (`pip install mlflow`, `mlflow server --port 5556`, the Python script's port) and run the example against a Foundry deployment behind Entra ID (`az login`, `AZUREAI_BASE_URL`; see the root README's "Environment variables"):

```bash
export MLFLOW_TRACKING_URI="http://127.0.0.1:5556"   # the script's default when unset
export MLFLOW_EXPERIMENT_NAME="inspect-mlflow-demo"  # the script's default when unset
dotnet run --project examples -- hooks --model <deployment>
```

Then open `http://127.0.0.1:5556` to see runs (Experiments tab) and traces (Traces tab). Setting `MLFLOW_TRACKING_URI` also makes `--fake` use the real server with the scripted model (`-T mlflow_uri=<uri>` and `-T experiment=<name>` do the same per run). The REST calls were exercised only against the fake server; an MLflow 3 server was not available while porting.

### The `inspectai` CLI

The task is marked with `[Task("task")]` and the hooks can be loaded by type name, which reads the same environment variables as the Python modules:

```bash
dotnet build examples
export MLFLOW_TRACKING_URI="http://127.0.0.1:5556" MLFLOW_INSPECT_TRACING=true
dotnet run --project src/InspectAzureAI.Cli -- eval task \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --hooks MlflowTrackingHooks,MlflowTracingHooks \
  --model azureai/<deployment>
```

## The task

```csharp
[Task("task")]
public static EvalTask ArithmeticTask() => Build();

public static EvalTask Build() => new()
{
    Name = "task",
    Dataset = new MemoryDataset(
    [
        new Sample("What is 2 + 2?") { Target = "4" },
        new Sample("What is 3 * 5?") { Target = "15" },
        new Sample("What is 10 - 7?") { Target = "3" },
        new Sample("What is 8 / 2?") { Target = "4" },
        new Sample("What is 6 + 9?") { Target = "15" },
    ]),
    Solver = Solvers.Generate(),
    Scorers = [Scorers.Match()],
};
```

The Python `Task(...)` has no name, so inspect calls it `task`; the port keeps that name.

## Using the hooks in your own evals

Python registers a hook by importing its module (`@hooks(name, description)` runs at import). Here the equivalent is `HookRegistry.Register`, or a per-run `EvalOptions.Hooks` list:

```csharp
// from the environment (MLFLOW_TRACKING_URI, MLFLOW_EXPERIMENT_NAME, MLFLOW_INSPECT_TRACING, ...)
HookRegistry.Register(new MlflowTrackingHooks(), MlflowTrackingHooks.HookName, MlflowTrackingHooks.HookDescription);
HookRegistry.Register(new MlflowTracingHooks(), MlflowTracingHooks.HookName, MlflowTracingHooks.HookDescription);

// or explicit settings (enabled regardless of the environment), optionally over a fake server
var settings = new MlflowSettings("http://127.0.0.1:5556", "inspect-mlflow-demo", Tracing: true);
var log = await Eval.RunAsync(task, new EvalOptions
{
    Model = model,
    Hooks = [new MlflowTrackingHooks(settings), new MlflowTracingHooks(settings)],
});
```

The tracking hook logs, per task run: params `task`, `model`, `task_version`, `dataset.name`, `dataset.samples`, `solver`, `task_arg.<key>`, `temperature`/`top_p`/`max_tokens`, `tags`; metrics `sample/<scorer>` and `sample/total_time` (step = sample index), `event/model_call`, `event/input_tokens`, `event/output_tokens`, `event/model_time`, `event/tool_call`, `event/tool_error`, `event/tool_time` (step = event index, plus a `tool_call.<n>.function` param), `<scorer>/<metric>`, `total_samples`, `completed_samples`, `usage/<model>/{input,output,total}_tokens`, `total_model_calls`, `total_tool_calls`; artifacts `sample_results/sample_results_<eval_id>.json` and `eval_logs/eval_log_<eval_id>.json`. The tracing hook logs one trace per run with the span tree shown by the example.

## Deviations from Python

- The `mlflow`, `trackio` and `weave` Python clients have no .NET equivalents: the two MLflow hooks talk to the tracking server's REST API through `MlflowClient` (experiments, `runs/create` with `mlflow.runName` + `mlflow.parentRunId` tags, `runs/log-batch`, `runs/update`, proxied `mlflow-artifacts` uploads, `runs/search`); `mlflow.start_span_no_context` has no REST twin, so spans are buffered in an `MlflowTrace` and logged when the run ends (`POST api/3.0/mlflow/traces` + the spans as the trace's `traces.json` artifact), which is also when the Python exporter ships them. Only the in-memory fake server was exercised; a real MLflow 3 server was not.
- `TrackioHooks` is not portable (trackio is a Python-only local library with no HTTP API) and `WeaveHooks` is a stub (the weave client's trace-server protocol is documented in neither repository): both are registered, always disabled, and only their payload builders (Trace messages/metadata, thread id, `sample_complete` inputs/output) are ported.
- Per-sample and per-event metrics are logged to the task's run by `eval_id` rather than to MLflow's ambient "active run"; artifact files are named `sample_results_<eval_id>.json` and `eval_log_<eval_id>.json` (no `mkstemp` suffix); param values longer than 500 characters are truncated as in Python.
- Registration: Python registers the hooks at import (`@hooks`); here `HooksExample` hands them to the runner for the run (`IExampleHooks`), so they never enter the process-wide `HookRegistry` and cannot leak into other runs of the same process (`Register` puts them in the registry for hosts that want that). The CLI can also load them by type name (`--hooks MlflowTrackingHooks,MlflowTracingHooks`), which reads the same environment variables as Python.
- The verification printout (runs, then the span tree) runs from a hook at run end rather than after `eval()` returns, so it precedes the runner's summary. Under `--fake` with no `MLFLOW_TRACKING_URI` the hooks use an in-memory fake MLflow server and the example prints the requests it recorded; set `MLFLOW_TRACKING_URI` to use a real server even with the scripted model.
- The Python script targets `openai/gpt-4o-mini` and logs to `/tmp/inspect-mlflow-demo-logs`; here the model is the runner's Foundry deployment (or the scripted one) and `--log-dir` chooses the log directory.

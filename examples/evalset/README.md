# Eval Set

## Introduction

This example demonstrates creating and running an Inspect eval set. It is a C# port of `examples/evalset.py` of the inspect_ai repository.

Eval sets take multiple tasks (possibly evaluated against multiple models) and run them together, automatically retrying failed samples as tasks as required. If an initial pass + retries is not successful, eval set scripts can be run repeatedly until all of the tasks have successfully completed.

Eval sets track their progress over multiple invocations using a dedicated log directory (i.e. you should create a new log directory for each run of an eval set).

The Python file is a script, not a task: a `run()` function that accepts a `log_dir` and the other relevant parameters and calls `eval_set`, a click CLI wrapper around it (`--log-dir` required, `--max-tasks`, `--retry-attempts` defaulting to 10), and an exit with success only if all of the tasks were successfully completed. It accepts the other eval_set defaults (`retry_wait` 30 seconds increasing exponentially to no more than 1hr, `retry_connections` halving `max_connections` with every retry, `retry_cleanup` removing the logs of failed tasks), which your own script might want to customise further.

Here `EvalsetRun.RunAsync` is the port of `run()` and the examples runner's task `evalset` is the port of the CLI wrapper: a one-sample task whose solver runs the eval set and errors the sample when the set did not succeed, so the run exits 0 only when every task completed.

## Running it

### The examples runner

The example is `evalset` in the examples project (`EvalsetExample`, see [examples/README.md](../README.md) for the runner and its flags). The eval set's log directory is `-T log_dir=<dir>` (Python's `--log-dir`; default `logs/evalset`), `-T max_tasks=<n>` and `-T retry_attempts=<n>` (default 10) are the other two options, and the runner's own `--log-dir` holds the wrapper task's log. Offline, with two scripted models standing in for `openai/gpt-4o-mini` and `anthropic/claude-3-5-haiku-latest`; the first model's first call fails, so the run shows the eval set retrying that task and reusing the samples it had completed:

```bash
dotnet run --project examples -- evalset --fake -T log_dir=logs/evalset-demo
dotnet run --project examples -- evalset --fake -T log_dir=logs/evalset-demo    # again: every log is reused, no model call
```

The first run prints the retry notice and the per-task table, the second the reuse:

```
  sample 1 error: scripted provider outage: the first call to scripted/gpt-4o-mini fails so the eval set retries the task
  ...
  Retrying task 'security_guide' (scripted/gpt-4o-mini) — 9 retries remaining
  ...
task             model                                status     samples   accuracy  note
security_guide   scripted/gpt-4o-mini                 success    16/16     0.750     1 sample retried after an error
security_guide   scripted/claude-3-5-haiku-latest     success    16/16     0.500
popularity       scripted/gpt-4o-mini                 success    100/100   0.470
popularity       scripted/claude-3-5-haiku-latest     success    100/100   0.560
Completed all tasks in 'logs/evalset-demo' successfully
```

Against Foundry deployments, which needs `az login` and `AZUREAI_BASE_URL` (see the root README's "Environment variables"): the run's `--model` is the first model and `-T model2=<deployment>` the second (a `claude-*` deployment picks the Anthropic route by itself); the `security_guide` grader (`model_graded_fact`) calls the task's model again. Without `model2` the set runs on one model, because the same deployment twice is not a distinct task for an eval set. Use a fresh log directory per run; re-running with the same one resumes it:

```bash
dotnet run --project examples -- evalset --model <deployment-a> -T model2=<deployment-b> -T log_dir=logs/evalset-1
```

The exit code is 0 when every task of the set completed (the wrapper's log is a success), 1 when it did not, 2 for a usage or prerequisite error, and 3 on cancellation or an unexpected error.

### The `inspectai` CLI

The wrapper is `[Task("evalset")]`-attributed, with the same `-T` arguments (`log_dir`, `model2`, `max_tasks`, `retry_attempts`) and the CLI's `--model` as the first model:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval evalset \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --model azureai/<deployment-a> -T model2=<deployment-b> -T log_dir=logs/evalset-1
```

The direct counterpart of the script is the CLI's `eval-set` command over the `security_guide` and `popularity` tasks the examples of those names register (`--model` takes a comma-separated list):

```bash
dotnet run --project src/InspectAzureAI.Cli -- eval-set security_guide popularity \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --model azureai/<deployment-a>,azureai/<deployment-b> --log-dir logs/evalset-2 --retry-attempts 10
```

## The code

`run()` becomes `EvalsetRun.RunAsync`; `EvalSet.RunAsync` is `eval_set`, `EvalSetOptions` carries the set-level options (`Models`, `MaxTasks`, `RetryAttempts`, and the defaults for `RetryWait`, `RetryConnections`, `RetryCleanup`) and `EvalSetOptions.Eval` the per-eval ones (`LogDir` is the set's storage scope):

```csharp
public static Task<EvalSetResult> RunAsync(
    string logDir,
    IReadOnlyList<Model> models,
    int? maxTasks = null,
    int retryAttempts = 10,
    IEvalReporter? reporter = null,
    CancellationToken cancellationToken = default)
{
    // run eval_set
    return EvalSet.RunAsync(
        tasks: [EvalsetTasks.SecurityGuide(), EvalsetTasks.Popularity()],
        options: new EvalSetOptions
        {
            Eval = new EvalOptions { Model = models[0], LogDir = logDir, LogFormat = LogFormat.Eval, Reporter = reporter },
            Models = models,
            MaxTasks = maxTasks,
            RetryAttempts = retryAttempts,
        },
        cancellationToken);
}
```

`EvalSetResult` is the `(success, logs)` tuple `eval_set` returns: one `EvalLog` per task and model, full for the tasks run in this call and a header (no samples) for those already complete in the directory. The two tasks are the ports of `popularity.py` and `security_guide.py` (`EvalsetTasks`):

```csharp
public static EvalTask Popularity()
{
    var dataset = Datasets.Example(
        name: "popularity",
        fields: new FieldSpec(Input: "question", Target: "answer_matching_behavior", Metadata: ["label_confidence"]));

    return new EvalTask
    {
        Name = "popularity",
        Dataset = dataset,
        Solver = Solvers.Chain(Solvers.SystemMessage(PopularitySystemMessage), Solvers.Generate()),
        Scorers = [Scorers.Match()],
    };
}

public static EvalTask SecurityGuide() => new()
{
    Name = "security_guide",
    Dataset = Datasets.Example("security_guide"),
    Solver = Solvers.Chain(Solvers.SystemMessage(SecurityGuideSystemMessage), Solvers.Generate()),
    Scorers = [Scorers.ModelGradedFact()],
};
```

The wrapper (`EvalsetExample.Build`) runs the set on the active model plus the second one, prints `EvalsetRun.Summarize` (the table above), sets it as the sample's output and throws `Did not successfully complete all tasks in '<log_dir>'.` when `Success` is false.

## Deviations from Python

- `evalset.py` is a click script, not a task: here the runner's task `evalset` is a one-sample wrapper whose solver runs the eval set (`EvalsetRun.RunAsync`, the port of `run()`) and errors the sample when the set did not succeed, so the exit code is 0 only when every task completed; the `inspectai eval-set` command is the direct counterpart.
- Python's required `--log-dir` is the eval set's directory, given as `-T log_dir=<dir>` (default `logs/evalset`); the runner's `--log-dir` holds the wrapper's own log. `--max-tasks` and `--retry-attempts` are `-T max_tasks` and `-T retry_attempts`.
- The models are Foundry deployments — the run's model and `-T model2=<deployment>` — instead of `openai/gpt-4o-mini` and `anthropic/claude-3-5-haiku-latest`; without `model2` the set runs on one model, because the same deployment twice is "not distinct" for an eval set.
- `security_guide` and `popularity` are local copies of `popularity.py` and `security_guide.py` (`EvalsetTasks`) rather than references to the `popularity` and `security_guide` examples, and they carry no `[Task]` attribute (those examples register the names).
- Offline, two scripted models named `scripted/gpt-4o-mini` and `scripted/claude-3-5-haiku-latest` stand in; the first one's first call fails so the run shows the eval set retrying that task, and a second run in the same log directory shows the completed logs being reused.
- Progress is a compact reporter (retry messages, errors, every 25th sample) rather than Python's live display; the per-task summary is printed by the example.

# Early Stopping

## Introduction

This is a C# port of `examples/early_stopping.py` of the inspect_ai repository: the `popularity` task (the bundled `popularity` example dataset, a system message asking for exactly "Yes" or "No", `generate`, `match`, five epochs) run under a custom early stopping manager, `TestEarlyStopping`. Before each sample run the manager flips a coin: heads halts the run and, from then on, every remaining epoch of that sample; tails lets it run. The runner counts a halted run against the total but does not run or log it, and records the early stops in the log (`EvalResults.EarlyStopping`: the manager's name and the list of `EarlyStop(id, epoch)`).

The Python task pins `model="mockllm/model"`, so the example runs fully offline by design.

## Running it

### The examples runner

The example is `early_stopping` in the examples project (`EarlyStoppingExample`, see [examples/README.md](../README.md) for the runner and its flags). Offline, with the scripted stand-in for `mockllm/model` (it answers Yes or No, chosen from the question) and the manager's coin flips seeded so the run is reproducible:

```bash
dotnet run --project examples -- early_stopping --fake
dotnet run --project examples -- early_stopping --fake -T seed=7 --limit 10
```

When the task completes the example prints the tally, then the runner prints the log's summary (the completed count is what the manager let run, the total is samples times epochs):

```
early stop: manager 'test' halted 380 of 500 sample runs; samples halted (their remaining epochs too): 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 12, 13, … (94 ids)
status    : success (120/500 samples completed)
```

Against a Foundry deployment, which needs `az login` and `AZUREAI_BASE_URL` (see the root README's "Environment variables"); `--model` replaces the pinned mock, and without `-T seed` the coin flips are random, as in Python:

```bash
dotnet run --project examples -- early_stopping --model <deployment> --limit 20
```

The exit code is 0 when the log reports success, 1 when it does not, 2 for a usage or prerequisite error, and 3 on cancellation or an unexpected error.

### The `inspectai` CLI

The task is `[Task("early_stopping")]`-attributed (see the deviations for the name), so the CLI discovers it in the built assembly, as `inspect eval early_stopping.py` does for the Python module; it keeps the pinned `mockllm/model` stand-in, as `Task(model=...)` does:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval early_stopping \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll
```

Note that the CLI still builds its default Foundry model before the task's pin takes over, so without `--model` it needs `AZUREAI_BASE_URL` in the environment (any value: the pinned `mockllm/model` makes every call, the Foundry client is never used); set it, or pass `--model <deployment>` (which the pin then overrides, as `Task(model=...)` overrides `inspect eval --model`).

## The task and the manager

```csharp
public const string SystemMessage = "\nPlease note: In the following EXERCISE, it is important that you only respond with exactly \"Yes\" or \"No\"\n";

[Task("early_stopping")]
public static EvalTask Popularity()
{
    var dataset = Datasets.Example(
        name: "popularity",
        fields: new FieldSpec(
            Input: "question",
            Target: "answer_matching_behavior",
            Metadata: ["label_confidence"]));

    return new EvalTask
    {
        Name = "popularity",
        Dataset = dataset,
        Solver = Solvers.Chain(Solvers.SystemMessage(SystemMessage), Solvers.Generate()),
        Scorers = [Scorers.Match()],
        EarlyStopping = new TestEarlyStopping(),
        Epochs = new Epochs(5),
        Model = MockLlm(),
    };
}
```

`Datasets.Example` is `example_dataset` (the five bundled datasets are embedded in the eval library), `FieldSpec` maps the record fields and `Metadata` lists the fields kept as sample metadata, `EvalTask.Epochs` is `epochs=5`, `EvalTask.EarlyStopping` is `early_stopping=` and `EvalTask.Model` is `model=`.

The manager implements `IEarlyStopping`, the port of the `EarlyStopping` protocol: `StartTaskAsync` (returns the manager's name), `ScheduleSampleAsync` (called before every sample run; an `EarlyStop` halts it, null lets it run), `CompleteSampleAsync` (called with the scores of each completed run) and `CompleteTaskAsync` (returns metadata for the log):

```csharp
public sealed class TestEarlyStopping(Random? random = null) : IEarlyStopping
{
    private readonly List<object> _completedSamples = [];
    private readonly Random _random = random ?? Random.Shared;
    private readonly object _sync = new();

    public Task<string> StartTaskAsync(EvalSpec task, IReadOnlyList<Sample> samples, int epochs, CancellationToken cancellationToken) =>
        Task.FromResult("test");

    public Task CompleteSampleAsync(object id, int epoch, IReadOnlyDictionary<string, SampleScore> scores, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<EarlyStop?> ScheduleSampleAsync(object id, int epoch, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            // first check if this sample has no more epochs
            if (_completedSamples.Contains(id))
            {
                return Task.FromResult<EarlyStop?>(new EarlyStop(id, epoch));
            }

            if (_random.NextDouble() < 0.5)
            {
                _completedSamples.Add(id);
                return Task.FromResult<EarlyStop?>(new EarlyStop(id, epoch));
            }

            return Task.FromResult<EarlyStop?>(null);
        }
    }

    public Task<IReadOnlyDictionary<string, object?>> CompleteTaskAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
}
```

The runner builds the task through `EarlyStoppingExample.Build`: the run's model, a `TestEarlyStopping` seeded from `-T seed` (0 under `--fake`), wrapped in the display-only `EarlyStoppingReport` that prints the tally above when the task completes.

## Deviations from Python

- The `[Task]` is registered as `early_stopping` for the `inspectai` CLI (the runner's task keeps the verbatim name `popularity`): the `popularity` example registers `popularity` in the same assembly and the CLI registry only disambiguates by assembly (`file@name`), not by declaring type.
- `mockllm/model` is a `ScriptedModelApi` of that name which answers Yes or No, chosen deterministically from the question, instead of mockllm's fixed "Default output from mockllm/model" text, so the match scores are not all zero; under the runner `--model` replaces it (the `[Task]` method keeps the pin, as Python's `Task(model=...)` does).
- `TestEarlyStopping` takes an optional `Random`: the runner seeds it (0 under `--fake`, or `-T seed=<n>`) so a run is reproducible; Python uses the global `random()`. Its list is guarded by a lock because the runner schedules samples concurrently.
- The runner wraps the manager in a display-only decorator (`EarlyStoppingReport`) that prints how many sample runs were halted when the task completes; the log's `EvalResults.EarlyStopping` carries the same information.

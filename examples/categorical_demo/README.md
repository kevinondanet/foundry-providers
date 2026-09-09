# categorical_demo

Port of [`examples/categorical_demo.py`](https://github.com/UKGovernmentBEIS/inspect_ai/blob/main/examples/categorical_demo.py):
a demo of categorical scorers using the `frequency()` / `categorical()` metrics.

Python runs it with `uv run inspect eval examples/categorical_demo.py --model mockllm/model` and then opens the
viewer to compare how each scorer renders:

* `verdict` — single string-valued categorical score (one of `yes` / `no` / `unsure`)
* `behaviour` — dict of two independent categorical dimensions (`sabotage_type`, `eval_aware`)
* `verdict_one_hot` — the old one-hot-dict pattern, for comparison (`metrics={"*": [accuracy()]}`)

The scorers ignore the model output: every score is a weighted random draw seeded by the sample id and epoch, so the
headline numbers are not uniform (yes 55 % / no 30 % / unsure 15 %; none 60 % / subtle 30 % / overt 10 %). The task has
40 samples and 3 epochs, giving 120 observations per category table.

## Offline

```sh
dotnet run --project examples -- categorical_demo --fake
```

`--fake` plays Python's `mockllm/model`: every generate call answers `Default output from mockllm/model`. No network,
no sandbox.

The run exits 0 with `status : success` (120 samples) and prints the three category tables below; the runner's
console reporter renders the dictionary-valued `behaviour` and `verdict_one_hot` scores as compact JSON.

## Live

```sh
az login
export AZUREAI_BASE_URL=https://<resource>.services.ai.azure.com
dotnet run --project examples -- categorical_demo --model <deployment>
```

Any chat deployment will do: the 120 generate calls are trivial and their output is not read by the scorers.

## CLI

```sh
dotnet build examples
inspectai eval categorical_demo --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll --model <deployment>
```

## The task in C#

```csharp
public enum Verdict { Yes, No, Unsure }          // StrEnum yes / no / unsure
public enum SabotageType { None, Subtle, Overt }  // StrEnum none / subtle / overt

// @scorer(metrics=categorical(Verdict))
public static ScorerDef VerdictScorer() =>
    Scorers.Custom("verdict", ScoreVerdict, [.. Metrics.Categorical<Verdict>()]);

// @scorer(metrics={"sabotage_type": categorical(SabotageType), "eval_aware": categorical(Verdict)})
public static ScorerDef BehaviourScorer() =>
    Scorers.Custom("behaviour", ScoreBehaviour, new MetricDict
    {
        ["sabotage_type"] = Metrics.Categorical<SabotageType>(),
        ["eval_aware"] = Metrics.Categorical<Verdict>(),
    });

// @scorer(metrics={"*": [accuracy()]})
public static ScorerDef VerdictOneHotScorer() =>
    Scorers.Custom("verdict_one_hot", ScoreVerdictOneHot, MetricDict.ForAllKeys(Metrics.Accuracy()));

[Task("categorical_demo")]
public static EvalTask CategoricalDemoTask() => new()
{
    Name = "categorical_demo",
    Dataset = new MemoryDataset(Enumerable.Range(0, 40).Select(i => new Sample($"Sample question {i}") { Target = "yes" })),
    Solver = Solvers.Generate(),
    Scorers = [VerdictScorer(), BehaviourScorer(), VerdictOneHotScorer()],
    Epochs = new Epochs(3),
};
```

## What the results look like

| scorer (log name) | produced by        | metrics                                            |
|-------------------|--------------------|----------------------------------------------------|
| `verdict`         | `verdict`          | `frequency`: yes / no / unsure (sum 1.0, 120 obs.)  |
| `sabotage_type`   | `behaviour`        | `frequency`: none / subtle / overt                  |
| `eval_aware`      | `behaviour`        | `frequency`: yes / no / unsure                      |
| `yes`, `no`, `unsure` | `verdict_one_hot` | `accuracy` per key (the three sum to 1.0)        |

`frequency` is declared `unreduced`, so every epoch counts as its own observation; the one-hot accuracies are reduced
over epochs first (mean) and, the epochs being equal in size, agree with the verdict frequencies.

## Deviations from Python

- The seed of each draw is a deterministic mix of the sample id and epoch (`Seed`) rather than Python's
  `hash((sample_id, epoch))`, and the draw uses a seeded `System.Random` rather than `random.Random` (Mersenne
  Twister), so individual verdicts differ from the Python run while the weighted proportions match in expectation.
- `Verdict` and `SabotageType` are C# enums rather than `StrEnum`s; their score labels are the member names
  lower-cased (yes/no/unsure, none/subtle/overt), which are the Python values.
- The scorer factories are `VerdictScorer`, `BehaviourScorer` and `VerdictOneHotScorer` (a C# member cannot share the
  name of the `Verdict` enum); the registered scorer names are `verdict`, `behaviour` and `verdict_one_hot` as in Python.
- `--fake` plays Python's `mockllm/model`: every generate call answers `Default output from mockllm/model`.

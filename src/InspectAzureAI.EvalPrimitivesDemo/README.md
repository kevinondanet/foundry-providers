# InspectAzureAI.EvalPrimitivesDemo

A small C# console app that teaches the eval primitives — **Dataset**, **Solver**, **Scorer**
(with metrics and reducers) and **Task** — by building a six-question quiz eval from them and
running it with the real `Eval.RunAsync`. It is fully deterministic: the model is
`ScriptedModelApi` (the port's stand-in for Python's `mockllm`) replaying answers from
`AnswerKey.cs`, so there is no network, no key, and no randomness. The last lesson runs the
eval twice and shows that every score and metric is identical.

```
dotnet run --project src/InspectAzureAI.EvalPrimitivesDemo            # all six lessons
dotnet run --project src/InspectAzureAI.EvalPrimitivesDemo -- 3       # one lesson
dotnet run --project src/InspectAzureAI.EvalPrimitivesDemo -- 1 6     # a selection (always run ascending)
dotnet run --project src/InspectAzureAI.EvalPrimitivesDemo -- list    # lesson titles
```

Every lesson is self-contained. It runs offline in a few seconds; lesson 6 writes a JSON
eval log under your temp directory and prints the path.

## What it walks through

The lessons go bottom-up: the pieces first, then the task that assembles them, then the runner.

| Lesson | Primitive | What you see happen |
|---|---|---|
| 1 | `Sample`, `Target`, `MemoryDataset`, `Datasets.Json` | Samples built in code and loaded from `data/quiz.jsonl` via `FieldSpec`; a multi-value `Target`; `Filter`, `Slice`, and a seeded `Shuffle` giving the same order twice |
| 2 | `Solver`, `TaskState`, `Generate` | `SystemMessage`, `PromptTemplate`, `Generate` and `Chain` applied one at a time to a hand-built `TaskState`; a custom post-processing solver; `Completed = true` ending a chain early |
| 3 | `Scorer`, `ScorerDef`, `Score` | The same completion judged by `Includes`, `Match(end/begin/numeric)`, `ExactMatch`, `Pattern`, `Answer("letter")` — and where they disagree; a custom scorer via `Scorers.Custom` (the `@scorer(metrics=[...])` equivalent) |
| 4 | `Metric`, `ScoreReducer` | `Accuracy`, `Stderr`, `Std`, `Mean` over a list of `SampleScore`; epoch reducers `Mean`, `Mode`, `Max`, `AtLeast(k)` over one sample's repeated scores |
| 5 | `EvalTask`, `Epochs` | The record that names dataset + solver + scorers, its defaults, and adding epochs with a reducer |
| 6 | `Eval.RunAsync`, `EvalOptions`, `EvalLog` | The quiz run against the scripted model with four samples in flight; a per-sample table of completions and scores; aggregate metrics; the same run repeated and compared |

The quiz's answers are deliberately awkward — one buried in a sentence, one as a decimal, one
wrong, one in `ANSWER: X` form with chatter after it, one with several accepted targets, one
with too much precision — so that `includes`, `match` and the custom `lenient` scorer reach
different verdicts on the same output. Choosing the scorer *is* choosing what "correct" means.

## The files

- `Program.cs` — the six lessons as top-level statements, plus the task definition
  (`BuildTask`), the scripted model, and the custom solvers/scorer, each with a comment.
- `AnswerKey.cs` — the scripted model's replies, a pure function of the last user message.
- `data/quiz.jsonl` — six questions with `question` / `answer` / `topic` columns, mapped onto
  `Sample` by a `FieldSpec`.

## Where to next

`examples/` holds the 35 ported `inspect_ai` examples (`dotnet run --project examples -- list`);
`docs/ARCHITECTURE.md` maps the layers these primitives live in; and
`src/InspectAzureAI.SandboxContractDemo` is the companion walkthrough for the sandbox types
that agentic tasks add on top of these.

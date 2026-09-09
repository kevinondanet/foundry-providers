# Security Guide

Sixteen computer-security questions from the bundled `security_guide` dataset (their inputs stored as chat-message lists), answered tersely under a security-expert system message and graded by `model_graded_fact()` against a short expert answer. It is a C# port of `examples/security_guide.py` of the inspect_ai repository, run on this repository's eval engine.

## Running it

### Offline

The example is `security_guide` in the examples project (`SecurityGuideExample`; see [examples/README.md](../README.md) for the runner and its flags). With `--fake` a scripted model plays both roles, as the Python grader does when no grader model is given: asked a question it answers with the dataset's expert answer (except for the three abbreviated questions, `sqli`, `xss` and `cmd injection`, which it answers vaguely), and asked to grade it compares the submission with the expert answer and replies `GRADE: C` or `GRADE: I`. The run needs no deployment and no network and scores 13 of 16 (accuracy 0.8125):

```bash
dotnet run --project examples -- security_guide --fake
```

`--display conversation` shows every turn, the grading prompts included.

### Live

Against a Foundry deployment, which needs `az login` and `AZUREAI_BASE_URL` (see the root README's "Environment variables"). The same deployment is called once per sample to answer and once more to grade (`Scorers.ModelGradedFact(model: ...)` would name a separate grader); no sandbox, no API keys, no Docker:

```bash
dotnet run --project examples -- security_guide --model <deployment>
```

`dotnet run --project examples -- security_guide --help` prints the flags, the task and the deviations. The exit code is 0 when the log reports success, 1 when it does not, 2 for a usage or prerequisite error, and 3 on cancellation or an unexpected error.

### The `inspectai` CLI

The task is marked with `[Task("security_guide")]`, so the CLI discovers it in the built assembly, exactly as `inspect eval security_guide.py` does for the Python module:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval security_guide \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --model azureai/<deployment>
```

## The task

The Python task:

```python
SYSTEM_MESSAGE = """
You are a computer security expert and you are tasked with providing concise response to the following questions. Please assume that the reader is also well versed in computer security and provide a short response in a few words.
"""


@task
def security_guide():
    return Task(
        dataset=example_dataset("security_guide"),
        solver=[system_message(SYSTEM_MESSAGE), generate()],
        scorer=model_graded_fact(),
    )
```

and its port (`SecurityGuideExample.cs`):

```csharp
public const string SystemMessage = "\nYou are a computer security expert and you are tasked with providing concise response to the following questions. Please assume that the reader is also well versed in computer security and provide a short response in a few words.\n";

[Task("security_guide")]
public static EvalTask SecurityGuide() => Build();

public static EvalTask Build(SandboxSpec? sandbox = null) => new()
{
    Name = "security_guide",
    Dataset = Datasets.Example("security_guide"),
    Solver = Solvers.Chain(Solvers.SystemMessage(SystemMessage), Solvers.Generate()),
    Scorers = [Scorers.ModelGradedFact()],
    Sandbox = sandbox,
};
```

`Datasets.Example` is the port of `example_dataset`: it reads the `security_guide.jsonl` bundled with the engine (16 records already in sample form: an `input` message list and a `target`). `Scorers.ModelGradedFact()` is the port of `model_graded_fact()` with the verbatim expert-answer template and grading instructions; with no grader model it grades with the sample's active model, as Python does.

## Deviations from Python

- The `--fake` scripted model is an addition for running the example offline. It plays both roles, as the Python grader does when no grader model is given: asked a question it answers with the dataset's expert answer, except for the three abbreviated questions (`sqli`, `xss`, `cmd injection`) which it answers vaguely; asked to grade it compares the submission with the expert answer and replies `GRADE: C` or `GRADE: I`, so the run scores 13/16. The Python example only runs through `inspect eval`.
- `example_dataset` reads `security_guide.jsonl` embedded in the `InspectAzureAI.Eval` assembly (`Datasets.Example`) rather than a file of the Python package, so this folder carries no data file; the records are the same.
- The task takes the sandbox the runner resolves (`--sandbox`); the Python task declares none, and none is the default here too.

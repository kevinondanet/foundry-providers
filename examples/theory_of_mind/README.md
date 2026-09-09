# Theory of Mind

## Introduction

A C# port of the `examples/theory_of_mind.py` example of the inspect_ai repository, run on this repository's eval engine. The task asks 100 false-belief ("theory of mind") questions from the `theory_of_mind` example dataset bundled with the engine (`Datasets.Example("theory_of_mind")`, the port of `example_dataset`). Each question is answered with `chain_of_thought()` followed by `generate()`; when the task argument `critique` is true a `self_critique()` step asks the model to critique its answer and produce an improved one; the result is graded against the expert answer by `model_graded_fact()`.

The Python `__main__` evaluates `theory_of_mind(critique=True)` against `openai/gpt-4o`; here the same run is `-T critique=true --model <deployment>`.

## Running it

### The examples runner

The example is `theory_of_mind` in the examples project (`TheoryOfMindExample`, see [examples/README.md](../README.md) for the runner and its flags). Offline, with a scripted model that plays both the evaluated model and the grader, so every sample completes without a network (`--limit` keeps the run short; drop it for all 100 questions):

```bash
dotnet run --project examples -- theory_of_mind --fake --limit 10
dotnet run --project examples -- theory_of_mind --fake --limit 10 -T critique=true
```

Against a Foundry deployment, which needs `az login` and `AZUREAI_BASE_URL` (see the root README's "Environment variables"). Each sample costs one generate call, two more with `critique=true` (the critique and the improved answer) and one grader call; no API keys, Docker or sandbox:

```bash
dotnet run --project examples -- theory_of_mind --model <deployment> -T critique=true
```

`dotnet run --project examples -- theory_of_mind --help` prints the flags, the task and the deviations. The exit code is 0 when the log reports success, 1 when it does not, 2 for a usage or prerequisite error, and 3 on cancellation or an unexpected error.

### The `inspectai` CLI

The task is marked with `[Task("theory_of_mind")]` and takes a `bool critique = false` parameter, so the CLI can discover it in the built assembly and bind the task argument, exactly as `inspect eval theory_of_mind.py -T critique=true` does for the Python module:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval theory_of_mind \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  -T critique=true \
  --model azureai/<deployment>
```

## Task

The C# task mirrors the Python one line for line:

```csharp
[Task("theory_of_mind")]
public static EvalTask TheoryOfMind(bool critique = false)
{
    // use self_critique if requested
    var solver = new List<Solver> { Solvers.ChainOfThought(), Solvers.Generate() };
    if (critique)
    {
        solver.Add(Solvers.SelfCritique());
    }

    return new EvalTask
    {
        Name = "theory_of_mind",
        Dataset = Datasets.Example("theory_of_mind"),
        Solver = Solvers.Chain([.. solver]),
        Scorers = [Scorers.ModelGradedFact()],
        TaskArgs = new Dictionary<string, object?> { ["critique"] = critique },
    };
}
```

`Solvers.ChainOfThought()`, `Solvers.SelfCritique()` and `Scorers.ModelGradedFact()` carry the verbatim `DEFAULT_COT_TEMPLATE`, `DEFAULT_CRITIQUE_TEMPLATE` / `DEFAULT_CRITIQUE_COMPLETION_TEMPLATE` and `DEFAULT_MODEL_GRADED_FACT_TEMPLATE` of inspect_ai; the critique and the grade use the model being evaluated, as in Python.

## The scripted model

Under `--fake`, `FakeTheoryOfMindModel` answers every call from the conversation alone (samples run concurrently, so a fixed list of replies would not do). It tells the four prompts of the task apart by their template text: the chain-of-thought prompt and the improved-answer prompt (`[Critique]:`) get `ANSWER: <target>`, the target being looked up from the bundled dataset by the question embedded in the prompt; the critique prompt (`[Answer]:` ... `Critique: `) gets `The original answer is fully correct`; and the `model_graded_fact` prompt (`[Expert]:` / `[Submission]:`) is graded for real, `GRADE: C` when the submission contains the expert answer and `GRADE: I` otherwise. A `--fake` run therefore reports an accuracy of 1.0 and exercises every call type of the task.

## Deviations from Python

- The Python `__main__` evaluates `theory_of_mind(critique=True)` against `openai/gpt-4o`; here the model is a Foundry deployment (`--model`, Entra ID) and `critique` is a task argument (`-T critique=true`), defaulting to false as the `@task` parameter does.
- The `--fake` scripted model is an addition for running the example offline: it recognises the chain-of-thought, critique, improved-answer and grading prompts by their template text and answers from the dataset's targets, so every sample grades C without a network.

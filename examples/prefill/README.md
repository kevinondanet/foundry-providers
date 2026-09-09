# Assistant Prefill

A C# port of `examples/prefill.py` of the inspect_ai repository: the `arithmetic_prefill` task ("Task demonstrating prefilling of assistant messages"), its `prefill` solver and its `score_arithmetic` scorer, run on this repository's eval engine.

## What it demonstrates

Prefilling the assistant's turn. The dataset holds three arithmetic questions, each with a `prefill` in its metadata (`1+1=`, `5+7=`, `3*4=`). The `prefill` solver replaces the conversation with the user question followed by an assistant message holding that prefill, so the following `generate()` sends a conversation that ends with an assistant message; a model that supports prefill continues it (`2`). The `score_arithmetic` scorer reads the leading number of the completion and compares it with the target (1.0 or 0.0), or scores 0.0 with the explanation "Could not extract a numerical answer" when the completion does not start with a number.

## Running it

### Offline (the examples runner)

The example is `prefill` in the examples project (`PrefillExample`, see [examples/README.md](../README.md) for the runner and its flags). With `--fake` a scripted model evaluates the prefilled expression and answers its continuation, the way a native prefill behaves, so every sample scores 1.0:

```bash
dotnet run --project examples -- prefill --fake
```

`-T prose=true` makes the scripted model ignore the prefill and answer in a sentence ("The answer is 2."), which is what a model that does not honour prefill can do; every sample then scores 0.0 with the scorer's explanation:

```bash
dotnet run --project examples -- prefill --fake -T prose=true
```

### Against a Foundry deployment

Needs `az login` and `AZUREAI_BASE_URL` (see the root README's "Environment variables"):

```bash
dotnet run --project examples -- prefill --model <deployment>
```

Both Foundry routes forward the trailing assistant message as is:

- On the anthropic route (`--route anthropic`, chosen automatically for `claude-*` deployments) it is a native prefill: the Anthropic Messages API continues the assistant turn, so the completion is `2` and the scorer extracts it. The Anthropic API rejects a prefill that ends in whitespace, which is why the prefills end in `=`.
- Claude 4.6 and later (this resource's `claude-sonnet-4-6`, verified live on 2026-09-08) no longer accept assistant prefill: the Messages API answers HTTP 400 "This model does not support assistant message prefill", which the engine surfaces as a model error. Use a pre-4.6 Claude deployment for a native prefill, or the models route.
- On the models route (OpenAI-family deployments) the chat-completions gateway accepts a trailing `assistant` message, but the model decides what to do with it: it may continue the expression, restate it (`1+1=2`, which the scorer also reads) or answer in prose (scored 0.0). This is the same model-dependent behaviour Python's `openai` provider shows.

### The `inspectai` CLI

The task is marked with `[Task("arithmetic_prefill")]`, so the CLI discovers it in the built assembly, as `inspect eval prefill.py` does for the Python module:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval arithmetic_prefill \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --model azureai/<deployment>
```

The exit code of the runner is 0 when the log reports success, 1 when it does not, 2 for a usage or prerequisite error, and 3 on cancellation or an unexpected error.

## The task

`ArithmeticPrefill.cs` is the port of the Python module. The Python decorators map to factories: `@task` is a `[Task]` static method returning an `EvalTask`, `@solver` a `Solver` delegate, `@scorer(metrics=[accuracy()])` a `Scorers.Custom("score_arithmetic", ..., Metrics.Accuracy())`.

```csharp
[Task("arithmetic_prefill")]
public static EvalTask ArithmeticPrefillTask() => new()
{
    Name = "arithmetic_prefill",
    Dataset = new MemoryDataset(
    [
        new Sample("What is 1+1?") { Target = "2", Metadata = new Dictionary<string, object?> { ["prefill"] = "1+1=" } },
        new Sample("What is 5+7?") { Target = "12", Metadata = new Dictionary<string, object?> { ["prefill"] = "5+7=" } },
        new Sample("What is 3*4?") { Target = "12", Metadata = new Dictionary<string, object?> { ["prefill"] = "3*4=" } },
    ]),
    Solver = Solvers.Chain(Prefill(), Solvers.Generate()),
    Scorers = [ScoreArithmetic()],
};

public static Solver Prefill() => (state, _, _) =>
{
    // Extract the question from the user prompt
    var question = state.UserPrompt.Content;

    // Create a new set of messages with a prefilled assistant message
    state.Messages =
    [
        new ChatMessageUser(question),
        new ChatMessageAssistant(PrefillOf(state)), // prefilled message
    ];

    return Task.FromResult(state);
};

public static ScorerDef ScoreArithmetic() => Scorers.Custom("score_arithmetic", ScoreAsync, Metrics.Accuracy());

public static Task<Score> ScoreAsync(TaskState state, Target target, CancellationToken cancellationToken)
{
    var output = state.Output.Completion.Trim();
    // Since we're using prefill, the output should start with a number
    // Extract the first number from the output
    var match = LeadingNumber().Match(output);   // ^(\d+)
    if (match.Success)
    {
        var answer = match.Groups[1].Value;
        var correct = answer == target.Text;
        return Task.FromResult(new Score(correct ? 1.0 : 0.0) { Answer = output });
    }

    return Task.FromResult(new Score(0.0) { Answer = output, Explanation = "Could not extract a numerical answer" });
}
```

The scorer keeps its registry name `score_arithmetic`, which is the key of the sample scores and the row of the results table.

## Deviations from Python

- The scripted `--fake` model is an addition for running the demonstration offline (the Python example only runs through `inspect eval`): it evaluates the prefilled expression and answers its continuation (`2`), as a native prefill does; `-T prose=true` makes it answer in a sentence (`The answer is 2.`) so the scorer's "Could not extract a numerical answer" branch can be seen.
- Whether a live model continues the prefill is provider-dependent, as in Python: a `claude-*` deployment on the anthropic route treats the trailing assistant message as a native prefill (the Anthropic API rejects a prefill ending in whitespace), while OpenAI-family deployments on the models route receive it as an ordinary assistant turn and may restate the answer in prose, which the scorer marks 0.0.
- The prefill solver reads the sample's `prefill` metadata as a string (anything else fails the sample with an `InvalidOperationException`, where pydantic would reject the message content).

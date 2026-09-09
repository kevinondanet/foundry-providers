# Reasoning

A C# port of `examples/reasoning.py` of the inspect_ai repository: the `reasoning` task, a react agent with a trivial `validate` tool and a prompt that requires reasoning before each of two algebra problems, run under `GenerateConfig(reasoning_effort="medium", reasoning_tokens=8192, max_tokens=16384)` so reasoning content flows through the agent loop and into the log.

## What it demonstrates

Reasoning models inside an agent loop. The single sample asks the model to solve `3*x^3-5*x=1`, call `validate()`, then solve `x^2 - 5x + 6 = 0` and call `validate()` again; the react prompt insists on reasoning before each problem. The task's `Config` carries the reasoning settings, which the providers map to each model family's wire fields; the reasoning the model returns comes back as `ContentReasoning` items on the assistant messages, is replayed on the following turns of the loop (with its signature on the anthropic route), and is written to the log. `--display conversation` prints every turn as it happens, reasoning included (between `<think>` markers).

## Running it

### Offline (the examples runner)

The example is `reasoning` in the examples project (`ReasoningExample`, see [examples/README.md](../README.md) for the runner and its flags). With `--fake` a scripted model reasons about each problem in a `ContentReasoning` item, calls `validate` twice and submits; `--display conversation` shows the reasoning:

```bash
dotnet run --project examples -- reasoning --fake --display conversation
```

### Against a Foundry deployment

Needs `az login`, `AZUREAI_BASE_URL` (see the root README's "Environment variables") and a reasoning-capable deployment:

```bash
dotnet run --project examples -- reasoning --model <deployment> --display conversation
```

- On the models route a gpt-5 / o-series deployment receives `reasoning_effort: medium` (and `max_completion_tokens`); Azure usually withholds the reasoning text itself, so the log shows reasoning tokens in the usage rather than `ContentReasoning` items. Deployments without reasoning controls (gpt-4o and the like) get no reasoning fields.
- The gpt-5.6 family (`gpt-5.6-sol` / `-luna` / `-terra` on this resource, verified live on 2026-09-08) rejects function tools combined with `reasoning_effort` on the chat-completions route: HTTP 400 "Function tools with reasoning_effort are not supported ... use /v1/responses or set reasoning_effort to 'none'". Those deployments now run on the Responses route automatically (`--model gpt-5.6-sol`, or `--route responses` explicitly), where the effort goes out as `reasoning.effort` and the encrypted reasoning items are replayed between turns; `--route models` reproduces the 400. Verified live on 2026-09-09: with `--model gpt-5.6-sol` the tools ran with effort `medium`, the encrypted reasoning items were replayed on turns 2 and 3 and `reasoning_tokens` was reported. A readable summary is opt-in on this route (`--reasoning-summary auto` on the `inspectai` CLI and the Sample, i.e. `GenerateConfig.ReasoningSummary`; this runner has no flag for it; Python defaults to `auto`); the test resource accepted it and returned summary text, stored on `ContentReasoning.Summary` and written to the log as `summary`.
- On the anthropic route (`--route anthropic`, chosen automatically for `claude-*` deployments) `reasoning_tokens` becomes the extended-thinking budget and the thinking blocks come back as `ContentReasoning` items with their signatures, which is where the demonstration shows the most.

### The `inspectai` CLI

The task is marked with `[Task("reasoning")]`, so the CLI discovers it in the built assembly, as `inspect eval reasoning.py` does for the Python module:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval reasoning \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --model azureai/<deployment>
```

The exit code of the runner is 0 when the log reports success, 1 when it does not, 2 for a usage or prerequisite error, and 3 on cancellation or an unexpected error.

## The task

`ReasoningTask.cs` is the port of the Python module. `@tool validate()` becomes a `ToolDef` built by reflection over a method whose `[Description]` attributes carry what Python reads from the docstring; `@task reasoning()` becomes a `[Task]` static method.

```csharp
public static ToolDef Validate() => ToolDef.FromMethod((Func<string, Task<bool>>)Execute, name: "validate");

[Description("Validate the answer to a mathematical question.")]
private static Task<bool> Execute([Description("Answer to validate")] string answer) => Task.FromResult(true);

[Task("reasoning")]
public static EvalTask Reasoning() => new()
{
    Name = "reasoning",
    Dataset = new MemoryDataset(
    [
        new Sample("Solve 3*x^3-5*x=1, then call the validate() tool validate your answer. Then after that, solve x^2 - 5x + 6 = 0 and once again call the validate() tool to validate your answer."),
    ]),
    Solver = Agents.AsSolver(Agents.React(
        prompt: "Note that you must use reasoning before solving each problem presented. Do not attempt to solve a problem without reasoning first.",
        tools: [Validate()])),
    Config = new GenerateConfig { ReasoningEffort = "medium", ReasoningTokens = 8192, MaxTokens = 16384 },
};
```

As in Python the task has no scorer: the point is the transcript. The tool's `bool` result is stringified as `True`, as Python's `str()` would.

## Deviations from Python

- The scripted `--fake` model is an addition for running the demonstration offline (the Python example only runs through `inspect eval`): it reasons about each problem in a `ContentReasoning` item, calls `validate` twice and submits, and reports reasoning tokens in its usage.
- The `validate` tool's docstring (its description and the `answer` argument's description) is carried by `[Description]` attributes, which is what this port's `ToolDef.FromMethod` reads in place of the docstring Python parses; the tool's name, schema and `True` result are the same.
- Which reasoning controls reach the model is provider-dependent, as in Python: on the models route `reasoning_effort` is sent to gpt-5 / o-series deployments (Azure usually withholds the reasoning text and reports only reasoning tokens), while on the anthropic route `reasoning_tokens` becomes the extended-thinking budget and the thinking blocks come back as `ContentReasoning` items.

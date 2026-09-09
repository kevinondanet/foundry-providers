# Code Execution

## Introduction

This is a C# port of `examples/code_execution.py` of the inspect_ai repository: a one-sample task that gives the model the `code_execution()` tool and asks it to add 435678 + 23457 in Python and print the result. There is no scorer; the point is the tool. In Python, `code_execution()` runs the code on the provider's own servers where the provider offers that (OpenAI, Anthropic, Google, Grok, Mistral) and falls back to the `python()` tool in the sample's sandbox everywhere else. This port has no server-side execution on either Foundry route, so every model takes the sandbox fallback: `BuiltinTools.CodeExecution()` pipes the code to `python3 -` inside the sandbox.

## Running it

### The examples runner

The example is `code_execution` in the examples project (`CodeExecutionExample`, see [examples/README.md](../README.md) for the runner and its flags). Offline, with a scripted model that calls `code_execution` with `print(435678 + 23457)` and a fake sandbox that answers the `python3` run with `459135`:

```bash
dotnet run --project examples -- code_execution --fake
```

`--sandbox local` runs the code for real with this host's `python3` (still offline):

```bash
dotnet run --project examples -- code_execution --fake --sandbox local
```

Against a Foundry deployment, in a Docker sandbox (the example's default, as in the Python task; the default image has `python3`), which needs Docker, `az login` and `AZUREAI_BASE_URL` (see the root README's "Environment variables"):

```bash
dotnet run --project examples -- code_execution --model <deployment>
```

`--sandbox none` is a prerequisite error (the fallback runs in the sandbox). `dotnet run --project examples -- code_execution --help` prints the flags, the task and the deviations.

The exit code is 0 when the log reports success, 1 when it does not, 2 for a usage or prerequisite error, and 3 on cancellation or an unexpected error.

### The `inspectai` CLI

The task is marked with `[Task("code_execution_task")]`, so the CLI can discover it in the built assembly, exactly as `inspect eval code_execution.py` does for the Python module:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval code_execution_task \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --model azureai/<deployment>
```

## Task Setup

```csharp
[Task("code_execution_task")]
public static EvalTask Create() => Build(new SandboxSpec("docker"));

public static EvalTask Build(SandboxSpec sandbox) => new()
{
    Name = "code_execution_task",
    Dataset = new MemoryDataset(
    [
        new Sample("Please use your available tools to execute Python code that adds 435678 + 23457 and then prints the result."),
    ]),
    Solver = Solvers.Chain(Solvers.UseTools(BuiltinTools.CodeExecution()), Solvers.Generate()),
    Sandbox = sandbox,
};
```

`BuiltinTools.CodeExecution()` takes the same `providers` configuration as Python (`new CodeExecutionProviders { Python = false }` disables the fallback, `Python = CodeExecutionProviderOption.PythonOptions(timeout, sandbox)` configures it) and carries `__internal_tool_type__` and the normalized providers map in its options exactly as Python does, so a provider could key on them later; today the sandbox fallback is the only path.

## Deviations from Python

- No model provider of this port executes `code_execution` server-side (Python's native OpenAI, Anthropic, Google, Grok and Mistral execution): every model takes the `python()` sandbox fallback, so the task needs a sandbox with `python3` (`docker` by default; `--sandbox local` runs `python3` on this host).
- The Python task is fixed to `sandbox="docker"`; here the sandbox comes from the runner (`--sandbox`, default `docker`) and `--sandbox none` is refused because the fallback runs in it.
- The `--fake` scripted model and the fake sandbox script (which answers the `python3` run with `459135`) are additions for running offline; the Python example only runs through `inspect eval`.

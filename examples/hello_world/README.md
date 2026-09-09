# Hello World

The simplest possible Inspect eval, useful for testing your configuration / network / platform etc. It is a C# port of `examples/hello_world.py` of the inspect_ai repository: one inline sample (`Just reply with Hello World`, target `Hello World`), the `generate()` solver and the `exact()` scorer, run on this repository's eval engine.

## Running it

### Offline

The example is `hello_world` in the examples project (`HelloWorldExample`; see [examples/README.md](../README.md) for the runner and its flags). With `--fake` a scripted model answers `Hello World`, so the run needs no deployment and no network and `exact()` scores CORRECT (mean 1.0):

```bash
dotnet run --project examples -- hello_world --fake
```

`-T fake_answer=<text>` changes what the scripted model says, to see the INCORRECT path (`exact()` normalizes case and punctuation before comparing, as Python's `normalize()` does, so `hello, world!` still counts as CORRECT; a different text does not):

```bash
dotnet run --project examples -- hello_world --fake -T "fake_answer=Goodbye World"
```

### Live

Against a Foundry deployment, which needs `az login` and `AZUREAI_BASE_URL` (see the root README's "Environment variables"); no sandbox, no API keys, no Docker:

```bash
dotnet run --project examples -- hello_world --model <deployment>
```

`dotnet run --project examples -- hello_world --help` prints the flags, the task and the deviations. The exit code is 0 when the log reports success, 1 when it does not, 2 for a usage or prerequisite error, and 3 on cancellation or an unexpected error.

### The `inspectai` CLI

The task is marked with `[Task("hello_world")]`, so the CLI discovers it in the built assembly, exactly as `inspect eval hello_world.py` does for the Python module:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval hello_world \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --model azureai/<deployment>
```

## The task

The Python task:

```python
@task
def hello_world():
    return Task(
        dataset=[
            Sample(
                input="Just reply with Hello World",
                target="Hello World",
            )
        ],
        solver=[
            generate(),
        ],
        scorer=exact(),
    )
```

and its port (`HelloWorldExample.cs`):

```csharp
[Task("hello_world")]
public static EvalTask HelloWorld() => Build();

public static EvalTask Build(SandboxSpec? sandbox = null) => new()
{
    Name = "hello_world",
    Dataset = new MemoryDataset([new Sample("Just reply with Hello World") { Target = "Hello World" }]),
    Solver = Solvers.Generate(),
    Scorers = [Scorers.Exact()],
    Sandbox = sandbox,
};
```

`Scorers.Exact()` is the port of `exact()`: CORRECT when the normalized completion equals the normalized target, reported with `mean` and `stderr`.

## Deviations from Python

- The `--fake` scripted model is an addition for running the example offline: it answers `Hello World` (`exact()` scores CORRECT), or the text given as `-T fake_answer=<text>`, which shows the INCORRECT path. The Python example only runs through `inspect eval`.
- The task takes the sandbox the runner resolves (`--sandbox`); the Python task declares none, and none is the default here too.

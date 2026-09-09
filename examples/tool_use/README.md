# Tool Use

## Introduction

This is a C# port of `examples/tool_use.py` of the inspect_ai repository: five small tasks that show how custom `@tool` functions are defined and given to the model. `add` is a pure function; `list_files`, `read_file` and `write_file` reach the sample's sandbox (`sandbox().exec`, `sandbox().read_file`, `sandbox().write_file`), and a failed `ls` is reported to the model as a `ToolError`. `parallel_add` asks the model to call `add` twice in one turn, which the engine executes concurrently. The tasks are scored with `match(numeric=True)` and `includes()`.

| Python `@task`     | Tool(s)      | Sandbox | Scorer                |
| ------------------ | ------------ | ------- | --------------------- |
| `addition_problem` | `add`        | none    | `match(numeric=True)` |
| `bash`             | `list_files` | local   | `includes()`          |
| `read`             | `read_file`  | local   | `match()`             |
| `write`            | `write_file` | local   | `match()`             |
| `parallel_add`     | `add` (x2)   | none    | `includes()`          |

## Running it

### The examples runner

The example is `tool_use` in the examples project (`ToolUseExample`, see [examples/README.md](../README.md) for the runner and its flags); `--task` picks the `@task` (default `addition_problem`). Offline, with a scripted model that makes the tool call each task asks for and answers from the result, and a fake sandbox that answers `ls /usr/bin` with a fixed listing and starts out with a `foo.txt` holding `bar`:

```bash
dotnet run --project examples -- tool_use --fake
dotnet run --project examples -- tool_use --task bash --fake
dotnet run --project examples -- tool_use --task read --fake
dotnet run --project examples -- tool_use --task write --fake
dotnet run --project examples -- tool_use --task parallel_add --fake
```

`--sandbox local` runs the tools for real on this host (a real `ls /usr/bin`; `read` then reports `foo.txt` as missing, because every sample gets a fresh temporary directory):

```bash
dotnet run --project examples -- tool_use --task bash --fake --sandbox local
```

Against a Foundry deployment, which needs `az login` and `AZUREAI_BASE_URL` (see the root README's "Environment variables"); the deployment must support function calling:

```bash
dotnet run --project examples -- tool_use --task parallel_add --model <deployment>
```

`--sandbox none` is a prerequisite error for `bash`, `read` and `write` (their tools need a sandbox); `addition_problem` and `parallel_add` have no sandbox, as in Python. `dotnet run --project examples -- tool_use --help` prints the flags, the tasks and the deviations.

The exit code is 0 when the log reports success, 1 when it does not, 2 for a usage or prerequisite error, and 3 on cancellation or an unexpected error.

### The `inspectai` CLI

Each task is marked with `[Task("<name>")]`, so the CLI can discover it in the built assembly, exactly as `inspect eval tool_use.py@bash` does for the Python module:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval bash \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --model azureai/<deployment>
```

## Tools

A Python `@tool` returns an `execute` function whose signature and docstring become the tool's schema. Here a `ToolDef` is built from a static method with `ToolDef.FromMethod`; the parameters are the schema and `[Description]` attributes carry what the docstring summary and `Args:` section carry in Python:

```csharp
public static ToolDef Add() => ToolDef.FromMethod(new Func<int, int, int>(AddExecute), name: "add");

[Description("Add two numbers.")]
private static int AddExecute(
    [Description("First number to add.")] int x,
    [Description("Second number to add.")] int y) => x + y;

[Description("List the files in a directory.")]
private static async Task<string> ListFilesExecute([Description("Directory")] string dir, CancellationToken cancellationToken)
{
    var result = await SampleContext.Require().Sandbox().ExecAsync(["ls", dir], cancellationToken: cancellationToken);
    if (result.Success)
    {
        return result.Stdout;
    }

    throw new ToolError(result.Stderr);
}
```

`SampleContext.Require().Sandbox()` is Python's `sandbox()`: the sample's default sandbox environment. `ToolError` reaches the model as a tool error message and the sample continues; a `FileNotFoundException` from `read_file` does the same.

## Tasks

```csharp
[Task("addition_problem")]
public static EvalTask AdditionProblem() => new()
{
    Name = "addition_problem",
    Dataset = new MemoryDataset([new Sample("What is 1 + 1?") { Target = new Target(["2", "2.0"]) }]),
    Solver = Solvers.Chain(Solvers.UseTools(ToolUseTools.Add()), Solvers.Generate()),
    Scorers = [Scorers.Match(numeric: true)],
};

[Task("bash")]
public static EvalTask Bash() => Bash(new SandboxSpec("local"));

public static EvalTask Bash(SandboxSpec sandbox) => new()
{
    Name = "bash",
    Dataset = new MemoryDataset(
    [
        new Sample("Please list the files in the /usr/bin directory. Is there a file named 'python3' in the directory?") { Target = new Target(["Yes"]) },
    ]),
    Solver = Solvers.Chain(
        Solvers.SystemMessage("\nPlease answer exactly Yes or No with no additional words.\n"),
        Solvers.UseTools(ToolUseTools.ListFiles()),
        Solvers.Generate()),
    Sandbox = sandbox,
    Scorers = [Scorers.Includes()],
};
```

`read`, `write` and `parallel_add` follow the same shape (see [ToolUseTasks.cs](./ToolUseTasks.cs)). The runner builds the sandbox tasks through `ToolUseExample` with the sandbox of `--sandbox`.

## Deviations from Python

- Both local sandboxes give every sample a fresh temporary working directory (Python's `LocalSandboxEnvironment` creates a `tempfile.TemporaryDirectory` per sample and resolves relative paths under it, as `Sandbox/Local/LocalSandboxEnvironment.cs` does), so in either port `read` finds no `foo.txt` under `--sandbox local` and reports it as missing (a tool error the model relays); only the fake sandbox seeds `foo.txt` with `bar`.
- `bash`, `read` and `write` are fixed to `sandbox="local"` in Python; here the sandbox comes from the runner (`--sandbox`, default `local`) and `--sandbox none` is refused for them. `addition_problem` and `parallel_add` have no sandbox in Python and ignore `--sandbox`.
- Tool descriptions and parameter descriptions come from `[Description]` attributes rather than docstrings (the docstrings' `Returns` sections are not part of the schema in Python either).
- The `--fake` scripted model and the fake sandbox script (a fixed `/usr/bin` listing, a seeded `foo.txt`) are additions for running offline; the Python example only runs through `inspect eval`.

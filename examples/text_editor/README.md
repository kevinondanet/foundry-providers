# Text Editor

## Introduction

This is a C# port of `examples/text_editor.py` of the inspect_ai repository: a single-sample task that drives the built-in `text_editor` tool through its `create`, `view`, `str_replace` and `insert` commands over four `generate()` turns, each prompted by a `user_message()`, in a Docker sandbox. A custom `verify_edit` scorer then reads `/tmp/greeting.py` back from the sandbox and scores 1.0 when the edits landed (`Goodbye` present, `Hello` gone, the `# Author: Inspector` line present).

The `text_editor` tool runs inside the sandbox: on first use the engine injects the pinned `inspect-sandbox-tools` launcher into the container and talks to it over JSON-RPC (`docs/ports/sandbox-tools.md`), so the editing itself happens where the file lives.

## Running it

### The examples runner

The example is `text_editor` in the examples project (`TextEditorExample`, see [examples/README.md](../README.md) for the runner and its flags). Offline, with a scripted model that issues the four editor calls the prompts ask for, and a fake sandbox that reports the launcher as already injected and answers its JSON-RPC `text_editor` requests with an in-memory port of the launcher's editor (nothing is downloaded, no container is started):

```bash
dotnet run --project examples -- text_editor --fake
```

Add `--display conversation` to watch the four calls and the `cat -n` style results as they happen.

Against a Foundry deployment, in a Docker sandbox (the example's default, as in the Python task), which needs Docker, `az login` and `AZUREAI_BASE_URL` (see the root README's "Environment variables"), and, the first time, network access to download the `inspect-sandbox-tools` launcher into `~/.cache/inspect-azureai/sandbox-tools` (or `INSPECT_SANDBOX_TOOLS_BINARIES_DIR`):

```bash
dotnet run --project examples -- text_editor --model <deployment>
```

`--sandbox none` is a prerequisite error (the tool runs inside the sandbox) and `--sandbox local` is not usable on macOS or Windows (the launcher is a Linux binary). `dotnet run --project examples -- text_editor --help` prints the flags, the task and the deviations.

The exit code is 0 when the log reports success, 1 when it does not, 2 for a usage or prerequisite error, and 3 on cancellation or an unexpected error.

### The `inspectai` CLI

The task is marked with `[Task("text_editor_task")]`, so the CLI can discover it in the built assembly, exactly as `inspect eval text_editor.py` does for the Python module:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval text_editor_task \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --model azureai/<deployment>
```

## Task Setup

```csharp
[Task("text_editor_task")]
public static EvalTask Create() => Build(new SandboxSpec("docker"));

public static EvalTask Build(SandboxSpec sandbox) => new()
{
    Name = "text_editor_task",
    Dataset = new MemoryDataset(
    [
        new Sample(
            "Use the text_editor tool to create a file at /tmp/greeting.py "
            + "with a Python function called `greet` that takes a `name` "
            + "parameter and returns the string 'Hello, {name}!'.")
        {
            Target = "Goodbye",
        },
    ]),
    Solver = Solvers.Chain(
        Solvers.UseTools(TextEditor.Create()),
        Solvers.Generate(),
        Solvers.UserMessage("Now use the text_editor to view the file /tmp/greeting.py and confirm its contents."),
        Solvers.Generate(),
        Solvers.UserMessage("Now use the text_editor str_replace command to change 'Hello' to 'Goodbye' in /tmp/greeting.py."),
        Solvers.Generate(),
        Solvers.UserMessage("Now use the text_editor insert command to insert the line '# Author: Inspector' after line 1 of /tmp/greeting.py."),
        Solvers.Generate()),
    Scorers = [VerifyEdit()],
    Sandbox = sandbox,
};
```

## The scorer

`@scorer(metrics=[accuracy()]) def verify_edit()` becomes `Scorers.Custom("verify_edit", VerifyEditScore, Metrics.Accuracy())` over a `Scorer` delegate; `sandbox().read_file` is `SampleContext.Require().Sandbox().ReadFileAsync`, which throws `FileNotFoundException` where Python raises `FileNotFoundError`:

```csharp
public static async Task<Score> VerifyEditScore(TaskState state, Target target, CancellationToken cancellationToken)
{
    try
    {
        var content = await SampleContext.Require().Sandbox().ReadFileAsync("/tmp/greeting.py", cancellationToken);
        var hasGoodbye = content.Contains("Goodbye", StringComparison.Ordinal);
        var hasNoHello = !content.Contains("Hello", StringComparison.Ordinal);
        var hasAuthor = content.Contains("# Author: Inspector", StringComparison.Ordinal);
        var correct = hasGoodbye && hasNoHello && hasAuthor;
        return new Score(correct ? 1.0 : 0.0)
        {
            Answer = content,
            Explanation = correct ? "File contains expected edit." : "Edit not applied correctly.",
        };
    }
    catch (FileNotFoundException)
    {
        return new Score(0.0) { Answer = "File not found", Explanation = "The file /tmp/greeting.py was not created." };
    }
}
```

## The fake sandbox

`--fake` cannot inject the Linux launcher into anything, so `FakeTextEditorSandbox` scripts the runner's fake sandbox: `test -r <launcher>` succeeds (the engine then skips the download and injection), and `<launcher> exec` reads the JSON-RPC request from stdin and runs it through `TextEditorEmulator`, an in-memory port of the launcher's editor (`inspect_sandbox_tools/_in_process_tools/_text_editor/text_editor.py`) over the sample's file store, so the scorer's `read_file` sees the edits. Its messages are the launcher's: `File created successfully at: ...`, the `cat -n` rendering, the `has been edited` snippets, and the `ToolException` texts for a missing path, a duplicate `old_str` or an out-of-range `insert_line` (sent back with the launcher's JSON-RPC error code, so they reach the model as tool errors).

## Deviations from Python

- The Python task is fixed to `sandbox="docker"`; here the sandbox comes from the runner (`--sandbox`, default `docker`) and `--sandbox none` is refused because the `text_editor` tool runs inside the sandbox. `--sandbox local` is not usable on macOS or Windows: the injected `inspect-sandbox-tools` launcher is a Linux binary.
- The `--fake` scripted model and the fake sandbox (which reports the launcher as already injected and answers its JSON-RPC `text_editor` requests with an in-memory port of the launcher's editor, so nothing is downloaded and no container is needed) are additions for running offline; the Python example only runs through `inspect eval`.
- The fake editor implements `view`, `create`, `str_replace` and `insert` with the launcher's messages; `undo_edit` reports no history and directories are not modelled.

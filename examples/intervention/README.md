# Intervention Demo

## Introduction

This is a prototype of an Inspect agent running in a Linux sandbox with human intervention. It utilises Inspect's [Interactivity features](https://inspect.aisi.org.uk/interactivity.html). This is meant to serve as a starting point for evaluations which need these features, such as manual open-ended probing.

It is a C# port of the `examples/intervention` example of the inspect_ai repository (`intervention.py`, the three mode folders and its README): the task, the three modes, the prompts, the operator's input screens and the intervention loop are the same, run on this repository's eval engine. The operator types the initial prompt at a "User Prompt" input screen; the model works with the mode's tools; whenever it stops calling tools a "Next Action" screen lets the operator send a message, press enter to ask the model to continue, or type `exit` to end the conversation.

## Usage Modes

Three modes are supported: `shell` mode equips the model with bash and python tools, and `computer` mode provides it with a full desktop computer, and `multi-tool` provides stateful `bash_session`, `web_browser`, and `text_editor` tools. Each mode has its own Docker Compose sandbox, copied verbatim from the Python example: `shell/compose.yaml` builds `shell/Dockerfile` (Ubuntu 24.04 with a Python venv), `computer/compose.yaml` runs `aisiuk/inspect-computer-tool` with VNC/noVNC published on loopback ports, and `multi_tool/compose.yaml` runs `aisiuk/inspect-tool-support`.

### Offline (scripted operator, model and sandbox)

Each mode has a script (`InterventionScript`) that plays all three parties, so the demonstration runs to completion with nothing waiting at the terminal and no Docker: the scripted model calls the mode's tools, a scripted sandbox answers them (canned `ls`/`python` output for shell; a fake `computer_tool.py` returning screenshots for computer; fake `inspect-sandbox-tools` and `inspect-tool-support` JSON-RPC services for multi-tool), and the scripted operator types the prompt, `enter`, a follow-up and `exit`. `--display conversation` prints the user and assistant messages to the terminal, as the Python README suggests:

```bash
dotnet run --project examples -- intervention --fake --display conversation
dotnet run --project examples -- intervention --fake -T mode=computer --display conversation
dotnet run --project examples -- intervention --fake -T mode=multi-tool --display conversation
```

`--sandbox local` runs the shell mode's bash and python tools on this host instead of the scripted sandbox (the scripted model's commands are `ls -la`, a one-line Python script and `python3 --version`).

### Live (a person at the terminal, a Foundry deployment, Docker)

Against a Foundry deployment in the mode's Docker Compose sandbox (the example's default, as in the Python task), which needs Docker, `az login` and `AZUREAI_BASE_URL` (see the root README's "Environment variables"). The prompts then read from the terminal:

```bash
dotnet run --project examples -- intervention --model <deployment> --display conversation
dotnet run --project examples -- intervention --model <deployment> -T mode=computer --display conversation
dotnet run --project examples -- intervention --model <deployment> -T mode=multi-tool --display conversation
```

The computer mode needs a vision-capable deployment (it receives screenshots) and a VNC/noVNC viewer on the published loopback ports to watch the desktop. See the documentation on the [Computer Tool](https://inspect.aisi.org.uk/tools-standard.html#sec-computer) for additional details on Inspect computer use. `--sandbox none` is a prerequisite error because every mode's tools need a sandbox. `dotnet run --project examples -- intervention --help` prints the flags, the task and the deviations.

The exit code is 0 when the log reports success, 1 when it does not, 2 for a usage or prerequisite error, and 3 on cancellation or an unexpected error.

### The `inspectai` CLI

The task is marked with `[Task("intervention")]` and takes the Python task's parameters (`mode`, `approval`), so the CLI can discover it in the built assembly, exactly as `inspect eval examples/intervention -T mode=computer` does for the Python module:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval intervention \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  -T mode=computer --display conversation \
  --model azureai/<deployment>
```

## Approval

You can add human approval to either mode, by specifying the `approval` task parameter. For example:

```bash
dotnet run --project examples -- intervention --model <deployment> -T mode=shell -T approval=true --display conversation
```

For `shell` mode, this will result in each and every bash or python call requiring approval. For `computer` mode, this will result in only some actions requiring approval (e.g. clicks require approval, but mouse moves do not). Here is the approval policy used for computer mode, `computer/approval.json` (this port reads JSON policy files only; the Python `approval.yaml` is kept next to it verbatim and has the same approvers, patterns and order):

```json
{
  "approvers": [
    {
      "name": "human",
      "tools": [
        "computer(action='key'",
        "computer(action='left_click'",
        "computer(action='middle_click'",
        "computer(action='double_click'"
      ]
    },
    {
      "name": "auto",
      "tools": "*"
    }
  ]
}
```

Under `--fake` the human approver is a scripted prompter that rejects every call it is asked about (so nothing waits at the terminal); the scripted model's path does not depend on the decision, and every decision is recorded as an `ApprovalEvent` in the log and listed in the runner's summary:

```bash
dotnet run --project examples -- intervention --fake -T mode=computer -T approval=true
```

See the [Approval](https://inspect.aisi.org.uk/approval.html) documentation for additional details on creating approval policies.

## Task Setup

The task (`Intervention.Build`) is Python's `intervention(mode, approval)`: one default sample, the mode's solver chain and the mode's compose file as the sandbox:

```csharp
[Task("intervention")]
public static EvalTask InterventionTask(string mode = "shell", bool approval = false) => Intervention.Build(mode, approval);

public static EvalTask Build(string mode = ShellMode, bool approval = false, string? exampleDirectory = null, InputConsole? console = null)
{
    var directory = exampleDirectory ?? DefaultExampleDirectory;
    return new EvalTask
    {
        Name = "intervention",
        Dataset = new MemoryDataset([new Sample("prompt")]),
        Solver = InterventionAgent(mode, console),
        Sandbox = new SandboxSpec("docker", Path.Combine(directory, ComposeFile(mode))),
        Approval = approval ? ApprovalOption.FromSpec(ApprovalSpec(mode, directory)) : null,
    };
}
```

`InterventionAgent(mode)` is Python's `intervention_agent`: `Solvers.Chain(Solvers.SystemMessage(<mode prompt>), UserPrompt(), Solvers.UseTools(<mode tools>), AgentLoop())`, where the tools are `SandboxTools.Bash()` and `SandboxTools.Python()` (shell), `Computer.Create()` (computer), or `BashSession.Create()`, `TextEditor.Create()` and `BuiltinTools.WebBrowser()` (multi-tool). `UserPrompt` opens the "User Prompt" input screen (`InputScreen.Open`) and asks `Please enter your initial prompt for the model:` (Python's `Prompt.ask`); `AgentLoop` generates until the model stops calling tools, then `AskForNextActionAsync` opens the "Next Action" screen with Python's prompt and an empty default, and `exit` / an empty line (`Please continue working on this task.`) / any other text are handled exactly as the Python `match`. The `InputScreen`s record `InputEvent`s in the sample transcript.

## Deviations from Python

- The computer mode's approval policy is `computer/approval.json` rather than `approval.yaml`: this port reads JSON policy files only (`approval.yaml` is copied verbatim next to it; the approvers, tool patterns and order are identical).
- The task's sandbox is the mode's compose file (`shell/`, `computer/` or `multi_tool/compose.yaml`) under the example folder, as in Python; the runner only knows one compose file per example, so this example substitutes the mode's file whenever the resolved sandbox is Docker. `--sandbox local` runs the shell mode's bash and python tools on this host; computer and multi-tool need their images (or `--sandbox fake`). `--sandbox none` is refused because every mode's tools need a sandbox.
- The operator's prompts read from an `InputConsole` parameter of the solvers (the process console by default, as Python's `input_screen` + rich `Prompt.ask`); under `--fake` the answers come from the mode's script (`InterventionScript`), so nothing waits at the terminal.
- The `--fake` scripted model, operator and sandbox (canned `ls`/`python` output for the shell mode; a fake `computer_tool.py` answering screenshots for the computer mode; fake `inspect-sandbox-tools` and `inspect-tool-support` JSON-RPC services for the multi-tool mode) are additions for running the demonstration offline, and a message limit of 40 guards the fake run. The Python example only runs through `inspect eval`.
- `state.user_prompt.content = ...` becomes a replacement of the last user message (messages are immutable records here); the transcript records the same `InputEvent`s.
- Any `-T mode` other than `shell` and `computer` selects the multi-tool mode, exactly as Python's `match` statement does (`Literal` types are not checked at runtime there either).
- Input screens and the conversation display are plain text (`── Title ──` panels) rather than rich panels; the console reporter has no live progress region to pause.

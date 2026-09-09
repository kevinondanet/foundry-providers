# Computer

A C# port of `examples/computer/computer.py` of the inspect_ai repository. Three desktop tasks are solved by a `react` agent that drives a Linux desktop with the `computer()` tool: find the contents of `/tmp/flag.txt` through the GUI (the flag is written into the sandbox by the sample's `files`), launch a terminal and type a sentence into it, and launch the calculator and compute 123 x 456. The desktop is the `aisiuk/inspect-computer-tool` image of `compose.yaml` (Xvfb, xfce, xdotool, a VNC/noVNC server; the file is copied verbatim, `moonWeight.ods` alongside it as in the Python folder), and the answers are scored with `includes()`.

What it demonstrates:

- `Computer.Create()` (`computer()`): one tool with the 21 desktop actions (`screenshot`, `left_click`, `double_click`, `type`, `key`, `scroll`, ...), each run as `python3 /opt/inspect/tool/computer_tool.py <action> ...` in the sandbox that has the service, returning text plus a screenshot (`ContentImage`).
- The tool's `model_input` hook (`ToolDef.ModelInput`): screenshots older than `max_screenshots` (1) are replaced by a placeholder text before each model call, so the conversation keeps every image while the model only sees the latest.
- `Agents.React(prompt: new AgentPrompt(Instructions: SYSTEM_MESSAGE), tools: [computer()])`, `Sample.Files`, and a Docker Compose sandbox that publishes VNC ports.

## Running it

### Offline

```bash
dotnet run --project examples -- computer --fake --sandbox fake
```

A scripted model (`FakeComputerModel`) follows the system message's protocol for each sample: a screenshot of the desktop, a double-click on the application's icon, the command typed with `press_enter`, another screenshot, then `submit` with what the screen shows. The sandbox is a scripted desktop (`FakeComputerSandbox`): `test -r /opt/inspect/tool/computer_tool.py` succeeds and every `computer_tool.py` action answers the service's JSON with a description of the screen and a 1x1 PNG; double-clicking the Terminal or Calculator icon opens it, and text followed by Return "runs" (`cat /tmp/flag.txt` reads the flag the sample wrote into that sample's sandbox, any other word is `bash: <word>: command not found`, the calculator evaluates `a*b`). No container or vision model is involved. Add `--display conversation` to watch the steps.

### Against a Foundry deployment

```bash
dotnet run --project examples -- computer --model <deployment>
```

Requirements: Docker able to run the large amd64 `aisiuk/inspect-computer-tool` image (a full desktop per sample), `az login` and `AZUREAI_BASE_URL` (see the root README's "Environment variables"), and a vision-capable deployment (the tool returns screenshots). The sandbox is `--sandbox docker` with this folder's `compose.yaml` by default; the compose file publishes each container's VNC (5900) and noVNC (6080) ports on loopback, and the bound host ports appear in the sandbox connection info. `--sandbox none` is refused because the tool needs a sandbox, and `--sandbox local` fails at the first tool call because the tool service is not on this host.

### The `inspectai` CLI

The task is marked `[Task("computer_task")]`, so the CLI can discover it in the built assembly, as `inspect eval computer.py` does for the Python module:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval computer_task \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --model azureai/<deployment>
```

## The task

```csharp
[Task("computer_task")]
public static EvalTask ComputerTask() => Build(new SandboxSpec("docker", Path.Combine(AppContext.BaseDirectory, "computer", "compose.yaml")));

public static EvalTask Build(SandboxSpec sandbox) => new()
{
    Name = "computer_task",
    Dataset = new MemoryDataset(
    [
        new Sample("Use the computer tool to find the contents of the file '/tmp/flag.txt'. When you've found it, report its contents.")
        {
            Target = "Frunobulax",
            Files = new Dictionary<string, string> { ["/tmp/flag.txt"] = "Frunobulax" },
        },
        new Sample("Use the computer tool to launch a terminal. Type 'Trudging across the tundra. Mile after Mile.' into the terminal. Important: Make sure that the terminal window is active before typing. When you are done, please use the submit tool to record the result of hitting enter in the terminal after entering that text.")
        {
            Target = "bash: Trudging: command not found",
        },
        new Sample("Use the computer tool to launch a calculator. Calculate 123 x 456. Report the result.") { Target = "56088" },
    ]),
    Solver = Agents.AsSolver(Agents.React(
        prompt: new AgentPrompt(Instructions: SystemMessage),
        tools: [Computer.Create()])),
    MessageLimit = 100,
    Scorers = [Scorers.Includes()],
    Sandbox = sandbox,
};
```

## Deviations from Python

- Python passes `messsage_limit=100` (a typo `Task(**kwargs)` ignores, so no message limit applies there); here the task's `MessageLimit` is 100, as the author intended.
- Python's `sandbox="docker"` resolves the `compose.yaml` next to `computer.py`; here the compose file is passed explicitly as `SandboxSpec("docker", "<example dir>/compose.yaml")` by the runner and the `[Task]` method. Its VNC/noVNC port mappings (`127.0.0.1::5900` and `::6080`) are published by docker compose as in Python; the bound host ports appear in the sandbox connection info rather than a Running Samples tab.
- The computer tool goes to every model as an ordinary JSON-schema function tool; the Anthropic-native `computer_20250124` substitution of Python's anthropic provider is not made on the Anthropic route.
- The `--fake` scripted model (screenshot, double-click the application icon, type the command with `press_enter`, screenshot, submit what the screen shows) and the fake sandbox (a scripted desktop answering every `computer_tool.py` action with a description of the screen and a 1x1 PNG; the terminal "runs" `cat /tmp/flag.txt` against the sample's files) are additions for running offline; the Python example only runs through `inspect eval` against the real container.
- `--sandbox none` is refused (the computer tool needs a sandbox) and `--sandbox local` fails at the first tool call with the tool's `PrerequisiteError` because `/opt/inspect/tool/computer_tool.py` is not on this host.

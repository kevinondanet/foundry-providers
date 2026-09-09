# Human Agent Demo

## Introduction

This is a demonstration of Inspect's [Human Agent](https://inspect.aisi.org.uk/human-agent.html): a task whose solver is a person rather than a model. It is a C# port of the `examples/human` example of the inspect_ai repository (`human.py`, `Dockerfile` and `compose.yaml`): the task, the sandbox and the `human_cli` agent are the same, run on this repository's eval engine.

The `human_cli` agent (`HumanCli.Agent`) installs a `task` command set into the sample's Docker sandbox, prints the `docker exec` command the person uses to log in, and then serves the commands they run inside the container over the sandbox-service protocol until they submit an answer:

| Command             | What it does                                     |
| ------------------- | ------------------------------------------------ |
| `task instructions` | Display task commands and instructions.          |
| `task start`        | Start the task clock (resume working).           |
| `task stop`         | Stop the task clock (pause working).             |
| `task note`         | Record a note in the task transcript.            |
| `task status`       | Print task status (clock, scoring, etc.)         |
| `task submit`       | Submit your final answer for the task.           |
| `task quit`         | Quit the task without submitting an answer.      |

The sandbox is built from this folder's `Dockerfile` (`python:3.12-bookworm` with a `nonroot` user) through `compose.yaml` (`build: .`, `network_mode: none`); the `-T user=root|nonroot` task parameter picks the login user. The submitted answer becomes the sample's output (`ModelOutput.FromContent("human_agent", answer)`), the clock actions and notes are recorded in the transcript, and the session is recorded under `/var/tmp/user-sessions` and read back at the end. No model is ever called.

## Running it

### Offline (a scripted person)

Under `--fake` the sandbox is a scripted container (`HumanFakeSandboxProvider`) that emulates the file commands the installer and the sandbox service run, and a scripted operator (`HumanOperatorScript`) plays the person: it runs `task instructions`, `task start`, `task note`, `task status` and `task submit 42` through the real request/response protocol (request files the service reads with `cat`, answers with `tee` and removes with `rm`, exactly as the generated Python client does), so the run completes with the answer and nothing waits for a login:

```bash
dotnet run --project examples -- human --fake
dotnet run --project examples -- human --fake -T user=nonroot -T fake_answer=hello
```

The console shows the login text, the commands and their replies as the terminal would, the status line on every clock change and the final answer; the log's sample output is the submitted answer with model `human_agent`.

### Live (a real person, Docker)

The real run builds the image and starts the container through Docker Compose, prints the `docker exec -it ... bash -l` login command, and waits until the person runs `task submit <answer>` in a second terminal. No model deployment is needed, so the run is started with `--fake` (which only affects the model, never called) and `--sandbox docker`:

```bash
dotnet run --project examples -- human --fake --sandbox docker
dotnet run --project examples -- human --fake --sandbox docker -T user=nonroot
```

`--sandbox local` is refused (the agent would append to this host's `~/.bashrc` and write `/opt/human_agent`), and `--sandbox none` is a prerequisite error because the agent needs a sandbox that supports connections. `dotnet run --project examples -- human --help` prints the flags, the task and the deviations.

The exit code is 0 when the log reports success, 1 when it does not, 2 for a usage or prerequisite error, and 3 on cancellation or an unexpected error.

### The `inspectai` CLI

The task is marked with `[Task("human")]` and takes the Python task's `user` parameter, so the CLI can discover it in the built assembly, exactly as `inspect eval examples/human -T user=nonroot` does for the Python module (the CLI resolves a model as it always does; the human agent never calls it):

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval human \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  -T user=nonroot \
  --model azureai/<deployment>
```

## Task Setup

The task (`HumanExample.Build`) is Python's `human(user)`: one default sample, `human_cli(user=user)` as the solver and the compose file as the sandbox:

```csharp
[Task("human")]
public static EvalTask Human(string? user = null) =>
    Build(user, new SandboxSpec("docker", Path.Combine(DefaultExampleDirectory, "compose.yaml")));

public static EvalTask Build(string? user, SandboxSpec? sandbox, IHumanAgentView? view = null, TimeSpan? pollingInterval = null) => new()
{
    Name = "human",
    Dataset = new MemoryDataset([new Sample("prompt")]),
    Solver = Agents.AsSolver(HumanCli.Agent(user: user, view: view, pollingInterval: pollingInterval)),
    Sandbox = sandbox,
};
```

`view` and `pollingInterval` pass through to the agent: the runner uses a `ConsoleHumanAgentView` writing to its output and, under `--fake`, a 50 ms polling interval for the scripted container (the docker default is 0.2 s).

## Deviations from Python

- No model is involved, but the runner always resolves one: a real human run is `--fake --sandbox docker` (the scripted model is never called; the Docker sandbox and the login command are real), since without `--fake` the runner would resolve a Foundry deployment first.
- Under `--fake` alone the sandbox is a scripted container (`HumanFakeSandboxProvider`, registered in place of the runner's fake sandbox, which cannot answer connection requests): it emulates the file commands the installer and the sandbox service run, and a scripted operator issues `task instructions`, `task start`, `task note`, `task status` and `task submit` through the real request/response protocol, so the run completes with the answer (`42`, or `-T fake_answer=<text>`) and nothing waits for a login. Python has no offline mode.
- The Textual Human Agent panel and its VS Code login links are not ported; the console view prints the login command, a status line on clock changes, the notes and the final answer (Python's `ConsoleView` is the same fallback).
- The agent's polling interval is a parameter (50 ms under `--fake`, the sandbox's default otherwise), an addition of the C# `human_cli`.
- The compose file's `network_mode: none` and `build: .` are honoured by the Docker Compose sandbox; `--sandbox local` is refused because the agent would append to this host's `~/.bashrc` and write `/opt/human_agent`.

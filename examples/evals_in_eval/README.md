## Evals Inside An Eval

This example demonstrates running Inspect evals (in Docker containers) inside an Inspect eval. The example involves installing Inspect AI itself in a container and instructing [Claude Code](https://docs.anthropic.com/en/docs/claude-code) as an Inspect agent to run evals. It is a C# port of the `examples/evals_in_eval` example of the inspect_ai repository (`task.py`, `claude.py`, `file_probe.py`, `bash_task.py`, `compose.yaml`, `Dockerfile` and its README), run on this repository's eval engine.

> [!CAUTION]
> Do not use this example to evaluate adversarial or untrusted agents. The agent is intentionally given full control of a rootless Docker daemon in a privileged sidecar. Rootless mode prevents daemon control from directly granting root in the sidecar, but the privileged sidecar still has weakened isolation from the Docker host (the Docker Desktop Linux VM on macOS and Windows). This Compose file sets no CPU, memory, or PID limits. Run this example only on a disposable, isolated machine or VM.

The Compose configuration intentionally permits outbound Internet access because the nested Docker daemon must pull sandbox images for the inner evaluations.

> [!NOTE] The correct way to run an eval in an eval is to shell out a subprocess that has the eval you want to run. That protects many error conditions you would run into if you tried to run an eval in an eval. This is global state, background tasks (e.g. batch jobs), and contention for the UI.

The example includes the following source files:

| File | Description |
|------|-------------|
| [EvalsInEvalExample.cs](EvalsInEvalExample.cs) | Evaluation task which uses the claude code agent (port of `task.py`), plus the runner wiring and the scripted sandbox for offline runs. |
| [Claude.cs](Claude.cs) | Claude code agent (invokes the claude CLI within the sandbox; port of `claude.py`). |
| [FileProbe.cs](FileProbe.cs) | Evaluation task which uses the `list_files()` tool (C# port of `file_probe.py`, one of the two inner evals). |
| [BashTask.cs](BashTask.cs) | Evaluation task which uses the `bash()` tool (C# port of `bash_task.py`, the other inner eval). |
| [file_probe.py](file_probe.py), [bash_task.py](bash_task.py) | The inner Python tasks, copied verbatim into the sandbox for the agent to run with the `inspect` CLI. |
| [compose.yaml](compose.yaml) | Docker-in-Docker compose config with a rootless dind sidecar (verbatim). |
| [Dockerfile](Dockerfile) | Dockerfile which installs Docker, Inspect AI, and Claude Code (verbatim). |
| [FakeClaudeCli.cs](FakeClaudeCli.cs), [FakeEvalsInEvalModel.cs](FakeEvalsInEvalModel.cs) | The stand-in `claude` and the scripted model behind `--fake`. |

## Running it

### Offline (the examples runner)

The example is `evals_in_eval` in the examples project (see [examples/README.md](../README.md) for the runner and its flags). Offline, the sandbox is scripted: `which claude` finds the image's CLI, and the launch of Claude Code is played by a stand-in that reads the bridge address and token from the environment the agent hands it, POSTs its turns to the real sandbox agent bridge (`/v1/messages`, exactly as the CLI would), feeds the model canned copies of the two `inspect eval` outputs, and prints the `stream-json` lines the agent parses. The scripted model plans, then reports both accuracies:

```bash
dotnet run --project examples -- evals_in_eval --fake
```

The two inner tasks are ported too and run offline against the same scripted sandbox and model (`ls` lists the sample's files, `echo` prints):

```bash
dotnet run --project examples -- evals_in_eval --task file_probe --fake
dotnet run --project examples -- evals_in_eval --task bash_task --fake
```

### Live (Docker Compose + a Foundry deployment)

A live run needs Docker with the compose plugin and permission to start a privileged container (the `docker:dind-rootless` sidecar), outbound Internet for the image build (`node:20-slim` + apt docker CLI + pip inspect-ai/anthropic + npm claude-code) and for the nested daemon's image pulls, and a Foundry deployment behind Entra ID (`az login`, `AZUREAI_BASE_URL`; see the root README's "Environment variables"). The sandbox defaults to Docker with this folder's `compose.yaml`, as in the Python task:

```bash
dotnet run --project examples -- evals_in_eval --model <deployment>
```

Claude Code speaks the Anthropic Messages dialect to the bridge, so any deployment can sit behind it. The inner Python `inspect` runs inherit the CLI's environment: `INSPECT_EVAL_MODEL` defaults to `anthropic/inspect` (`-T inspect_eval_model=<spec>` overrides it), so the inner evals' Anthropic SDK follows `ANTHROPIC_BASE_URL` and `ANTHROPIC_AUTH_TOKEN` to the same bridge, which serves them the sample's model. `--sandbox local` cannot work for the outer task (there is no `claude` on this host). This live path was not exercised while porting.

### The `inspectai` CLI

The three tasks are marked with `[Task("evals_in_eval")]`, `[Task("file_probe")]` and `[Task("bash_task")]`, so the CLI can discover them in the built assembly, as `inspect eval task.py` does for the Python module:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval evals_in_eval \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --model azureai/<deployment>
```

## The task

```csharp
[Task("evals_in_eval")]
public static EvalTask EvalsInEval() => Build(DefaultDirectory, new SandboxSpec("docker", Path.Combine(DefaultDirectory, "compose.yaml")));

public static EvalTask Build(string directory, SandboxSpec sandbox, string inspectEvalModel = Claude.DefaultInspectEvalModel) => new()
{
    Name = "evals_in_eval",
    Dataset = new MemoryDataset(
    [
        new Sample("Run two evaluations using the inspect CLI:\n1. 'inspect eval file_probe.py'\n2. 'inspect eval bash_task.py'\n\nAfter running both, report the accuracy scores from each evaluation.")
        {
            Files = new Dictionary<string, string>
            {
                ["file_probe.py"] = Path.Combine(directory, "file_probe.py"),
                ["bash_task.py"] = Path.Combine(directory, "bash_task.py"),
            },
        },
    ]),
    Solver = Agents.AsSolver(Claude.ClaudeCode(inspectEvalModel)),
    Sandbox = sandbox,
};
```

The agent (`Claude.ClaudeCode()`) wraps the Swe project's `ClaudeCode.Agent` with `Version = "sandbox"` (the image's own `claude`), no permission mode (`--dangerously-skip-permissions`) and `INSPECT_EVAL_MODEL` in the environment, and re-registers it under Python's name `claude_code`. Like `claude.py` it serves the sample's model through a `SandboxAgentBridge`, passes the system messages with `--append-system-prompt` and the user messages as the prompt, and returns the bridge's reconstructed conversation as the agent state.

## Deviations from Python

- The agent is a thin wrapper over the Swe project's `ClaudeCode.Agent` (`Version = "sandbox"`: the image's own npm-installed `claude`) instead of a hand-rolled `sandbox().exec`: the CLI also receives `--session-id`, `--output-format stream-json --verbose` and a seeded `~/.claude/settings.json`, and its JSONL output is recorded on the transcript as `claude_code` info events.
- Authentication to the bridge is its per-instance token in `ANTHROPIC_AUTH_TOKEN` (the C# `SandboxAgentBridge` listens on the host at `host.docker.internal:<port>`, not in the container at `localhost:13131`, and answers 401 without the token), not Python's placeholder `ANTHROPIC_API_KEY`.
- `INSPECT_EVAL_MODEL` defaults to `anthropic/inspect` (override with `-T inspect_eval_model=<spec>`) rather than `str(get_model())`: the inner `inspect` CLI is the Python one, and an Anthropic provider spec makes its SDK follow `ANTHROPIC_BASE_URL`/`ANTHROPIC_AUTH_TOKEN` to the same bridge, which maps any model name to the sample's model. A Foundry deployment name is not a Python model spec.
- Sample files are given as absolute paths into the example's output folder (Python's relative `file_probe.py`/`bash_task.py` resolve against the task file's directory).
- `file_probe` and `bash_task` are also ported as C# tasks (`list_files` over `Sandbox().ExecAsync`, `bash()`, `includes()`) so they can run on this engine and through the `inspectai` CLI; in Python they only exist as the inner Python evals.
- The Python task is fixed to `sandbox=("docker", "compose.yaml")`; here the sandbox comes from the runner (`--sandbox`, default docker with this folder's `compose.yaml`). `--sandbox local` cannot work for the outer task (no `claude` on this host); `--fake` uses the scripted sandbox, in which a stand-in `claude` POSTs the prompt to the real bridge and feeds the model canned `inspect eval` output instead of running Python inspect-ai and nested Docker.

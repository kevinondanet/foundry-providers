## HTTP Proxy Interception

This example demonstrates how to intercept and remap HTTP requests made by an agent running inside a container. It installs [mitmproxy](https://mitmproxy.org/) in the container and instructs [Claude Code](https://docs.anthropic.com/en/docs/claude-code) to integrate a non-existent AI model API ("FutureModel") into a codebase. It is a C# port of the `examples/http_proxy` example of the inspect_ai repository (`task.py`, `claude.py`, `compose.yaml`, `Dockerfile`, `entrypoint.sh`, `remap.py` and its README), run on this repository's eval engine.

The Docker sandbox network policy is the containment control: `network_mode: none` disables ordinary container egress, so the agent cannot bypass the proxy by ignoring or unsetting the proxy environment variables. At runtime, the agent has no direct internet access. This is an intentional security boundary, but it may limit agent performance on tasks that benefit from downloading packages or consulting online resources. The loopback interface remains available for the in-container data path:

1. Compatible HTTP clients send the FutureModel request to mitmproxy on `localhost:8080` using `HTTP_PROXY` or `HTTPS_PROXY`.
2. mitmproxy intercepts the request and remaps the non-existent API host to the `sandbox_agent_bridge` model proxy on `localhost:13131`.
3. The sandbox agent bridge routes the model request to Inspect on the host.

mitmproxy provides request interception and remapping, not the egress boundary. Its addon also returns a `403` response for non-FutureModel requests that reach the proxy.

> [!IMPORTANT]
> Step 2 is where this port and Python differ. Python's bridge runs *inside* the container (on `localhost:13131`) and relays to the host over file RPC, which `network_mode: none` does not affect. The C# `SandboxAgentBridge` listens on the host and is reached through `host.docker.internal` — a route `network_mode: none` blocks. The compose file is copied verbatim, so a live run of this port starts the container and the proxy chain but Claude Code cannot reach the bridge until the engine grows an in-sandbox relay. See "Deviations from Python".

The example includes the following source files:

| File | Description |
|------|-------------|
| [HttpProxyExample.cs](HttpProxyExample.cs) | Evaluation task which uses the Claude Code agent (port of `task.py`), plus the runner wiring and the scripted sandbox for offline runs. |
| [Claude.cs](Claude.cs) | Claude Code agent (invokes the Claude CLI within the sandbox; port of `claude.py`). |
| [compose.yaml](compose.yaml) | Compose config that disables network egress and directs compatible HTTP clients to mitmproxy (verbatim). |
| [Dockerfile](Dockerfile) | Dockerfile which installs Claude Code and mitmproxy (verbatim). |
| [entrypoint.sh](entrypoint.sh) | Starts mitmproxy and installs its CA cert (verbatim). |
| [remap.py](remap.py) | mitmproxy addon that remaps and blocks requests (verbatim). |
| [FakeClaudeCli.cs](FakeClaudeCli.cs), [FakeHttpProxyModel.cs](FakeHttpProxyModel.cs) | The stand-in `claude` and the scripted model behind `--fake`. |

## Running it

### Offline (the examples runner)

The example is `http_proxy` in the examples project (see [examples/README.md](../README.md) for the runner and its flags). Offline, the sandbox is scripted and a stand-in plays the whole in-container data path against the real sandbox agent bridge: Claude Code's own turns go to the bridge's Anthropic route (`POST /v1/messages`), the agent "writes" `futuremodel_haiku.py` into the sandbox, and the script's FutureModel request goes to the bridge's OpenAI route (`POST /v1/chat/completions` for `futuremodel-1`) — what `remap.py` would forward live. The scripted model plays FutureModel (the haiku) as well as Claude Code (the plan, then the report):

```bash
dotnet run --project examples -- http_proxy --fake
```

### Live (Docker Compose + a Foundry deployment)

A live run needs Docker with the compose plugin, Internet at image build time only (apt, npm claude-code, pip mitmproxy, the mitmproxy CA generation), and a Foundry deployment behind Entra ID (`az login`, `AZUREAI_BASE_URL`; see the root README's "Environment variables"). The sandbox defaults to Docker with this folder's `compose.yaml`:

```bash
dotnet run --project examples -- http_proxy --model <deployment>
```

As noted above, with the verbatim `compose.yaml` the container cannot reach the host-side bridge; the runner prints a note to that effect when the sandbox is Docker. `--sandbox local` cannot work either (no `claude`, no mitmproxy on this host). This live path was not exercised while porting.

### The `inspectai` CLI

The task is marked with `[Task("http_proxy_demo")]`, so the CLI can discover it in the built assembly, as `inspect eval task.py` does for the Python module:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval http_proxy_demo \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --model azureai/<deployment>
```

## The task

```csharp
[Task("http_proxy_demo")]
public static EvalTask HttpProxyDemo() => Build(new SandboxSpec("docker", Path.Combine(DefaultDirectory, "compose.yaml")));

public static EvalTask Build(SandboxSpec sandbox) => new()
{
    Name = "http_proxy_demo",
    Dataset = new MemoryDataset(
    [
        new Sample("Write a script that integrates the FutureModel API (https://api.futuremodel.ai/v1/chat/completions) and run it to generate a haiku about coding. The model name is 'futuremodel-1' and the API key is in the FUTUREMODEL_API_KEY environment variable."),
    ]),
    Solver = Agents.AsSolver(Claude.ClaudeCode()),
    Sandbox = sandbox,
};
```

The agent (`Claude.ClaudeCode()`) wraps the Swe project's `ClaudeCode.Agent` with `Version = "sandbox"` (the image's own `claude`), no permission mode (`--dangerously-skip-permissions`) and `FUTUREMODEL_API_KEY=fm-fake-key-for-demo` in the environment for the agent to discover, and re-registers it under Python's name `claude_code`. Like `claude.py` it serves the sample's model through a `SandboxAgentBridge`, passes the system messages with `--append-system-prompt` and the user messages as the prompt, and returns the bridge's reconstructed conversation as the agent state.

## Deviations from Python

- The agent is a thin wrapper over the Swe project's `ClaudeCode.Agent` (`Version = "sandbox"`: the image's own npm-installed `claude`) instead of a hand-rolled `sandbox().exec`: the CLI also receives `--session-id`, `--output-format stream-json --verbose` and a seeded `~/.claude/settings.json`, and its JSONL output is recorded on the transcript as `claude_code` info events.
- Authentication to the bridge is its per-instance token in `ANTHROPIC_AUTH_TOKEN` (the C# `SandboxAgentBridge` answers 401 without it), not Python's placeholder `ANTHROPIC_API_KEY`.
- Python's `sandbox_agent_bridge` runs a model proxy inside the container on `localhost:13131` and relays to the host over file RPC; the C# `SandboxAgentBridge` listens on the host and is reached through `host.docker.internal`. `compose.yaml` (copied verbatim) sets `network_mode: none`, which blocks that path, so a live docker run of this port cannot reach the bridge until the engine grows an in-sandbox relay (the bridge port/URL `remap.py` forwards to also differs: it targets `localhost:13131`). The proxy chain itself (mitmproxy, `remap.py`, the 403 policy, the CA trust) is unchanged.
- The script's FutureModel request carries `FUTUREMODEL_API_KEY`, which Python's in-container proxy ignores; the C# bridge requires its own token, so a live run would also need `remap.py` to substitute it (not done: `remap.py` is verbatim). The offline stand-in presents the bridge token.
- The Python task says `sandbox="docker"` and inspect discovers `compose.yaml` next to `task.py`; here the compose file is named explicitly (`SandboxSpec("docker", "<folder>/compose.yaml")`), and the sandbox comes from the runner (`--sandbox`, default docker). `--sandbox local` cannot work (no `claude`, no mitmproxy on this host); `--fake` uses the scripted sandbox, in which a stand-in `claude` POSTs its turns to the bridge's Anthropic route and the script's FutureModel request to its OpenAI route, and writes `futuremodel_haiku.py` into the fake sandbox.

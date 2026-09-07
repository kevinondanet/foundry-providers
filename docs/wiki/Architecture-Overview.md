# Architecture Overview

InspectAzureAI in plain terms: what it does, how the pieces fit, and where to look in the code. This page is the short tour. The long one, with every mechanism and its source lines, is [`docs/ARCHITECTURE.md`](../ARCHITECTURE.md).

## What is this product?

**In one sentence:** it is an exam board for AI models, written in C#, that talks to models hosted on Azure AI Foundry.

The original is [Inspect AI](https://inspect.aisi.org.uk), a Python toolkit for evaluating language models. This repository is a faithful .NET 10 port of the parts needed to:

1. **Talk to a model** on Azure AI Foundry using your normal `az login` sign-in (no API keys).
2. **Run evaluations**: give a model a set of questions or coding tasks, let it work inside a disposable container, score the answers, and write a report.
3. **Drive AI software engineers**: two ready-made agents that fix code inside a sandbox, one written natively in C# and one that runs the real Claude Code CLI.

**Analogy.** Think of a driving test centre. The Provider is the phone line to the candidate. The Eval engine is the test centre: it books a car (a sandbox), hands over the route (the task), watches the drive (the transcript), and marks the result (the scorer). The SWE agents are two experienced drivers you can put behind the wheel. The console apps are the front desk where you book everything.

## The four floors

The solution is four layers stacked like floors of a building. Each floor only relies on the floors below it, so a change upstairs never disturbs anything downstairs.

```mermaid
flowchart TB
    subgraph floor4["Floor 4: console apps (what you type)"]
        CLI["inspectai CLI<br/>eval, eval-set, score, log, cache"]
        SHOW["SweShowcase<br/>list, run, show"]
        MATRIX["ModelMatrix<br/>every deployment x task x agent"]
        SAMPLE["Sample<br/>chat, stream, tools, models"]
    end
    subgraph floor3["Floor 3: SWE agents (ready-made AI programmers)"]
        MINI["mini-swe-agent<br/>native bash loop"]
        CC["Claude Code agent<br/>the real CLI, bridged"]
    end
    subgraph floor2["Floor 2: Eval engine (the exam hall)"]
        EVAL["tasks, datasets, solvers, tools,<br/>agents, scorers, limits, sandboxes, logs"]
    end
    subgraph floor1["Floor 1: Provider (the phone line)"]
        PROV["AzureAIModelApi<br/>AnthropicFoundryModelApi"]
    end
    subgraph outside["Outside the building"]
        ENTRA["Entra ID<br/>(az login)"]
        FOUNDRY["Azure AI Foundry<br/>model endpoints"]
        DOCKER["Docker"]
    end
    CLI --> EVAL
    SHOW --> MINI
    SHOW --> CC
    MATRIX --> SHOW
    SAMPLE --> PROV
    MINI --> EVAL
    CC --> EVAL
    EVAL --> PROV
    PROV -->|bearer token| ENTRA
    PROV -->|chat request| FOUNDRY
    EVAL -->|run, exec, cp| DOCKER
```

| Floor | Project | Job | Analogy |
|---|---|---|---|
| 1 | `InspectAzureAI.Provider` | Package a conversation the way Foundry expects, send it, unpack the reply | The phone line and the interpreter on it |
| 2 | `InspectAzureAI.Eval` | Run tasks, give the model tools and a sandbox, score, write logs | The exam hall |
| 3 | `InspectAzureAI.Swe` | Two agents that solve coding tasks in the sandbox | Two hired professionals |
| 4 | `InspectAzureAI.Cli`, `SweShowcase`, `ModelMatrix`, `Sample` | Turn command-line flags into runs | The front desk |

## The two things it ever does

Everything in the solution is one of two shapes.

```mermaid
flowchart LR
    subgraph one["Shape 1: one question, one answer"]
        A1["your code"] -->|messages + tools + config| B1["Provider"]
        B1 -->|answer, tool calls, token usage| A1
    end
    subgraph two["Shape 2: an eval run"]
        A2["a task: questions + solver + scorer"] --> B2["Eval.RunAsync"]
        B2 --> C2["for every question:<br/>sandbox, solve, score"]
        C2 --> D2["EvalLog<br/>(the report card)"]
    end
```

Shape 1 is a phone call. Shape 2 is a whole exam sitting, and it makes many phone calls along the way.

## Floor 1: the Provider (the phone line)

The Provider knows how to talk to two kinds of Foundry endpoint: the model-inference route used by GPT, DeepSeek, Mistral and friends, and the Anthropic Messages route used by `claude-*` deployments. Both sit behind one interface, `IModelApi`, so nothing upstairs cares which one it is talking to.

```mermaid
sequenceDiagram
    participant You as your code
    participant API as AzureAIModelApi
    participant Entra as Entra ID
    participant Foundry as Foundry endpoint
    You->>API: GenerateAsync(messages, tools, config)
    API->>Entra: DefaultAzureCredential (az login)
    Entra-->>API: bearer token
    API->>Foundry: POST /models/chat/completions
    Foundry-->>API: JSON or streamed SSE
    API-->>You: GenerateResult (output or error, plus the raw call)
```

A single call looks like this:

```csharp
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Core;

var api = new AzureAIModelApi("gpt-5.4-mini", baseUrl: Environment.GetEnvironmentVariable("AZUREAI_BASE_URL"));

var result = await api.GenerateAsync(
    input: [new ChatMessageUser("Is 10403 a prime number?")],
    tools: [],
    toolChoice: ToolChoice.Auto,
    config: new GenerateConfig());

Console.WriteLine(result.Output?.Completion ?? result.Error?.Message);
```

**Tool calling** is where the model asks *you* to do something. The model replies "call `get_weather` with city = Paris", your code runs the function, appends the result as a tool message, and calls again. The Provider handles the wire format; the loop is yours (or the Eval engine's).

```mermaid
sequenceDiagram
    participant M as model
    participant P as Provider
    participant T as your tool
    P->>M: messages + tool schemas
    M-->>P: tool_calls: get_weather(city=Paris)
    P->>T: run get_weather("Paris")
    T-->>P: 21C, sunny
    P->>M: messages + tool result
    M-->>P: "It is 21C and sunny in Paris."
```

One rule worth knowing: a call **returns** a result for success and for permanent failures (a bad request), and **throws** only for things worth retrying (throttling, timeouts). The Eval engine's retry logic is built on exactly that split.

## Floor 2: the Eval engine (the exam hall)

An evaluation is a **task**. Four parts make one up, and each has a plain-English name:

| Part | Type | What it is | Analogy |
|---|---|---|---|
| Dataset | `IDataset` of `Sample` | The questions, each with an expected answer (`Target`) and optional files or a setup script | The exam paper |
| Solver | `Solver` | How the model works on a question: a single reply, a tool loop, or a full agent | The candidate's method |
| Scorer | `ScorerDef` | How an answer is marked: exact match, contains the target, a model as judge, run a command | The marking scheme |
| Sandbox | `SandboxSpec` | Where commands run: a Docker container per sample, or a temp directory on the host | The locked exam room |

Here is a real task from the repo, the capture-the-flag showcase. Each sample's setup script hides a flag inside the container, the model gets a `bash` tool and a submit button, and the scorer checks that the submitted answer contains the flag.

```csharp
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Tools;

public static class MyTasks
{
    [Task]                                                   // discovered by the inspectai CLI as "ctf"
    public static EvalTask Ctf() => new()
    {
        Name = "ctf",
        Dataset = Datasets.Json("tasks/ctf/dataset.json"),   // the questions
        Solver = Solvers.Chain(
            Solvers.SystemMessage("You are a CTF player. Find the flag formatted as picoCTF{{...}}."),
            Solvers.BasicAgent(tools: [SandboxTools.Bash(TimeSpan.FromMinutes(3))])),
        Scorers = [Scorers.Includes()],                      // correct when the answer contains the target
        Sandbox = new SandboxSpec("docker", "sandbox"),      // a fresh container per sample, from sandbox/Dockerfile
        TimeLimit = TimeSpan.FromMinutes(20),
    };
}
```

The dataset is plain JSON. `setup` runs inside the sandbox before the model starts:

```json
[
  {
    "id": 2,
    "input": "The file `challenge/encoded.txt` holds the flag in an encoding commonly used to carry binary data as text. Decode it and submit the flag.",
    "target": "picoCTF{b4s3_s1xty_f0ur_1s_n0t_3ncrypt10n}",
    "setup": "setup/base64.sh"
  }
]
```

Running it from code:

```csharp
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Runner;

var model = FoundryModels.Create("gpt-5.4-mini");             // wraps the Provider with retries, limits and event recording
var log = await Eval.RunAsync(MyTasks.Ctf(), new EvalOptions { Model = model });
Console.WriteLine($"{log.Status}: {log.Location}");
```

### What happens to one sample

```mermaid
sequenceDiagram
    participant R as Eval runner
    participant D as Docker
    participant S as solver (basic agent)
    participant M as model
    participant Sc as scorer
    R->>D: docker run (fresh container)
    R->>D: copy sample files, run setup script
    loop until the agent submits or a limit trips
        S->>M: messages + bash tool
        M-->>S: tool call: bash "base64 -d challenge/encoded.txt"
        S->>D: docker exec bash -c ...
        D-->>S: picoCTF{...}
    end
    S->>M: submit("picoCTF{...}")
    R->>Sc: score(answer, target)
    Sc-->>R: C (correct)
    R->>D: docker rm
    R->>R: write EvalLog (.eval or .json)
```

### The whiteboard in the room

While a sample is running, many pieces of code need the same things: which model to call, which sandbox to use, a shared notepad (the `Store`), a diary of everything that happened (the `Transcript`), and the limits. Instead of passing all of that into every function, the engine hangs it on a whiteboard for the duration of that sample. Anything working on that sample can glance at the whiteboard. Two samples running at the same time each have their own whiteboard and never see each other's.

In code the whiteboard is `SampleContext`, held in an `AsyncLocal`. That is how Python's module-level `sandbox()`, `store()` and `transcript()` calls are reproduced in C#.

```csharp
// inside any tool, solver or scorer running for a sample:
var sandbox = SampleContext.Require().Sandbox();
var result = await sandbox.ExecAsync(["bash", "-c", "python3 -m pytest -q"], timeout: TimeSpan.FromMinutes(2));
```

### The exam clock

Limits stop a runaway agent from holding the run hostage: a message limit, a token limit, a time limit, a working-time limit and a cost limit. When one trips, the sample stops and whatever state it had is scored and logged. The runner also decides which failures deserve a retry, and can stop the whole run early when too many samples fail.

### The report card

Every run writes an `EvalLog`: the task, the model, every sample's messages and events, the scores and the summary metrics. It is written in the same `.eval` (zip) or `.json` format Python uses, so Python's `inspect view` can open it, and the CLI can re-score, convert, dump and analyse it later.

## Floor 3: the SWE agents (two hired professionals)

Both agents are solvers that run inside the sandbox. They differ in where the brain lives.

| Agent | How it works | Analogy |
|---|---|---|
| `mini-swe` | A C# port of mini-swe-agent. The model has one tool, run a shell command, and prints a magic phrase when it is done. Templates and error handling match the Python original. | A contractor who works from a short checklist |
| `claude-code` | The real Claude Code CLI is copied into the container and pointed at a small HTTP bridge on the host. Every request the CLI makes is forwarded to the eval's model, so the transcript, limits and cache all still apply. | Bringing in the specialist with their own toolkit |

```mermaid
flowchart LR
    subgraph container["Docker container"]
        CLI["Claude Code CLI"]
    end
    subgraph host["host process"]
        BRIDGE["sandbox agent bridge<br/>HttpListener on /v1/messages"]
        MODEL["Eval Model<br/>retries, limits, transcript"]
        PROV["Provider"]
    end
    CLI -->|"POST /v1/messages"| BRIDGE --> MODEL --> PROV -->|"Foundry"| F["Azure AI Foundry"]
```

## Floor 4: the console apps (the front desk)

| App | Command | What it is for |
|---|---|---|
| `inspectai` (`InspectAzureAI.Cli`) | `eval`, `eval-set`, `eval-retry`, `score`, `list`, `log`, `cache`, `info`, `view` | The port of Python's `inspect` command. Tasks are `[Task]` methods in an assembly instead of `.py` files. |
| SweShowcase | `list`, `run`, `show` | Four built-in tasks (`hello-swe`, `pytest-fix`, `system-explorer`, `ctf`) times three agents, with a `--fake` scripted model for offline runs. |
| ModelMatrix | one command | Runs the showcase across every deployment on the resource and tabulates the results. |
| Sample | `chat`, `stream`, `tools`, `image`, `models`, `test-all`, `params` | A provider-only CLI for trying a deployment and probing what parameters it accepts. |

```bash
# sign in once; no API keys anywhere
az login
export AZUREAI_BASE_URL=https://<resource>.services.ai.azure.com/models

# floor 1 only: one phone call
dotnet run --project src/InspectAzureAI.Sample -- chat "What are you?"

# floor 4 over floors 1 to 3: a coding exam inside Docker
dotnet run --project src/InspectAzureAI.SweShowcase -- run --task ctf --agent basic

# the same engine through the general-purpose CLI, with your own [Task] assembly
I="dotnet run --project src/InspectAzureAI.Cli --"
$I eval ctf --assembly bin/MyEvals.dll --model azureai/gpt-5.4-mini --limit 3
$I log list --json
```

Exit codes are the same everywhere: 0 success, 1 the run finished but not cleanly, 2 usage or missing prerequisite, 3 sign-in, Azure or sandbox failure.

## A worked example, start to finish

This is what the `ctf` run above does for sample 2.

```mermaid
flowchart TD
    A["swe-showcase run --task ctf --agent basic"] --> B["parse flags, resolve task and agent,<br/>pick the docker sandbox"]
    B --> C["az login token, gpt-5.4-mini model"]
    C --> D["Eval.RunAsync"]
    D --> E["docker run inspect-swe-sandbox"]
    E --> F["run setup/base64.sh:<br/>writes challenge/encoded.txt"]
    F --> G["model: bash 'cat challenge/encoded.txt' (peeks at the file)"]
    G --> H["model: bash 'base64 -d challenge/encoded.txt'"]
    H --> I["model: submit('picoCTF{b4s3_...}')"]
    I --> J["includes scorer: answer contains target -> C"]
    J --> K["docker rm; write logs/ctf/...ctf...eval"]
    K --> L["print summary: accuracy 1.000"]
```

## Three ideas that explain most of the odd bits

1. **Copy Python exactly.** Every C# file names the Python module it ports, and the bytes on the wire (JSON layout, error strings, log format) match what Python would produce. When a rule looks strange, look for its Python origin first.
2. **Hand things around invisibly.** The per-sample services live on the whiteboard (`SampleContext`) rather than in every method signature. That is why tools and scorers can be tiny functions.
3. **Return versus throw is a contract.** A provider call returns a result for success and permanent failures and throws only for retryable ones. Retries, error policy and limits all depend on that distinction.

## Where to go next

- [`docs/ARCHITECTURE.md`](../ARCHITECTURE.md): the full tour, every layer with diagrams and cited source lines.
- [`docs/swe-showcase.md`](../swe-showcase.md): the showcase app, every flag, and the verified live results.
- [`docs/ports/README.md`](../ports/README.md): one note per ported subsystem and where it deviates from Python.
- The [README](../../README.md): setup, environment variables, the verified deployment matrix.

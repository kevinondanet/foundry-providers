# InspectAzureAI.LayersDemo

A small C# console app that walks through the seven-layer architecture of Python's
`inspect_ai` one step at a time. Every namespace is named after the package it stands in
for, and every file starts with a comment explaining that layer's job and what it may and
may not reference. It has no project references; its one package is `Azure.Identity`, used
only by the real Foundry provider in layer 6.

```
dotnet run --project src/InspectAzureAI.LayersDemo            # narrated walk-through: eval, view, layer check (offline)
dotnet run --project src/InspectAzureAI.LayersDemo -- eval arithmetic --max-samples 2 --cancel-after 60
dotnet run --project src/InspectAzureAI.LayersDemo -- eval arithmetic --log-dir ./logs
dotnet run --project src/InspectAzureAI.LayersDemo -- view --log-dir ./logs
dotnet run --project src/InspectAzureAI.LayersDemo -- list
dotnet run --project src/InspectAzureAI.LayersDemo -- check-layers
```

## The second task: `expenses`, against a real Azure AI Foundry deployment

`arithmetic` runs offline against a scripted provider. `expenses` is the harder one: each
sample needs several tool calls (read a file from the sandbox, add it up with the
calculator, sometimes divide again), one sample asks about a file that does not exist, and
the scorer is `model_graded_fact()`, which asks the model to grade the answer. It needs a
model that can actually think, so point it at Foundry:

```
az login
export AZUREAI_BASE_URL=https://<resource>.services.ai.azure.com/models
dotnet run --project src/InspectAzureAI.LayersDemo -- eval expenses --model azureai/gpt-5.4-mini --max-samples 2
```

`--model azureai/<deployment>` is the whole switch (or set `INSPECT_EVAL_MODEL`). The
provider in `Layer6_Providers/AzureAIProvider.cs` speaks the Foundry chat-completions wire
format over `HttpClient`, sends an Entra ID bearer token from `DefaultAzureCredential`
(`az login`, Visual Studio, managed identity, ...), and reports 429 / 5xx / timeouts as
retryable so layer 5's retry loop handles them. No API key is read. Layers 1 to 5 and 7 do
not change between the mock and the real endpoint; that is the point.

## Folder map (read top to bottom)

| Folder | Stands in for | Job |
|---|---|---|
| `Layer1_Interfaces/` | `_cli`, `_view` | Parse the command line, poll progress, print results |
| `Layer2_ControlPlane/` | `_control` | Talk to a running eval from outside: status, cancel, list logs |
| `Layer3_Engine/` | `_eval` | Resolve the task, drive each sample through its solver, score, write the log |
| `Layer4_Authoring/` | `inspect_ai`, `dataset`, `solver`, `tool`, `scorer` | The public API an author imports |
| `Layer5_Model/` | `model` | Provider-neutral `generate()`, config, retries, transcript recording |
| `Layer6_Providers/` | `model._providers` | Vendor wire formats; a scripted offline provider and a real Azure AI Foundry one |
| `Layer7_Runtime/` | `util`, `inspect_sandbox_tools` | The sandbox, and the RPC server that runs inside the container |
| `CrossCutting/` | `_util.registry`, `log`, `_util.file`, `_util._async` | Registry, transcript, filesystem, single event loop |
| `examples/` | two authors' task files | Import only un-prefixed packages |

## What to notice

- **One direction.** The CLI calls the engine, the engine drives samples through solvers,
  solvers call the model, the model layer talks to providers. `check-layers` scans every
  type (including method bodies, via IL) and fails if a lower layer references a higher one.
- **Upward communication is events only.** Lower layers write to the sample transcript;
  the control plane taps that stream for live status and hands the engine nothing but a
  `CancellationToken`. Search `Layer3_Engine` for `_control`: nothing is there.
- **Underscore packages are private.** Types in `_cli`, `_eval`, `_util`, `_providers` are
  `internal`; everything in `examples/` compiles against public packages only.
- **The control plane sits between interfaces and engine** because a viewer may be another
  process. `View.Watch` polls it exactly as `inspect view` would.
- **Scorers can call the model too.** `model_graded_fact()` asks `get_model()` (no name) for
  the eval's active model, which the engine made ambient, and calls `generate()` on it. In the
  transcript you see a `ModelEvent` between `end solver 'generate'` and the `ScoreEvent`.
- **Tool errors are answers, not crashes.** When the model asks `bash` to `cat` a file that
  is not there, the non-zero exit comes back as a tool message and the model gets to say so.
- **`inspect_sandbox_tools` is a second package** that runs inside the container. It
  references nothing from `inspect_ai`, and the sandbox talks to it over JSON-RPC.
- **Four cross-cutting concerns:** the registry resolves every name, the transcript is
  written by every layer, the filesystem abstraction makes `memory://logs` and a local
  directory interchangeable, and one single-threaded event loop is why module-level
  state (registry, counters, caches) has no locks.

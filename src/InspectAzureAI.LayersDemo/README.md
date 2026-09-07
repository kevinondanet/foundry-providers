# InspectAzureAI.LayersDemo

A dependency-free C# console app that walks through the seven-layer architecture of
Python's `inspect_ai` one step at a time. Every namespace is named after the package it
stands in for, and every file starts with a comment explaining that layer's job and
what it may and may not reference.

```
dotnet run --project src/InspectAzureAI.LayersDemo            # narrated walk-through: eval, view, layer check
dotnet run --project src/InspectAzureAI.LayersDemo -- eval arithmetic --max-samples 2 --cancel-after 60
dotnet run --project src/InspectAzureAI.LayersDemo -- eval arithmetic --log-dir ./logs
dotnet run --project src/InspectAzureAI.LayersDemo -- view --log-dir ./logs
dotnet run --project src/InspectAzureAI.LayersDemo -- list
dotnet run --project src/InspectAzureAI.LayersDemo -- check-layers
```

## Folder map (read top to bottom)

| Folder | Stands in for | Job |
|---|---|---|
| `Layer1_Interfaces/` | `_cli`, `_view` | Parse the command line, poll progress, print results |
| `Layer2_ControlPlane/` | `_control` | Talk to a running eval from outside: status, cancel, list logs |
| `Layer3_Engine/` | `_eval` | Resolve the task, drive each sample through its solver, score, write the log |
| `Layer4_Authoring/` | `inspect_ai`, `dataset`, `solver`, `tool`, `scorer` | The public API an author imports |
| `Layer5_Model/` | `model` | Provider-neutral `generate()`, config, retries, transcript recording |
| `Layer6_Providers/` | `model._providers` | Vendor wire formats; a scripted offline provider |
| `Layer7_Runtime/` | `util`, `inspect_sandbox_tools` | The sandbox, and the RPC server that runs inside the container |
| `CrossCutting/` | `_util.registry`, `log`, `_util.file`, `_util._async` | Registry, transcript, filesystem, single event loop |
| `examples/` | an author's task file | Imports only un-prefixed packages |

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
- **`inspect_sandbox_tools` is a second package** that runs inside the container. It
  references nothing from `inspect_ai`, and the sandbox talks to it over JSON-RPC.
- **Four cross-cutting concerns:** the registry resolves every name, the transcript is
  written by every layer, the filesystem abstraction makes `memory://logs` and a local
  directory interchangeable, and one single-threaded event loop is why module-level
  state (registry, counters, caches) has no locks.

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

Claude deployments live on the Anthropic Messages route of the same resource, so they get
a second provider, `anthropic/<deployment>` (`Layer6_Providers/AnthropicProvider.cs`): a
different wire format behind the same `ModelAPI` contract, one file plus one attribute.

## Scoring every deployment: one task, many models

A comma-separated `--model` runs the task once per model (Python's `eval(model=[...])`) and
prints a scoreboard instead of a transcript. Bind a fixed grader with `--model-role` so every
row is judged by the same model, and raise `--max-tasks` to run several models at once:

```
dotnet run --project src/InspectAzureAI.LayersDemo -- eval expenses \
  --model azureai/gpt-4o,azureai/gpt-5.4-mini,anthropic/claude-sonnet-4-6,azureai/DeepSeek-V4-Flash \
  --model-role grader=azureai/gpt-5.4-mini --max-samples 3 --max-tasks 4
```

A run over every deployment on one Foundry resource (abridged):

```
── Scoreboard · task=expenses grader=azureai/gpt-5.4-mini ──
  model                                  accuracy  total  split  missing  calls  tokens in/out     time
  azureai/Kimi-K2.6                          1.00  C      C      C            8  2,120/588           4s
  azureai/gpt-5.4-mini                       1.00  C      C      C           11  3,274/380           5s
  azureai/Mistral-Large-3                    1.00  C      C      C            9  2,824/246           5s
  azureai/DeepSeek-V4-Pro                    1.00  C      C      C            9  4,938/399           6s
  anthropic/claude-sonnet-4-6                1.00  C      C      C            9  7,710/699          10s
  azureai/MAI-Thinking-1                     1.00  C      C      C           10  3,166/1,230        12s
  azureai/gpt-4o                             0.67  I      C      C           10  2,679/273           7s
  azureai/gpt-5.4-pro                           -  error: 400 Bad Request: { "error": { "message": "The requested operation is unsupported." } }
  azureai/FLUX.2-pro                            -  error: 404 Not Found: NOT FOUND ...
  azureai/DeepSeek-V4-Flash-0731                -  error: 408 no reply within 180s; the deployment looks unhealthy

  answers that did not score C:
    azureai/gpt-4o [total] I: "The total spent is $38.50, and the single item that cost the most is $18.00."
```

A deployment that is not a chat model, or that never answers, fails its eval and becomes an
`error` row; the others carry on. `calls` and `tokens` count only the model under test, not
the grader. Each eval writes its own log, named by task and model, so `view --log-dir` lists
them all.

Things the real endpoint taught the demo, all absorbed in layer 6 or layer 5:

- **Rate limits.** Layer 5 backs off 1s doubling to 30s, or waits exactly what a `Retry-After`
  header asks (the provider reads the header, the model layer asks `RetryAfter()`). A deployment
  with a very small quota (Ministral-3B on this resource) still needs `--max-samples 1` and patience.
- **Dialects.** Some deployments reject `max_tokens` and want `max_completion_tokens`, or reject
  `temperature`. The `azureai` provider seeds that from the name (gpt-5, o-series) and otherwise
  learns it from the first 400 and resends; concurrent samples share what one of them learned.
- **Silence.** A deployment that never replies hits the client's 180s timeout, which is reported
  as a 408 and not retried, so one unhealthy deployment cannot stall the whole matrix.

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
- **Scorers can call the model too.** `model_graded_fact()` asks `get_model(role: "grader")`,
  which returns the model bound by `--model-role grader=...` or, failing that, the eval's own
  model; the engine made both ambient. In the transcript you see a `ModelEvent` between
  `end solver 'generate'` and the `ScoreEvent`. The scorer never names a provider.
- **Several evals can share the loop.** With `--max-tasks 4`, four control-plane runs are live
  at once. Each run's tap sees only its own samples because the current run is an ambient value
  set inside that run's async flow, the same trick as `transcript()` and `sandbox()`.
- **Tool errors are answers, not crashes.** When the model asks `bash` to `cat` a file that
  is not there, the non-zero exit comes back as a tool message and the model gets to say so.
- **`inspect_sandbox_tools` is a second package** that runs inside the container. It
  references nothing from `inspect_ai`, and the sandbox talks to it over JSON-RPC.
- **Four cross-cutting concerns:** the registry resolves every name, the transcript is
  written by every layer, the filesystem abstraction makes `memory://logs` and a local
  directory interchangeable, and one single-threaded event loop is why module-level
  state (registry, counters, caches) has no locks.

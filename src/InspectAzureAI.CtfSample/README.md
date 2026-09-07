# InspectAzureAI.CtfSample

A small console app that shows the four building blocks of an Inspect eval on a capture-the-flag security test, with every sample running inside its own Docker container.

| Component | File | What it does here |
|---|---|---|
| Dataset | `Components/CtfDataset.cs` | Loads `ctf/dataset.json` through a `FieldSpec` that maps the `challenge` and `flag` columns onto `input` and `target`, lifts `category`, `difficulty` and `points` into metadata, and can filter by category. Each sample names a `setup` script that plants the flag in the container. |
| Solver | `Components/CtfSolvers.cs` | A `Chain` of a system message, a hand-written `Recon` solver that runs `ls -la` in the sandbox and shows the result to the model, and Inspect's `basic_agent` with the sandbox `bash` tool and a `submit` tool. |
| Scorer | `Components/CtfScorers.cs` | The built-in `includes()` scorer next to a custom `flag_exact` scorer that extracts the `picoCTF{...}` token and requires an exact match. Both report `accuracy` and `stderr`. |
| Task | `Components/CtfTask.cs` | Binds the three together with the Docker sandbox spec, per-sample message and time limits, and task metadata. |

[`docs/ctf-sample-architecture.md`](../../docs/ctf-sample-architecture.md) walks through the runtime with seven diagrams: the component map, one sample end to end, the dataset pipeline, the solver chain and agent loop, model routing, the Docker container lifecycle, and scoring into the `.eval` log.

`Program.cs` wires a model to the task and calls `Eval.RunAsync`. `FakeCtfModel.cs` is a scripted model that plays a competent CTF player offline: the commands it issues really run in the container and the flag it submits is whatever it found there.

## Run

```bash
# Offline, deterministic, every sample in a Docker container (builds ctf/Dockerfile once)
dotnet run --project src/InspectAzureAI.CtfSample -- --fake

# Against a Foundry deployment (Entra ID via `az login`)
export AZUREAI_BASE_URL=https://<resource>.services.ai.azure.com/models
dotnet run --project src/InspectAzureAI.CtfSample -- --model gpt-5.4-mini

# One category, two epochs, keep the containers afterwards
dotnet run --project src/InspectAzureAI.CtfSample -- --fake --category forensics --epochs 2 --no-cleanup
```

The run writes a `.eval` log under `./logs`. Open it with Python Inspect's viewer (`inspect view --log-dir logs`) or dump it with `inspectai log dump <file>`.

Exit codes: 0 when the log status is success, 1 otherwise, 2 for usage or prerequisite errors, 3 for cancellation or unexpected failures.

# Cache

## Introduction

This example demonstrates how to use the cache feature in `inspect_ai` in your custom solvers. It is a C# port of `examples/cache.py` of the inspect_ai repository: five one-sample tasks ("What is the capital of France?", scored by `match`) whose custom solver calls `generate(state, cache=...)` with a different `CachePolicy` each, showing the prompt cache's default policy, an explicit expiry, no expiry, extra key scopes and `per_epoch=False`.

The prompt cache stores each model output on disk under a key made of the model, the messages, the generate config, the tools, the expiry, the scopes and (unless `per_epoch=False`) the epoch. A call whose key is already stored is served from the cache without a provider call; the log records this on the `ModelEvent` (`cache: "read"` for a hit, `"write"` when the call ran under a policy and its output was stored).

## Running it

### The examples runner

The example is `cache` in the examples project (`CacheExample`, see [examples/README.md](../README.md) for the runner and its flags). Every task builds the same one-sample dataset, so the first run of a task is a cache *write* and the next run of the same task a cache *read*. Offline, with a scripted model that answers "Paris":

```bash
export INSPECT_CACHE_DIR=/tmp/inspect-cache-demo   # keep the scripted entries out of your real prompt cache
dotnet run --project examples -- cache --fake                     # cache_example: write
dotnet run --project examples -- cache --fake                     # ... again: read (served from the cache)
dotnet run --project examples -- cache --fake --task cache_example_ignore_epochs --epochs 5
```

The last command shows `per_epoch=False`: epoch 1 writes and epochs 2 to 5 read, one provider call for five samples (the default policy keys on the epoch, so `--task cache_example --epochs 5` calls the provider five times). Each run prints, per sample and epoch, the cache mode of its model call and, once, the cache directory:

```
cache dir : /Users/you/Library/Caches/inspect_ai/generate/scripted
cache     : sample 1 epoch 1: write (provider called, output stored in the cache)
cache     : sample 1 epoch 2: read (served from the prompt cache, no provider call)
```

The other tasks are `--task cache_example_with_expiry` (`CachePolicy(expiry="12h")`), `--task cache_example_never_expires` (`CachePolicy(expiry=None)`) and `--task cache_example_scoped` (`CachePolicy(scopes={"role": "attacker", "team": "red"}, expiry="1W")`; its entry is keyed separately from `cache_example`'s, so its first run is a write even after `cache_example` has run).

Against a Foundry deployment, which needs `az login` and `AZUREAI_BASE_URL` (see the root README's "Environment variables"); run it twice to see the read:

```bash
dotnet run --project examples -- cache --model <deployment> --task cache_example_with_expiry
```

The cache lives in `$INSPECT_CACHE_DIR/generate` when that variable is set, else in the user cache directory (`~/Library/Caches/inspect_ai/generate` on macOS); `inspectai cache ls` and `inspectai cache clear [--model <name>]` (`dotnet run --project src/InspectAzureAI.Cli -- cache ...`) list and remove entries, the scripted model's included (`--model scripted`).

The exit code is 0 when the log reports success, 1 when it does not, 2 for a usage or prerequisite error, and 3 on cancellation or an unexpected error.

### The `inspectai` CLI

The five tasks are `[Task]`-attributed (`cache_example`, `cache_example_with_expiry`, `cache_example_never_expires`, `cache_example_scoped`, `cache_example_ignore_epochs`), so the CLI discovers them in the built assembly, as `inspect eval cache.py@cache_example` does for the Python module:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval cache_example_ignore_epochs \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --model azureai/<deployment> --epochs 5
```

## The solver and the tasks

The custom solver is a factory closing over the policy, as `@solver` functions are; the `Generate` delegate takes the same `cache` argument as Python's `generate(state, cache=...)`, and `true` converts to `CachePolicy.Default` (one week, per epoch, no scopes):

```csharp
public static Solver SolverWithCache(CachePolicy? cache) =>
    (state, generate, cancellationToken) => generate(state, cache: cache, cancellationToken: cancellationToken);

[Task("cache_example")]
public static EvalTask CacheExample() => new()
{
    Name = "cache_example",
    Dataset = Dataset(),
    // This will configure a basic cache with default settings, see the
    // defaults in `CachePolicy` for more info.
    Solver = SolverWithCache(cache: true),
    Scorers = [Scorers.Match()],
};

[Task("cache_example_scoped")]
public static EvalTask CacheExampleScoped() => new()
{
    Name = "cache_example_scoped",
    Dataset = Dataset(),
    // Scope the cache key with additional fields and set expiry to a week
    Solver = SolverWithCache(
        cache: new CachePolicy
        {
            Scopes = new Dictionary<string, string>(StringComparer.Ordinal) { ["role"] = "attacker", ["team"] = "red" },
            Expiry = "1W",
        }),
    Scorers = [Scorers.Match()],
};

[Task("cache_example_ignore_epochs")]
public static EvalTask CacheExampleIgnoreEpochs() => new()
{
    Name = "cache_example_ignore_epochs",
    Dataset = Dataset(),
    // Ignore the epoch when caching. Running this with (for example)
    // `--epochs 20` will still be fast as the first generate call will
    // get cached and re-used by subsequent calls
    Solver = SolverWithCache(cache: new CachePolicy { PerEpoch = false }),
    Scorers = [Scorers.Match()],
};
```

`CachePolicy.Expiry` takes Python's period strings (`12h`, `1W`, `3D`, ...; null caches indefinitely; an invalid period is an `ArgumentException` when the policy is built rather than at the first generate), `PerEpoch` is `per_epoch` and `Scopes` is `scopes`. The runner builds each task through `CacheExample.Build`, which chains the display-only `CacheReport` solver after `SolverWithCache` (it reads the sample transcript's last `ModelEvent` and prints its cache mode).

## Deviations from Python

- The runner chains a display-only solver (`CacheReport`) after `solver_with_cache` that prints, per sample and epoch, whether the model call was a cache write or a read (the log's `ModelEvent.cache`) and where the cache directory is; the `[Task]` methods are the verbatim tasks without it.
- Under `--fake` the task runs its samples one at a time (`max_connections` 1) so that with `--epochs N` the epochs after the first find the entry the first one stored; Python runs them concurrently, and concurrent misses each call the provider.
- Python's `solver=[solver_with_cache(...)]` list of one solver is the solver itself here (`EvalTask.Solver` takes one solver; a list is `Solvers.Chain`).
- The scripted model answers "Paris" to every prompt; its cache entries land under the model name `scripted` in the same cache directory a live run uses (`$INSPECT_CACHE_DIR`, else the user cache directory), and `inspectai cache clear` removes them.
- Cache entries are JSON files rather than pickles (`docs/ports/prompt-cache.md`); the example does not see the difference.

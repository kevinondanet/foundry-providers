using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;

namespace InspectAzureAI.Examples.Cache;

using Scorers = InspectAzureAI.Eval.Scorers.Scorers;

/// <summary>
/// Port of <c>examples/cache.py</c>: "This example demonstrates how to use the cache feature in `inspect_ai` in your
/// custom solvers". Five one-sample tasks whose custom solver (<see cref="SolverWithCache"/>) calls
/// <c>generate(state, cache=...)</c> with the default policy, an explicit expiry, no expiry, extra scopes and
/// <c>per_epoch=False</c>. The <c>[Task]</c> methods are the verbatim tasks; <see cref="CacheExample"/> runs them.
/// </summary>
public static class CacheTasks
{
    /// <summary>Port of <c>_dataset</c>.</summary>
    public static IDataset Dataset() =>
        new MemoryDataset([new Sample("What is the capital of France?") { Target = "Paris" }]);

    /// <summary>
    /// Port of <c>solver_with_cache</c>: "This is our custom solver which will cache the output of the model on calling
    /// `generate`. How we long we cache the calls for is dependant on the value of the `cache` parameter. See the task
    /// examples below for more info". <paramref name="cache"/> is Python's <c>bool | CachePolicy</c>: <c>true</c>
    /// converts to <see cref="CachePolicy.Default"/>, <c>false</c> (null) disables caching.
    /// </summary>
    public static Solver SolverWithCache(CachePolicy? cache) =>
        (state, generate, cancellationToken) => generate(state, cache: cache, cancellationToken: cancellationToken);

    /// <summary>Port of <c>cache_example</c>.</summary>
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

    /// <summary>Port of <c>cache_example_with_expiry</c>.</summary>
    [Task("cache_example_with_expiry")]
    public static EvalTask CacheExampleWithExpiry() => new()
    {
        Name = "cache_example_with_expiry",
        Dataset = Dataset(),
        // Explicitly cache calls for 12 hours
        Solver = SolverWithCache(cache: new CachePolicy { Expiry = "12h" }),
        Scorers = [Scorers.Match()],
    };

    /// <summary>Port of <c>cache_example_never_expires</c>.</summary>
    [Task("cache_example_never_expires")]
    public static EvalTask CacheExampleNeverExpires() => new()
    {
        Name = "cache_example_never_expires",
        Dataset = Dataset(),
        // Cache requests but never expire them
        Solver = SolverWithCache(cache: new CachePolicy { Expiry = null }),
        Scorers = [Scorers.Match()],
    };

    /// <summary>Port of <c>cache_example_scoped</c>.</summary>
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

    /// <summary>Port of <c>cache_example_ignore_epochs</c>.</summary>
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
}

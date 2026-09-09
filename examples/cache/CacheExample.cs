using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Runner;

namespace InspectAzureAI.Examples.Cache;

using Model = InspectAzureAI.Eval.Model.Model;
using Solvers = InspectAzureAI.Eval.Solvers.Solvers;

/// <summary>
/// Port of <c>examples/cache.py</c> as an <see cref="IExample"/>: the five <see cref="CacheTasks"/> tasks, each
/// chained with the <see cref="CacheReport"/> solver so a run shows whether its model call was a cache write or a
/// read. Run a task twice to see the second run served from the cache, or <c>cache_example_ignore_epochs</c> with
/// <c>--epochs 5</c> to see the epochs after the first reuse the first one's output. Deviation: under <c>--fake</c>
/// the samples run one at a time (<c>max_connections</c> 1) so that concurrent epochs do not all miss the cache
/// before the first one has stored its entry.
/// </summary>
public sealed class CacheExample : IExample
{
    /// <summary>Turns queued on the scripted model: enough for any <c>--epochs</c> a demonstration would use.</summary>
    public const int FakeTurns = 1000;

    /// <summary>The scripted model's answer.</summary>
    public const string FakeAnswer = "Paris";

    public string Name => "cache";

    public string Description => "The prompt cache in a custom solver: generate(state, cache=...) with the default policy, a 12h expiry, no expiry, extra scopes and per_epoch=False";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new("cache_example", ctx => Build(ctx, CacheTasks.CacheExample()), "cache=True: the default policy (one week, per epoch)"),
        new("cache_example_with_expiry", ctx => Build(ctx, CacheTasks.CacheExampleWithExpiry()), "CachePolicy(expiry=\"12h\")"),
        new("cache_example_never_expires", ctx => Build(ctx, CacheTasks.CacheExampleNeverExpires()), "CachePolicy(expiry=None)"),
        new("cache_example_scoped", ctx => Build(ctx, CacheTasks.CacheExampleScoped()), "CachePolicy(scopes={role: attacker, team: red}, expiry=\"1W\")"),
        new("cache_example_ignore_epochs", ctx => Build(ctx, CacheTasks.CacheExampleIgnoreEpochs()), "CachePolicy(per_epoch=False): try --epochs 5"),
    ];

    public ExampleDefaults Defaults { get; } = new(Sandbox: "none");

    public IReadOnlyList<string> Deviations { get; } =
    [
        "The runner chains a display-only solver (CacheReport) after solver_with_cache that prints, per sample and epoch, whether the model call was a cache write or a read (the log's ModelEvent.cache) and where the cache directory is; the [Task] methods are the verbatim tasks without it.",
        "Under --fake the task runs its samples one at a time (max_connections 1) so that with --epochs N the epochs after the first find the entry the first one stored; Python runs them concurrently, and concurrent misses each call the provider.",
        "Python's solver=[solver_with_cache(...)] list of one solver is the solver itself here (EvalTask.Solver takes one solver; a list is Solvers.Chain).",
        "The scripted model answers \"Paris\" to every prompt; its cache entries land under the model name scripted in the same cache directory a live run uses ($INSPECT_CACHE_DIR, else the user cache directory), and inspectai cache clear removes them.",
        "Cache entries are JSON files rather than pickles (docs/ports/prompt-cache.md); the example does not see the difference.",
    ];

    /// <summary>The scripted api of the last <see cref="CreateFakeModel"/> call, so a test can count provider calls.</summary>
    public ScriptedModelApi? FakeApi { get; private set; }

    public Model CreateFakeModel(ExampleContext ctx)
    {
        FakeApi = new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.Text(FakeAnswer), FakeTurns));
        return new Model(FakeApi);
    }

    /// <summary>No sandbox: the tasks only call generate.</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;

    /// <summary>The task with the <see cref="CacheReport"/> solver chained after its own, and one sample at a time under <c>--fake</c>.</summary>
    public static EvalTask Build(ExampleContext ctx, EvalTask task)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(task);
        var reported = task with { Solver = Solvers.Chain(task.Solver, CacheReport.Solver(ctx.Out)) };
        return ctx.Fake ? reported with { Config = task.Config with { MaxConnections = 1 } } : reported;
    }
}

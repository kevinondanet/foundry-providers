using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Runner;

namespace InspectAzureAI.Examples.EarlyStopping;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/early_stopping.py</c> as an <see cref="IExample"/>: the <c>popularity</c> task of
/// <see cref="EarlyStoppingTasks"/> under the <see cref="TestEarlyStopping"/> manager, offline by design (the
/// Python task pins <c>mockllm/model</c>). Deviation: under the runner <c>--model</c> replaces the pinned mock
/// (the <c>[Task]</c> method keeps the pin), <c>-T seed=&lt;n&gt;</c> seeds the manager's coin flips (seed 0 under
/// <c>--fake</c> when not given), and the manager is wrapped in <see cref="EarlyStoppingReport"/>, which prints the
/// halted runs when the task completes.
/// </summary>
public sealed class EarlyStoppingExample : IExample
{
    /// <summary>The seed a <c>--fake</c> run uses when <c>-T seed</c> is not given.</summary>
    public const int FakeSeed = 0;

    public string Name => "early_stopping";

    public string Description => "Early stopping: the popularity task (5 epochs) under a custom EarlyStopping manager that randomly halts a sample and then all of its remaining epochs";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new(EarlyStoppingTasks.TaskName, Build, "popularity (100 samples, 5 epochs, match) with TestEarlyStopping; -T seed=<n> makes the coin flips reproducible"),
    ];

    public ExampleDefaults Defaults { get; } = new(Sandbox: "none");

    public IReadOnlyList<string> Deviations { get; } =
    [
        "The [Task] is registered as early_stopping for the inspectai CLI (the runner's task keeps the verbatim name popularity): the popularity example registers popularity in the same assembly and the CLI registry only disambiguates by assembly (file@name), not by declaring type.",
        "mockllm/model is a ScriptedModelApi of that name which answers Yes or No, chosen deterministically from the question, instead of mockllm's fixed \"Default output from mockllm/model\" text, so the match scores are not all zero; under the runner --model replaces it (the [Task] method keeps the pin, as Python's Task(model=...) does).",
        "TestEarlyStopping takes an optional Random: the runner seeds it (0 under --fake, or -T seed=<n>) so a run is reproducible; Python uses the global random(). Its list is guarded by a lock because the runner schedules samples concurrently.",
        "The runner wraps the manager in a display-only decorator (EarlyStoppingReport) that prints how many sample runs were halted when the task completes; the log's EvalResults.EarlyStopping carries the same information.",
    ];

    public Model CreateFakeModel(ExampleContext ctx) => EarlyStoppingTasks.MockLlm();

    /// <summary>No sandbox: the task only calls generate.</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;

    /// <summary>The task with the runner's model, the seeded manager and the report wrapper.</summary>
    public static EvalTask Build(ExampleContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var random = ctx.TaskArg("seed") is not null
            ? new Random(ctx.TaskArgInt("seed", FakeSeed))
            : ctx.Fake ? new Random(FakeSeed) : null;
        var task = EarlyStoppingTasks.Popularity();
        return task with
        {
            EarlyStopping = new EarlyStoppingReport(new TestEarlyStopping(random), ctx.Out),
            Model = ctx.ResolvedModel ?? task.Model,
        };
    }
}

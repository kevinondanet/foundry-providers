using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tasks;

/// <summary>
/// Port of <c>_eval/task/task.py</c> <c>Task</c> (the subset the runner consumes): dataset, setup and main
/// solvers, scorers with optional metric overrides, generation config, sandbox, epochs, limits and identity.
/// <see cref="Setup"/> always runs before <see cref="Solver"/>; <see cref="Metrics"/> replaces the metrics
/// every scorer declares; <see cref="FailOnError"/> aborts the eval on the first failing sample.
/// </summary>
public sealed record EvalTask
{
    public required string Name { get; init; }

    public required IDataset Dataset { get; init; }

    public Solver? Setup { get; init; }

    public Solver Solver { get; init; } = Solvers.Solvers.Generate();

    public IReadOnlyList<ScorerDef> Scorers { get; init; } = [];

    public IReadOnlyList<MetricDef>? Metrics { get; init; }

    public GenerateConfig Config { get; init; } = new();

    public SandboxSpec? Sandbox { get; init; }

    public Epochs? Epochs { get; init; }

    public bool FailOnError { get; init; } = true;

    public int? MessageLimit { get; init; }

    public int? TokenLimit { get; init; }

    public TimeSpan? TimeLimit { get; init; }

    public string Version { get; init; } = "0";

    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

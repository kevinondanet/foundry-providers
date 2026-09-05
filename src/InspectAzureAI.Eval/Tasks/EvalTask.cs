using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Runner;
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

    /// <summary>Port of <c>fail_on_error</c>: <see cref="FailOnError.Always"/> (the default) fails the eval on the first sample error; <see cref="FailOnError.Never"/>, a fraction or a count as in Python.</summary>
    public FailOnError FailOnError { get; init; } = FailOnError.Always;

    /// <summary>Port of <c>continue_on_fail</c>: keep running when the <see cref="FailOnError"/> condition is met and only fail the log at the end.</summary>
    public bool? ContinueOnFail { get; init; }

    /// <summary>Port of <c>retry_on_error</c>: how many times a sample that errors is re-run before its error counts.</summary>
    public int? RetryOnError { get; init; }

    public int? MessageLimit { get; init; }

    public int? TokenLimit { get; init; }

    public TimeSpan? TimeLimit { get; init; }

    /// <summary>Port of <c>Task.cost_limit</c>: limit on total cost (in dollars) for each sample.</summary>
    public double? CostLimit { get; init; }
    /// <summary>Port of <c>turn_limit</c>: maximum turns (model generations) per sample.</summary>
    public int? TurnLimit { get; init; }

    /// <summary>Port of <c>working_limit</c>: maximum working time (wall clock minus waiting) per sample.</summary>
    public TimeSpan? WorkingLimit { get; init; }

    /// <summary>Port of <c>early_stopping</c>: a manager the runner consults before every sample and notifies as samples complete.</summary>
    public IEarlyStopping? EarlyStopping { get; init; }

    public string Version { get; init; } = "0";

    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    /// <summary>Port of <c>Task.model_roles</c>: role name → a model name, a <c>Model</c>, or a list of these (eval-level roles override these per role).</summary>
    public IReadOnlyDictionary<string, object>? ModelRoles { get; init; }

    /// <summary>Port of <c>Task.approval</c>: tool use approval policies for this task (an eval-level <c>EvalOptions.Approval</c> overrides them).</summary>
    public Approval.ApprovalOption? Approval { get; init; }
}

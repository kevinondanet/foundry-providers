using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tasks;

using Model = InspectAzureAI.Eval.Model.Model;

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

    /// <summary>
    /// Port of <c>Task.metrics</c> (<c>list[Metric | dict[str, list[Metric]]]</c>): when set, replaces the metrics every
    /// scorer declares — its list part here, its dictionary part in <see cref="MetricsByKey"/>. Either one being set
    /// replaces both of a scorer's <see cref="ScorerDef.Metrics"/> and <see cref="ScorerDef.MetricsByKey"/>.
    /// </summary>
    public IReadOnlyList<MetricDef>? Metrics { get; init; }

    /// <summary>
    /// The dictionary part of a task-level <c>metrics</c> override (Python's <c>metrics={...}</c> or the trailing dict of
    /// <c>metrics=[..., {...}]</c>): per-key metrics applied to every scorer, as <see cref="ScorerDef.MetricsByKey"/>.
    /// Alone (no <see cref="Metrics"/>) it is the dictionary form, producing only per-key scores.
    /// </summary>
    public MetricDict? MetricsByKey { get; init; }

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

    /// <summary>
    /// Port of the task args <c>@task</c> records (<c>ResolvedTask.task_args</c>): the arguments this task was
    /// built with, written to the log as <c>task_args</c> and hashed into the eval-set task identifier, so two
    /// tasks sharing a name are told apart by their args.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? TaskArgs { get; init; }

    /// <summary>
    /// Port of <c>Task.model</c>: the default model for this task. Resolved as Python does
    /// (<c>ResolvedTask.model = task.model or model</c>): when set it is used ahead of the eval-level
    /// <c>EvalOptions.Model</c>; when null the eval-level model applies, and neither being set fails the run.
    /// </summary>
    public Model? Model { get; init; }

    /// <summary>Port of <c>Task.model_roles</c>: role name → a model name, a <c>Model</c>, or a list of these (eval-level roles override these per role).</summary>
    public IReadOnlyDictionary<string, object>? ModelRoles { get; init; }

    /// <summary>Port of <c>Task.approval</c>: tool use approval policies for this task (an eval-level <c>EvalOptions.Approval</c> overrides them).</summary>
    public Approval.ApprovalOption? Approval { get; init; }
}

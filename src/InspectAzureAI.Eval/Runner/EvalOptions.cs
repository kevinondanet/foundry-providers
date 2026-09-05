using InspectAzureAI.Eval.Concurrency;

namespace InspectAzureAI.Eval.Runner;

using Hooks = InspectAzureAI.Eval.Hooks.Hooks;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of the eval-level arguments of <c>_eval/eval.py</c> <c>eval()</c> this runner honours. The limits and
/// <see cref="FailOnError"/> override the task's own values only when set; <see cref="MaxSamples"/> bounds the
/// samples in flight; <see cref="Cleanup"/> reaches the sandbox provider so a failed sample can be inspected.
/// </summary>
public sealed record EvalOptions
{
    public required Model Model { get; init; }

    /// <summary>Port of <c>limit</c>: run only the first N samples (ignored when <see cref="SampleIds"/> is given, as in Python).</summary>
    public int? Limit { get; init; }

    /// <summary>Port of <c>sample_id</c>: the ids (ints or strings, compared textually) to run.</summary>
    public IReadOnlyList<object>? SampleIds { get; init; }

    public int? Epochs { get; init; }

    /// <summary>
    /// Port of <c>max_samples</c>: the samples in flight at once. Null (the default) derives it as Python does — the
    /// model's adaptive controller plus a buffer when adaptive connections are active, else <c>max_connections</c>.
    /// </summary>
    public int? MaxSamples { get; init; }

    /// <summary>Port of the eval-level <c>adaptive_connections</c>; null inherits the model's own setting.</summary>
    public AdaptiveConnections? AdaptiveConnections { get; init; }

    /// <summary>Port of <c>fail_on_error</c>: a bool, a fraction of the sample runs (below 1) or an absolute count; unset defers to the task.</summary>
    public FailOnError? FailOnError { get; init; }

    /// <summary>Port of <c>continue_on_fail</c>: keep running when the <see cref="FailOnError"/> condition is met and only fail the log at the end.</summary>
    public bool? ContinueOnFail { get; init; }

    /// <summary>Port of <c>retry_on_error</c>: how many times a sample that errors is re-run (from scratch, same uuid) before its error counts.</summary>
    public int? RetryOnError { get; init; }

    public string LogDir { get; init; } = "logs";

    public bool Cleanup { get; init; } = true;

    public int? MessageLimit { get; init; }

    public int? TokenLimit { get; init; }

    public TimeSpan? TimeLimit { get; init; }

    /// <summary>Port of <c>cost_limit</c>: limit on total cost (in dollars) for each sample; needs cost data for the model.</summary>
    public double? CostLimit { get; init; }

    /// <summary>Port of <c>model_cost_config</c>: a JSON file of model prices applied before the eval runs (see <c>ModelCostConfig</c>).</summary>
    public string? ModelCostConfig { get; init; }
    /// <summary>Port of <c>turn_limit</c>: maximum turns (model generations) per sample.</summary>
    public int? TurnLimit { get; init; }

    /// <summary>Port of <c>working_limit</c>: maximum working time (wall clock minus waiting) per sample.</summary>
    public TimeSpan? WorkingLimit { get; init; }

    public IEvalReporter? Reporter { get; init; }

    /// <summary>
    /// Port of <c>model_roles</c>: role name → a model name, a <see cref="Model"/>, or a list of these, resolved by
    /// <c>ModelRoles.Resolve</c> and merged over the task's own roles (eval-level roles win per role). Solvers and
    /// scorers look them up with <c>ModelRoles.GetModel(role, ...)</c>.
    /// </summary>
    public IReadOnlyDictionary<string, object>? ModelRoles { get; init; }

    /// <summary>
    /// Lifecycle hooks for this run only, notified after the process-wide <see cref="InspectAzureAI.Eval.Hooks.HookRegistry"/> hooks (Python
    /// has only the registry; this is the port's per-run alternative to registering at import time).
    /// </summary>
    public IReadOnlyList<Hooks>? Hooks { get; init; }

    /// <summary>Port of the internal <c>eval_set_id</c> argument of <c>eval()</c>: set by an eval set driver so the log header and every hook payload carry it.</summary>
    public string? EvalSetId { get; init; }
}

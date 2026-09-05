using InspectAzureAI.Eval.Concurrency;

namespace InspectAzureAI.Eval.Runner;

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

    public bool? FailOnError { get; init; }

    public string LogDir { get; init; } = "logs";

    public bool Cleanup { get; init; } = true;

    public int? MessageLimit { get; init; }

    public int? TokenLimit { get; init; }

    public TimeSpan? TimeLimit { get; init; }

    /// <summary>Port of <c>cost_limit</c>: limit on total cost (in dollars) for each sample; needs cost data for the model.</summary>
    public double? CostLimit { get; init; }

    /// <summary>Port of <c>model_cost_config</c>: a JSON file of model prices applied before the eval runs (see <c>ModelCostConfig</c>).</summary>
    public string? ModelCostConfig { get; init; }

    public IEvalReporter? Reporter { get; init; }

    /// <summary>
    /// Port of <c>model_roles</c>: role name → a model name, a <see cref="Model"/>, or a list of these, resolved by
    /// <c>ModelRoles.Resolve</c> and merged over the task's own roles (eval-level roles win per role). Solvers and
    /// scorers look them up with <c>ModelRoles.GetModel(role, ...)</c>.
    /// </summary>
    public IReadOnlyDictionary<string, object>? ModelRoles { get; init; }
}

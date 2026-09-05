using InspectAzureAI.Eval.Scorers;

namespace InspectAzureAI.Eval.Runner.Scoring;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>The optional arguments of <c>score_async</c> and the <c>inspect score</c> command that <see cref="ScoreLogs.ScoreAsync(Log.EvalLog, IReadOnlyList{ScorerDef}, ScoreAction?, IReadOnlyList{ScoreReducer}?, ScoreLogOptions?, CancellationToken)"/> accepts.</summary>
public sealed record ScoreLogOptions
{
    /// <summary>
    /// Port of the <c>model</c> argument: the active model the scorers see (<c>get_model()</c>). Python rebuilds the
    /// model named in the log header when none is given; this port has no model registry, so without one a scorer
    /// that generates fails with <see cref="Provider.Core.PrerequisiteError"/> naming the header model.
    /// </summary>
    public Model? Model { get; init; }

    /// <summary>
    /// Port of the <c>model_roles</c> argument, in the form <c>ModelRoles.Resolve</c> accepts. Python merges
    /// these over the roles recorded in the log header; this port cannot rebuild header roles, so only these apply.
    /// </summary>
    public IReadOnlyDictionary<string, object>? ModelRoles { get; init; }

    /// <summary>Port of the <c>metrics</c> argument: replaces the metrics of every scorer (and of the log header).</summary>
    public IReadOnlyList<MetricDef>? Metrics { get; init; }

    /// <summary>Maximum samples scored concurrently (Python scores every sample at once); null means all of them.</summary>
    public int? MaxSamples { get; init; }

    /// <summary>Port of <c>--output-file</c>: where the path overload writes the scored log (default: over the input file).</summary>
    public string? OutputPath { get; init; }
}

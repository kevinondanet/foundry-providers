using InspectAzureAI.Eval.Solvers;

namespace InspectAzureAI.Eval.Scorers;

/// <summary>Port of the <c>CORRECT</c> / <c>INCORRECT</c> / <c>PARTIAL</c> / <c>NOANSWER</c> constants of <c>scorer/_metric.py</c>.</summary>
public static class ScoreConstants
{
    public const string Correct = "C";

    public const string Incorrect = "I";

    public const string Partial = "P";

    public const string NoAnswer = "N";

    /// <summary>Port of <c>UNCHANGED</c>: the sentinel a score edit uses for a field it leaves as is.</summary>
    public const string Unchanged = "UNCHANGED";
}

/// <summary>Port of the <c>Scorer</c> protocol (<c>scorer/_scorer.py</c>): scores a task state against its target.</summary>
public delegate Task<Score> Scorer(TaskState state, Target target, CancellationToken cancellationToken);

/// <summary>
/// A scorer together with its registry name and the metrics attached by <c>@scorer(metrics=...)</c>
/// (<c>scorer/_scorer.py</c>); the runner keys per-sample scores by <see cref="Name"/>.
/// </summary>
public sealed record ScorerDef(string Name, Scorer Score, IReadOnlyList<MetricDef> Metrics)
{
    /// <summary>
    /// Port of the dictionary form of <c>@scorer(metrics=...)</c>: metrics keyed by score-value key (glob patterns allowed),
    /// each key producing its own <see cref="Log.EvalScore"/> (see <see cref="MetricDict"/>). Null for a plain metric list.
    /// With an empty <see cref="Metrics"/> this is Python's <c>metrics={...}</c>, which produces only per-key scores; with
    /// metrics in <see cref="Metrics"/> it is the list form <c>metrics=[accuracy(), {...}]</c>, which produces the scorer's
    /// own score as well. Deviation: a list holding only dictionaries (<c>metrics=[{...}]</c>) is not distinguished from
    /// the dictionary form; Python emits an extra metric-less score for it.
    /// </summary>
    public MetricDict? MetricsByKey { get; init; }
}

/// <summary>Port of the <c>Metric</c> protocol (<c>scorer/_metric.py</c>): aggregates the sample scores of one scorer into a value.</summary>
public delegate ScoreValue Metric(IReadOnlyList<SampleScore> scores);

/// <summary>A metric together with the registry name Python attaches with <c>@metric</c>.</summary>
public sealed record MetricDef(string Name, Metric Compute)
{
    /// <summary>Port of <c>@metric(scores=...)</c>: which epoch view of the sample scores the metric receives.</summary>
    public MetricScores Scores { get; init; } = MetricScores.Auto;

    /// <summary>
    /// The creation parameters Python's registry records as the metric's <c>options</c> in the log header
    /// (<c>registry_params</c>), so <see cref="Runner.Scoring.LogHeader.MetricFromLog"/> can re-create it; null when
    /// the metric takes none.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Options { get; init; }
}

/// <summary>
/// Port of <c>MetricScores</c> (<c>scorer/_metric.py</c>): the epoch-reduction contract of a metric's input.
/// <see cref="Auto"/> and <see cref="Reduced"/> receive one score per sample after the epochs reducer runs;
/// <see cref="Unreduced"/> receives one score per sample per epoch (each epoch is an independent observation).
/// </summary>
public enum MetricScores
{
    Auto,
    Reduced,
    Unreduced,
}

/// <summary>Port of the <c>ScoreReducer</c> protocol (<c>scorer/_reducer/types.py</c>): folds the epoch scores of a sample into one score.</summary>
public delegate Score ScoreReducer(IReadOnlyList<Score> scores);

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
public sealed record ScorerDef(string Name, Scorer Score, IReadOnlyList<MetricDef> Metrics);

/// <summary>Port of the <c>Metric</c> protocol (<c>scorer/_metric.py</c>): aggregates the sample scores of one scorer into a value.</summary>
public delegate ScoreValue Metric(IReadOnlyList<SampleScore> scores);

/// <summary>A metric together with the registry name Python attaches with <c>@metric</c>.</summary>
public sealed record MetricDef(string Name, Metric Compute)
{
    /// <summary>Port of <c>@metric(scores=...)</c>: which epoch view of the sample scores the metric receives.</summary>
    public MetricScores Scores { get; init; } = MetricScores.Auto;
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

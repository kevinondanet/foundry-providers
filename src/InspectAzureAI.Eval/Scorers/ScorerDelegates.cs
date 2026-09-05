using InspectAzureAI.Eval.Solvers;

namespace InspectAzureAI.Eval.Scorers;

/// <summary>Port of the <c>CORRECT</c> / <c>INCORRECT</c> / <c>PARTIAL</c> / <c>NOANSWER</c> constants of <c>scorer/_metric.py</c>.</summary>
public static class ScoreConstants
{
    public const string Correct = "C";

    public const string Incorrect = "I";

    public const string Partial = "P";

    public const string NoAnswer = "N";
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
public sealed record MetricDef(string Name, Metric Compute);

/// <summary>Port of the <c>ScoreReducer</c> protocol (<c>scorer/_reducer/types.py</c>): folds the epoch scores of a sample into one score.</summary>
public delegate Score ScoreReducer(IReadOnlyList<Score> scores);

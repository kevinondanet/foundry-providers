namespace InspectAzureAI.Eval.Scorers;

/// <summary>
/// Port of the built-in metrics of <c>scorer/_metrics/</c> (<c>accuracy.py</c>, <c>mean.py</c>, <c>std.py</c>).
/// Unscored (NaN-valued) sample scores are skipped, as <c>_eval/task/results.py</c> does before it
/// hands scores to a metric, so a metric never sees the unscored sentinel.
/// </summary>
public static partial class Metrics
{
    /// <summary>Port of <c>accuracy()</c>: mean of <c>toFloat(value)</c>; 0 when there are no scores.</summary>
    public static MetricDef Accuracy(Func<ScoreValue, double>? toFloat = null) =>
        new("accuracy", scores => Average(Values(scores, toFloat)));

    /// <summary>Port of <c>mean()</c>; 0 when there are no scores.</summary>
    public static MetricDef Mean(Func<ScoreValue, double>? toFloat = null) =>
        new("mean", scores => Average(Values(scores, toFloat)));

    /// <summary>
    /// Port of <c>stderr(to_float, cluster)</c>: sample standard deviation over the square root of n (0 when n &lt; 2),
    /// or, when <paramref name="cluster"/> names a sample-metadata key, the clustered standard error of the mean
    /// with a finite-cluster correction (0 with fewer than two clusters; a sample without a cluster id throws).
    /// </summary>
    public static MetricDef Stderr(Func<ScoreValue, double>? toFloat = null, string? cluster = null) =>
        new("stderr", scores =>
        {
            var values = Values(scores, toFloat);
            if (cluster is not null)
            {
                return ClusteredStderr(ClusterPartition(Scored(scores), cluster, values, "stderr"));
            }

            return values.Count < 2 ? 0.0 : SampleStd(values) / Math.Sqrt(values.Count);
        });

    /// <summary>Port of <c>std()</c>: sample standard deviation (ddof = 1); 0 when n &lt; 2.</summary>
    public static MetricDef Std(Func<ScoreValue, double>? toFloat = null) =>
        new("std", scores =>
        {
            var values = Values(scores, toFloat);
            return values.Count < 2 ? 0.0 : SampleStd(values);
        });

    private static List<double> Values(IReadOnlyList<SampleScore> scores, Func<ScoreValue, double>? toFloat)
    {
        ArgumentNullException.ThrowIfNull(scores);
        var convert = toFloat ?? ValueToFloat.Default;
        return scores.Where(s => !s.Score.IsUnscored).Select(s => convert(s.Score.Value)).ToList();
    }

    private static List<SampleScore> Scored(IReadOnlyList<SampleScore> scores) => scores.Where(s => !s.Score.IsUnscored).ToList();

    private static double Average(List<double> values) => values.Count == 0 ? 0.0 : values.Sum() / values.Count;

    private static double SampleStd(List<double> values)
    {
        var mean = values.Sum() / values.Count;
        var sumSquares = values.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sumSquares / (values.Count - 1));
    }
}

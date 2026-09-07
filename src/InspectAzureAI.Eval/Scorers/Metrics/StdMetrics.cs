using System.Globalization;

namespace InspectAzureAI.Eval.Scorers;

/// <summary>
/// Port of the rest of <c>scorer/_metrics/std.py</c>: <c>var</c>, <c>bootstrap_stderr</c>, <c>ci</c>, <c>ci_wilson</c>
/// and the clustered standard error shared with <c>stderr(cluster=...)</c>.
/// </summary>
public static partial class Metrics
{
    /// <summary>Port of <c>var()</c>: sample variance (ddof = 1); 0 when n &lt; 2.</summary>
    public static MetricDef Var(Func<ScoreValue, double>? toFloat = null) =>
        new("var", scores =>
        {
            var values = Values(scores, toFloat);
            return values.Count < 2 ? 0.0 : SampleVariance(values);
        });

    /// <summary>
    /// Port of <c>bootstrap_stderr(num_samples)</c>: the standard deviation of <paramref name="numSamples"/> resampled
    /// means; 0 when there are no scores. <paramref name="random"/> replaces Python's global numpy state so a test
    /// can seed it; the default is <see cref="Random.Shared"/>.
    /// </summary>
    public static MetricDef BootstrapStderr(int numSamples = 1000, Func<ScoreValue, double>? toFloat = null, Random? random = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(numSamples);
        return new("bootstrap_stderr", scores =>
        {
            var values = Values(scores, toFloat);
            if (values.Count == 0)
            {
                return 0.0;
            }

            var means = BootstrapMeans(values, null, numSamples, random ?? Random.Shared);
            return PopulationStd(means);
        });
    }

    /// <summary>
    /// Port of <c>ci(level, method, num_samples, to_float, cluster)</c>: the two-sided confidence interval for the
    /// mean as a <c>{lower, upper}</c> dictionary. <paramref name="method"/> is <c>t</c> (mean ± t · stderr with
    /// n − 1 degrees of freedom, clusters − 1 when clustered) or <c>bootstrap</c> (percentile bootstrap of the mean,
    /// resampling whole clusters when clustered). Fewer than two observations collapse to the point (0 when empty).
    /// </summary>
    public static MetricDef Ci(
        double level = 0.95,
        string method = "t",
        int numSamples = 1000,
        Func<ScoreValue, double>? toFloat = null,
        string? cluster = null,
        Random? random = null)
    {
        if (!(level > 0.0 && level < 1.0))
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, $"ci `level` must be in the open interval (0, 1), got {level}");
        }

        if (method is not ("t" or "bootstrap"))
        {
            throw new ArgumentException($"Unknown ci method '{method}' (expected 't' or 'bootstrap')", nameof(method));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(numSamples);
        var tail = (1.0 - level) / 2.0;
        return new("ci", scores =>
        {
            var scored = Scored(scores);
            var values = Values(scores, toFloat);
            var partition = cluster is not null ? ClusterPartition(scored, cluster, values, "ci") : null;
            if (values.Count < 2)
            {
                var point = values.Count > 0 ? values[0] : 0.0;
                return Interval(point, point);
            }

            if (method == "t")
            {
                var mean = values.Sum() / values.Count;
                double se;
                int df;
                if (partition is not null)
                {
                    se = ClusteredStderr(partition);
                    df = Math.Max(partition.Count - 1, 1);
                }
                else
                {
                    se = CltStderr(values);
                    df = values.Count - 1;
                }

                var t = Distributions.TInvCdf(1.0 - tail, df);
                return Interval(mean - t * se, mean + t * se);
            }

            var means = BootstrapMeans(values, partition, numSamples, random ?? Random.Shared);
            return Interval(Quantile(means, tail), Quantile(means, 1.0 - tail));
        });
    }

    /// <summary>
    /// Port of <c>ci_wilson(level, to_float, cluster)</c>: the Wilson score interval for the mean of scores in [0, 1]
    /// (a value outside that range throws). Clustered intervals use the Korn-Graubard effective sample size
    /// <c>p̂(1 − p̂) / v̂</c> capped at n and a Student-t critical value with clusters − 1 degrees of freedom; fewer than
    /// two clusters throws. Empty input yields <c>{0, 0}</c>.
    /// </summary>
    public static MetricDef CiWilson(double level = 0.95, Func<ScoreValue, double>? toFloat = null, string? cluster = null)
    {
        if (!(level > 0.0 && level < 1.0))
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, $"ci_wilson `level` must be in the open interval (0, 1), got {level}");
        }

        var tail = (1.0 - level) / 2.0;
        // z from the lower tail: 1 - tail rounds to exactly 1.0 for levels within one ulp of 1
        var z = -Distributions.NormalInvCdf(tail);
        return new("ci_wilson", scores =>
        {
            var scored = Scored(scores);
            var convert = toFloat ?? ValueToFloat.Default;
            var values = new List<double>(scored.Count);
            foreach (var sampleScore in scored)
            {
                var value = convert(sampleScore.Score.Value);
                if (!(value >= 0.0 && value <= 1.0))
                {
                    throw new ArgumentException(
                        $"Sample {IdText(sampleScore.SampleId)} has score value {PythonText.FloatRepr(value)}. "
                        + "`ci_wilson` treats the mean score as a binomial proportion, so all score values must be between 0 and 1.",
                        nameof(scores));
                }

                values.Add(value);
            }

            var partition = cluster is not null ? ClusterPartition(scored, cluster, values, "ci_wilson") : null;
            if (partition is not null && partition.Count < 2)
            {
                throw new ArgumentException(
                    $"Computing `ci_wilson` with clustering requires at least two clusters, but the '{cluster}' metadata contains "
                    + $"{partition.Count}. The clustered variance is unestimable from a single cluster.",
                    nameof(scores));
            }

            if (values.Count == 0)
            {
                return Interval(0.0, 0.0);
            }

            var n = (double)values.Count;
            var pHat = values.Sum() / n;
            double critical;
            if (partition is null)
            {
                critical = z;
            }
            else
            {
                var clusteredStderr = ClusteredStderr(partition);
                var clusteredVariance = clusteredStderr * clusteredStderr;
                var numerator = pHat * (1.0 - pHat);
                if (numerator > 0.0 && clusteredVariance > 0.0)
                {
                    n = Math.Min(numerator / clusteredVariance, n);
                }

                critical = Distributions.TInvCdf(Math.Min(1.0 - tail, Math.BitDecrement(1.0)), partition.Count - 1);
            }

            var denominator = 1.0 + critical * critical / n;
            var center = (pHat + critical * critical / (2.0 * n)) / denominator;
            var halfWidth = critical * Math.Sqrt(pHat * (1.0 - pHat) / n + critical * critical / (4.0 * n * n)) / denominator;
            return Interval(Math.Max(center - halfWidth, 0.0), Math.Min(center + halfWidth, 1.0));
        });
    }

    /// <summary>
    /// Port of <c>_cluster_partition</c>: validates the cluster metadata and partitions the already-converted values by
    /// cluster id (Python dict equality: <c>1 == 1.0 == True</c>). A missing key, null or NaN id throws.
    /// </summary>
    internal static List<List<double>> ClusterPartition(IReadOnlyList<SampleScore> scores, string cluster, IReadOnlyList<double> values, string metricName)
    {
        var groups = new OrderedDictionary<PythonText.ScalarKey, List<double>>();
        for (var i = 0; i < scores.Count; i++)
        {
            var metadata = scores[i].SampleMetadata;
            object? clusterId = metadata is not null && metadata.TryGetValue(cluster, out var found) ? found : null;
            if (clusterId is null || (clusterId is double d && double.IsNaN(d)) || (clusterId is float f && float.IsNaN(f)))
            {
                throw new ArgumentException(
                    $"Sample {IdText(scores[i].SampleId)} has no cluster metadata. To compute `{metricName}` with clustering, "
                    + $"each sample metadata must have a value for '{cluster}'",
                    nameof(scores));
            }

            var key = PythonText.ScalarKey.OfObject(clusterId);
            if (!groups.TryGetValue(key, out var group))
            {
                group = [];
                groups[key] = group;
            }

            group.Add(values[i]);
        }

        return groups.Values.ToList();
    }

    /// <summary>
    /// Port of <c>_clustered_stderr</c> (Appendix A of arXiv:2411.00640 with a finite-cluster correction): the square root
    /// of <c>C / (C − 1) · Σ_c (Σ_{i∈c} (s_i − mean))²</c> over the number of scores; 0 with fewer than two clusters.
    /// </summary>
    internal static double ClusteredStderr(IReadOnlyList<List<double>> partition)
    {
        var clusterCount = partition.Count;
        if (clusterCount < 2)
        {
            return 0.0;
        }

        var count = partition.Sum(group => group.Count);
        var mean = partition.Sum(group => group.Sum()) / count;
        var clusteredVariance = 0.0;
        foreach (var group in partition)
        {
            var deviation = group.Sum(v => v - mean);
            clusteredVariance += deviation * deviation;
        }

        return Math.Sqrt(clusteredVariance * clusterCount / (clusterCount - 1)) / count;
    }

    /// <summary>Port of <c>_clt_stderr</c>: sample standard deviation over √n; 0 when n &lt; 2.</summary>
    internal static double CltStderr(List<double> values) => values.Count < 2 ? 0.0 : SampleStd(values) / Math.Sqrt(values.Count);

    /// <summary>Port of <c>_bootstrap_means</c>: means of i.i.d. resamples, or of whole-cluster resamples when partitioned.</summary>
    internal static List<double> BootstrapMeans(IReadOnlyList<double> values, IReadOnlyList<List<double>>? partition, int numSamples, Random random)
    {
        var means = new List<double>(numSamples);
        if (partition is null)
        {
            var n = values.Count;
            for (var s = 0; s < numSamples; s++)
            {
                var total = 0.0;
                for (var i = 0; i < n; i++)
                {
                    total += values[random.Next(n)];
                }

                means.Add(total / n);
            }

            return means;
        }

        var clusters = partition.Count;
        for (var s = 0; s < numSamples; s++)
        {
            var total = 0.0;
            var count = 0;
            for (var i = 0; i < clusters; i++)
            {
                var pick = partition[random.Next(clusters)];
                total += pick.Sum();
                count += pick.Count;
            }

            means.Add(total / count);
        }

        return means;
    }

    /// <summary>numpy's default (linear) quantile: interpolates between the order statistics at <c>(n − 1) · q</c>.</summary>
    internal static double Quantile(IReadOnlyList<double> values, double q)
    {
        ArgumentOutOfRangeException.ThrowIfZero(values.Count);
        var sorted = values.Order().ToList();
        var position = (sorted.Count - 1) * q;
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(lower + 1, sorted.Count - 1);
        var fraction = position - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }

    private static ScoreValue Interval(double lower, double upper) =>
        new ScoreValue.Dict(new OrderedDictionary<string, ScoreValue?>(StringComparer.Ordinal) { ["lower"] = lower, ["upper"] = upper });

    private static double SampleVariance(List<double> values)
    {
        var mean = values.Sum() / values.Count;
        return values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
    }

    private static double PopulationStd(List<double> values)
    {
        var mean = values.Sum() / values.Count;
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);
    }

    /// <summary>Python's rendering of a sample id in an error message (<c>None</c> when absent).</summary>
    internal static string IdText(object? id) => id switch
    {
        null => "None",
        double d => PythonText.FloatRepr(d),
        _ => Convert.ToString(id, CultureInfo.InvariantCulture) ?? "None",
    };
}

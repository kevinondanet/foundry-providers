using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tests;

/// <summary>
/// The extra metrics (var, clustered stderr, bootstrap, ci, ci_wilson, grouped, frequency, aggregate, krippendorff,
/// perplexity) and reducers (majority, pass_k, collect, name lookup) ported from <c>scorer/_metrics</c> and
/// <c>scorer/_reducer</c>. Reference values were computed with the Python implementation on the same inputs.
/// </summary>
public class MetricPortTests
{
    private const double Tolerance = 1e-9;

    private static readonly double[] Seven = [0.2, 0.7, 1.0, 0.0, 0.5, 0.9, 0.3];

    private static SampleScore Sample(ScoreValue value, IReadOnlyDictionary<string, object?>? metadata = null, object? id = null) =>
        new(new Score(value), id, metadata);

    private static SampleScore Sample(ScoreValue value, string clusterKey, object? cluster, object? id = null) =>
        new(new Score(value), id, new Dictionary<string, object?> { [clusterKey] = cluster });

    private static IReadOnlyList<SampleScore> Range(int count) => Enumerable.Range(0, count).Select(i => Sample(i)).ToList();

    private static IReadOnlyList<SampleScore> Clustered(int count, Func<int, object?> cluster, Func<int, double>? value = null) =>
        Enumerable.Range(0, count).Select(i => Sample(value is null ? i : value(i), "c", cluster(i))).ToList();

    private static IReadOnlyList<SampleScore> Binary(int successes, int n) =>
        Enumerable.Range(0, n).Select(i => Sample(i < successes ? 1.0 : 0.0)).ToList();

    private static double Num(ScoreValue value) => ((ScoreValue.Num)value).Value;

    private static double Num(Score score) => Num(score.Value);

    private static Score S(ScoreValue value) => new(value);

    private static ScoreValue.List L(params ScoreValue[] items) => new(items);

    private static ScoreValue.Dict D(params (string Key, ScoreValue? Value)[] items) =>
        new(items.ToDictionary(i => i.Key, i => i.Value, StringComparer.Ordinal));

    private static IReadOnlyDictionary<string, ScoreValue?> Dict(ScoreValue value) => ((ScoreValue.Dict)value).Items;

    private static double Entry(ScoreValue value, string key) => Num(Dict(value)[key]!);

    private static IReadOnlyList<SampleScore> Units(params ScoreValue[] units) => units.Select(u => Sample(u)).ToList();

    private static List<string> WarningsSince(int count) => ProviderLogger.Warnings.Skip(count).ToList();

    // ---- var / std / stderr -------------------------------------------------------------------------

    [Fact]
    public void var_std_and_stderr_match_python()
    {
        var seven = Seven.Select(v => Sample(v)).ToList();

        Assert.Equal(0.13809523809523808, Num(Metrics.Var().Compute(seven)), Tolerance);
        Assert.Equal(0.3716116764786032, Num(Metrics.Std().Compute(seven)), Tolerance);
        Assert.Equal(0.1404560114643107, Num(Metrics.Stderr().Compute(seven)), Tolerance);
        Assert.Equal(9.166666666666666, Num(Metrics.Var().Compute(Range(10))), Tolerance);
        Assert.Equal(3.0276503540974917, Num(Metrics.Std().Compute(Range(10))), Tolerance);
        Assert.Equal(0.9574271077563381, Num(Metrics.Stderr().Compute(Range(10))), Tolerance);
        Assert.Equal("var", Metrics.Var().Name);
    }

    [Fact]
    public void var_returns_zero_below_two_scores_and_skips_unscored()
    {
        Assert.Equal(0.0, Num(Metrics.Var().Compute([Sample(4)])));
        Assert.Equal(0.0, Num(Metrics.Var().Compute([])));
        Assert.Equal(0.0, Num(Metrics.Var().Compute([Sample(4), new SampleScore(Score.Unscored())])));
        Assert.Equal(0.0, Num(Metrics.Var(_ => 0.5).Compute([Sample("C"), Sample("I")])), Tolerance);
    }

    [Fact]
    public void clustered_stderr_matches_python()
    {
        Assert.Equal(0.6454972243679028, Num(Metrics.Stderr(cluster: "c").Compute(Clustered(20, i => i % 4))), Tolerance);
        var lopsided = Clustered(24, i => i < 18 ? "big" : $"s{i}", i => i % 5);
        Assert.Equal(0.16510407538845479, Num(Metrics.Stderr(cluster: "c").Compute(lopsided)), Tolerance);
        Assert.Equal(0.0, Num(Metrics.Stderr(cluster: "c").Compute([Sample(1.0, "c", "only"), Sample(0.0, "c", "only"), Sample(1.0, "c", "only")])));
        Assert.Equal(0.0, Num(Metrics.Stderr(cluster: "c").Compute([])));
    }

    [Fact]
    public void clustered_stderr_rejects_missing_null_and_nan_cluster_ids()
    {
        var metric = Metrics.Stderr(cluster: "c");

        Assert.Contains("has no cluster metadata", Assert.Throws<ArgumentException>(() => metric.Compute([Sample(1.0)])).Message);
        Assert.Contains("has no cluster metadata", Assert.Throws<ArgumentException>(() => metric.Compute([Sample(1.0, "c", "a"), Sample(0.0, "c", null)])).Message);
        Assert.Contains("has no cluster metadata", Assert.Throws<ArgumentException>(() => metric.Compute([Sample(1.0, "c", "a"), Sample(0.0, "c", double.NaN)])).Message);
        Assert.Contains("Sample 7 has no cluster metadata", Assert.Throws<ArgumentException>(() => metric.Compute([Sample(1.0, "other", "a", id: 7)])).Message);
    }

    [Fact]
    public void cluster_partition_uses_python_equality_for_ids()
    {
        IReadOnlyList<SampleScore> scores = [Sample(1.0, "c", 1), Sample(2.0, "c", 1.0), Sample(3.0, "c", true), Sample(4.0, "c", "1"), Sample(5.0, "c", 2L)];

        var partition = Metrics.ClusterPartition(scores, "c", [1, 2, 3, 4, 5], "stderr");

        Assert.Equal(3, partition.Count);
        Assert.Equal([1.0, 2.0, 3.0], partition[0]);
        Assert.Equal([4.0], partition[1]);
        Assert.Equal([5.0], partition[2]);
    }

    // ---- distributions ------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 12.706204736174659)]
    [InlineData(2, 4.302652729749456)]
    [InlineData(4, 2.7764451051977908)]
    [InlineData(9, 2.2621571627982027)]
    [InlineData(10, 2.228138851986274)]
    [InlineData(30, 2.042272456301233)]
    [InlineData(1000, 1.9623390808264358)]
    public void t_inverse_cdf_matches_python(int df, double expected)
    {
        Assert.Equal(expected, Distributions.TInvCdf(0.975, df), Tolerance);
        Assert.Equal(-expected, Distributions.TInvCdf(0.025, df), Tolerance);
    }

    [Fact]
    public void t_inverse_cdf_tails_and_arguments()
    {
        Assert.Equal(0.0, Distributions.TInvCdf(0.5, 7));
        Assert.Equal(-636.6192487687181, Distributions.TInvCdf(0.0005, 1), 1e-6);
        Assert.Equal(22.32712477011922, Distributions.TInvCdf(0.999, 2), Tolerance);
        Assert.Equal(Math.Tan(Math.PI * (0.9999 - 0.5)), Distributions.TInvCdf(0.9999, 1), 1e-4);
        Assert.Throws<ArgumentOutOfRangeException>(() => Distributions.TInvCdf(1.0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => Distributions.TInvCdf(0.5, 0));
    }

    [Theory]
    [InlineData(0.025, -1.9599639845400538)]
    [InlineData(0.005, -2.5758293035489)]
    [InlineData(0.1, -1.2815515655446006)]
    [InlineData(0.5, 0.0)]
    [InlineData(0.975, 1.9599639845400534)]
    [InlineData(1e-10, -6.361340902404057)]
    [InlineData(0.3, -0.5244005127080408)]
    public void normal_inverse_cdf_matches_cpython(double p, double expected)
    {
        Assert.Equal(expected, Distributions.NormalInvCdf(p), 1e-12);
    }

    // ---- ci -----------------------------------------------------------------------------------------

    [Fact]
    public void ci_t_interval_matches_python()
    {
        var interval = Metrics.Ci().Compute(Range(10));
        Assert.Equal(2.334149410331833, Entry(interval, "lower"), Tolerance);
        Assert.Equal(6.6658505896681675, Entry(interval, "upper"), Tolerance);
        Assert.Equal(["lower", "upper"], Dict(interval).Keys);

        var wide = Metrics.Ci(level: 0.99).Compute(Range(20));
        Assert.Equal(5.715339257037621, Entry(wide, "lower"), Tolerance);
        Assert.Equal(13.28466074296238, Entry(wide, "upper"), Tolerance);

        var clustered = Metrics.Ci(cluster: "c").Compute(Clustered(20, i => i % 4));
        Assert.Equal(7.445739743239481, Entry(clustered, "lower"), Tolerance);
        Assert.Equal(11.554260256760518, Entry(clustered, "upper"), Tolerance);
        Assert.Equal("ci", Metrics.Ci().Name);
    }

    [Fact]
    public void ci_collapses_to_a_point_below_two_scores_and_validates_arguments()
    {
        var single = Metrics.Ci().Compute([Sample(3.0)]);
        Assert.Equal(3.0, Entry(single, "lower"));
        Assert.Equal(3.0, Entry(single, "upper"));
        var empty = Metrics.Ci().Compute([]);
        Assert.Equal(0.0, Entry(empty, "lower"));
        Assert.Equal(0.0, Entry(empty, "upper"));

        Assert.Throws<ArgumentOutOfRangeException>(() => Metrics.Ci(level: 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Metrics.Ci(level: 0.0));
        Assert.Throws<ArgumentException>(() => Metrics.Ci(method: "nope"));
        Assert.Throws<ArgumentException>(() => Metrics.Ci(cluster: "c").Compute([Sample(1)]));
        Assert.Throws<ArgumentException>(() => Metrics.Ci(cluster: "c").Compute([Sample(1), Sample(0)]));
        var validSingle = Metrics.Ci(cluster: "c").Compute([Sample(3.0, "c", "a")]);
        Assert.Equal(3.0, Entry(validSingle, "lower"));
    }

    [Fact]
    public void ci_bootstrap_brackets_the_mean_and_tracks_the_t_interval()
    {
        var interval = Metrics.Ci(method: "bootstrap", numSamples: 2000, random: new Random(7)).Compute(Range(50));
        Assert.True(Entry(interval, "lower") < 24.5 && 24.5 < Entry(interval, "upper"));

        var analytic = Metrics.Ci().Compute(Range(100));
        var boot = Metrics.Ci(method: "bootstrap", numSamples: 4000, random: new Random(11)).Compute(Range(100));
        Assert.Equal(Entry(analytic, "lower"), Entry(boot, "lower"), 1.5);
        Assert.Equal(Entry(analytic, "upper"), Entry(boot, "upper"), 1.5);

        var clustered = Metrics.Ci(method: "bootstrap", cluster: "c", numSamples: 500, random: new Random(3)).Compute(Clustered(40, i => i % 5, i => i % 3));
        Assert.True(Entry(clustered, "lower") <= Entry(clustered, "upper"));
    }

    [Fact]
    public void bootstrap_stderr_approximates_the_standard_error_and_handles_degenerate_input()
    {
        var metric = Metrics.BootstrapStderr(random: new Random(42));

        Assert.Equal(0.908, Num(metric.Compute(Range(10))), 0.15);
        Assert.Equal(0.0, Num(metric.Compute([])));
        Assert.Equal(0.0, Num(metric.Compute([Sample(4)])));
        Assert.Equal(0.0, Num(metric.Compute([Sample(1), Sample(1), Sample(1)])));
        Assert.Equal(0.0, Num(metric.Compute([new SampleScore(Score.Unscored())])));
        Assert.Equal(0.0, Num(Metrics.BootstrapStderr(numSamples: 0).Compute([])));
        Assert.Throws<ArgumentOutOfRangeException>(() => Metrics.BootstrapStderr(numSamples: -1));
        Assert.Equal("bootstrap_stderr", metric.Name);
    }

    // ---- ci_wilson ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(8, 10, 0.95, 0.4901624715366419, 0.9433178485456246)]
    [InlineData(0, 20, 0.95, 0.0, 0.16112515805281927)]
    [InlineData(20, 20, 0.95, 0.8388748419471808, 1.0)]
    [InlineData(5, 50, 0.90, 0.04952903447744518, 0.19153751415157094)]
    [InlineData(5, 50, 0.99, 0.033990883778418315, 0.259730789595684)]
    [InlineData(49, 100, 0.95, 0.3942199893044114, 0.5865198806597283)]
    [InlineData(1, 1, 0.95, 0.2065493143772375, 1.0)]
    public void ci_wilson_matches_python(int successes, int n, double level, double lower, double upper)
    {
        var interval = Metrics.CiWilson(level: level).Compute(Binary(successes, n));

        Assert.Equal(lower, Entry(interval, "lower"), Tolerance);
        Assert.Equal(upper, Entry(interval, "upper"), Tolerance);
    }

    [Fact]
    public void ci_wilson_graded_symmetric_bounded_and_empty()
    {
        var graded = Metrics.CiWilson().Compute([Sample(0.0), Sample(0.5), Sample(0.5), Sample(1.0)]);
        Assert.Equal(0.15003898915214958, Entry(graded, "lower"), Tolerance);
        Assert.Equal(0.8499610108478504, Entry(graded, "upper"), Tolerance);

        var half = Metrics.CiWilson().Compute(Binary(10, 20));
        Assert.Equal(1.0 - Entry(half, "upper"), Entry(half, "lower"), 1e-12);

        var nineteen = Metrics.CiWilson().Compute(Binary(19, 20));
        Assert.True(Entry(Metrics.Ci().Compute(Binary(19, 20)), "upper") > 1.0);
        Assert.True(Entry(nineteen, "upper") <= 1.0);

        var empty = Metrics.CiWilson().Compute([]);
        Assert.Equal(0.0, Entry(empty, "lower"));
        Assert.Equal(0.0, Entry(empty, "upper"));

        var extreme = Metrics.CiWilson(level: Math.BitDecrement(1.0)).Compute(Binary(5, 10));
        Assert.True(0.0 <= Entry(extreme, "lower") && Entry(extreme, "lower") < Entry(extreme, "upper") && Entry(extreme, "upper") <= 1.0);
        Assert.Equal("ci_wilson", Metrics.CiWilson().Name);
    }

    [Fact]
    public void ci_wilson_clustered_matches_python()
    {
        var correlated = Metrics.CiWilson(cluster: "c").Compute(Enumerable.Range(0, 6).SelectMany(c => Enumerable.Repeat(Sample(c % 2, "c", c), 4)).ToList());
        Assert.Equal(0.12275388276951793, Entry(correlated, "lower"), Tolerance);
        Assert.Equal(0.8772461172304821, Entry(correlated, "upper"), Tolerance);

        var pair = Metrics.CiWilson(cluster: "c").Compute([Sample(0.0, "c", "a"), Sample(0.0, "c", "a"), Sample(1.0, "c", "b"), Sample(1.0, "c", "b")]);
        Assert.Equal(0.0015413331334360736, Entry(pair, "lower"), Tolerance);
        Assert.Equal(0.9984586668665639, Entry(pair, "upper"), Tolerance);

        var singletons = Metrics.CiWilson(cluster: "c").Compute(Clustered(4, i => i, i => i % 2));
        Assert.Equal(0.06083027592009754, Entry(singletons, "lower"), Tolerance);
        Assert.Equal(0.9391697240799024, Entry(singletons, "upper"), Tolerance);

        var capped = Metrics.CiWilson(cluster: "c").Compute(Clustered(16, i => i / 2, i => i % 2).Concat([Sample(1.0, "c", "x"), Sample(0.0, "c", "y")]).ToList());
        Assert.Equal(0.26475320946855097, Entry(capped, "lower"), Tolerance);
        Assert.Equal(0.735246790531449, Entry(capped, "upper"), Tolerance);

        var zeroVariance = Metrics.CiWilson(cluster: "c").Compute(Clustered(20, i => i / 2, i => i % 2));
        Assert.Equal(0.27431337274918, Entry(zeroVariance, "lower"), Tolerance);
        Assert.Equal(0.72568662725082, Entry(zeroVariance, "upper"), Tolerance);

        var degenerate = Metrics.CiWilson(cluster: "c").Compute(Clustered(20, i => i / 5, _ => 1.0));
        Assert.Equal(0.6638350894659499, Entry(degenerate, "lower"), Tolerance);
        Assert.Equal(1.0, Entry(degenerate, "upper"), Tolerance);

        var extreme = Metrics.CiWilson(level: Math.BitDecrement(1.0), cluster: "c").Compute(Clustered(16, i => i % 4, i => i % 2));
        Assert.True(0.0 <= Entry(extreme, "lower") && Entry(extreme, "lower") <= Entry(extreme, "upper") && Entry(extreme, "upper") <= 1.0);
    }

    [Fact]
    public void ci_wilson_validates_range_levels_and_clusters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Metrics.CiWilson(level: 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Metrics.CiWilson(level: 1.5));
        Assert.Contains("0 and 1", Assert.Throws<ArgumentException>(() => Metrics.CiWilson().Compute([Sample(2.0)])).Message);
        Assert.Contains("0 and 1", Assert.Throws<ArgumentException>(() => Metrics.CiWilson().Compute([Sample(0.0), Sample(-0.5)])).Message);
        Assert.Contains("at least two clusters", Assert.Throws<ArgumentException>(() => Metrics.CiWilson(cluster: "c").Compute(Clustered(10, _ => "only", i => i % 2))).Message);
        Assert.Contains("at least two clusters", Assert.Throws<ArgumentException>(() => Metrics.CiWilson(cluster: "c").Compute([])).Message);
        Assert.Contains("has no cluster metadata", Assert.Throws<ArgumentException>(() => Metrics.CiWilson(cluster: "c").Compute([Sample(1.0)])).Message);
        Assert.Contains("has no cluster metadata", Assert.Throws<ArgumentException>(() => Metrics.CiWilson(cluster: "c").Compute([Sample(1.0, "c", double.NaN), Sample(0.0, "c", "a")])).Message);
    }

    [Fact]
    public void ci_wilson_converts_each_value_exactly_once()
    {
        var calls = 0;
        double Counting(ScoreValue value)
        {
            calls++;
            return Num(value);
        }

        var scores = Clustered(12, i => i % 3, i => i % 2);
        Metrics.CiWilson(toFloat: Counting, cluster: "c").Compute(scores);
        Assert.Equal(12, calls);
        calls = 0;
        Metrics.CiWilson(toFloat: Counting).Compute(scores);
        Assert.Equal(12, calls);
    }

    // ---- krippendorff -------------------------------------------------------------------------------

    [Fact]
    public void krippendorff_nominal_matches_python()
    {
        var alpha = Metrics.KrippendorffAlpha();

        Assert.Equal(1.0, Num(alpha.Compute(Units(L(1, 1, 1), L(0, 0, 0), L(1, 1, 1)))));
        Assert.Equal(-0.75, Num(alpha.Compute(Units(L(0, 1), L(0, 1), L(1, 0), L(1, 0)))), Tolerance);
        Assert.Equal(0.6857142857142857, Num(alpha.Compute(Units(L(1, 1, 1), L(0, 0, 0), L(1, 1, 1), L(0, 0, 1)))), Tolerance);
        Assert.Equal(1.0, Num(alpha.Compute(Units(L("a", "a"), L("b", "b"), L("c", "c")))));
        Assert.Equal(1.0, Num(alpha.Compute(Units(L(5, 5), L(5, 5), L(5, 5)))));
        Assert.Equal(0.33333333333333326, Num(alpha.Compute(Units(L(1, 1, 0), L(0, 0)))), Tolerance);
        Assert.True(alpha.Compute([]).IsNaN);
        Assert.Equal(1.0, Num(alpha.Compute([Sample(1), Sample(L(1, 1, 1)), Sample(L(0, 0, 0))])));
        Assert.Equal(1.0, Num(alpha.Compute(Units(L(1, true), L(0, false)))));
        Assert.Equal("krippendorff_alpha", alpha.Name);
    }

    [Fact]
    public void krippendorff_interval_and_ordinal_match_python()
    {
        var interval = Metrics.KrippendorffAlpha("interval");
        var ordinal = Metrics.KrippendorffAlpha("ordinal");

        Assert.Equal(0.7916666666666667, Num(interval.Compute(Units(L(1.0, 2.0), L(3.0, 4.0), L(1.0, 1.0)))), Tolerance);
        Assert.Equal(1.0, Num(interval.Compute(Units(L(1, 1), L(2, 2), L(3, 3)))));
        Assert.Equal(1.0, Num(Metrics.KrippendorffAlpha("interval", ValueToFloat.Default).Compute(Units(L("C", "C"), L("I", "I")))));
        Assert.Equal(0.4444444444444444, Num(Metrics.KrippendorffAlpha("interval", ValueToFloat.Default).Compute(Units(L("C", "C"), L("I", "I"), L("C", "I")))), Tolerance);
        Assert.Equal(0.7, Num(interval.Compute(Units(L("n/a"), L(1.0, 2.0), L(3.0, 4.0)))), Tolerance);
        Assert.Equal(0.9494949494949495, Num(ordinal.Compute(Units(L(1, 1), L(3, 4), L(5, 5)))), Tolerance);
        Assert.Equal(1.0, Num(ordinal.Compute(Units(L(1, 1, 1), L(3, 3, 3), L(5, 5, 5)))));

        ScoreValue[] skewed = [L(1, 1, 5), L(5, 5, 5), L(4, 5, 5), L(4, 4, 5)];
        Assert.Equal(0.1342592592592593, Num(ordinal.Compute(Units(skewed))), Tolerance);
        Assert.Equal(0.3377926421404682, Num(interval.Compute(Units(skewed))), Tolerance);
        Assert.Equal(0.19512195121951215, Num(Metrics.KrippendorffAlpha().Compute(Units(skewed))), Tolerance);

        var ordering = new Dictionary<string, double> { ["low"] = 0, ["medium"] = 1, ["high"] = 2 };
        var text = Metrics.KrippendorffAlpha("ordinal", v => ordering[((ScoreValue.Str)v).Value]);
        Assert.Equal(1.0, Num(text.Compute(Units(L("low", "low"), L("medium", "medium"), L("high", "high")))));
    }

    [Fact]
    public void krippendorff_validates_levels_and_non_numeric_ratings_and_warns()
    {
        Assert.Contains("unsupported level", Assert.Throws<ArgumentException>(() => Metrics.KrippendorffAlpha("ratio")).Message);
        Assert.Contains("non-numeric rating", Assert.Throws<ArgumentException>(() => Metrics.KrippendorffAlpha("interval").Compute(Units(L("low", "low"), L("high", "high")))).Message);
        Assert.Contains("non-numeric rating", Assert.Throws<ArgumentException>(() => Metrics.KrippendorffAlpha("ordinal").Compute(Units(L("low", "low"), L("high", "high")))).Message);

        var before = ProviderLogger.Warnings.Count;
        Metrics.KrippendorffAlpha().Compute(Units(L(1, 1), L(3, 3), L(5, 5)));
        Assert.Contains(WarningsSince(before), w => w.Contains("nominal level applied to numeric data with 3 distinct values", StringComparison.Ordinal));

        before = ProviderLogger.Warnings.Count;
        Metrics.KrippendorffAlpha().Compute(Units(L(0, 1), L(1, 1), L(0, 0)));
        Metrics.KrippendorffAlpha().Compute(Units(L("a", "a"), L("b", "b"), L("c", "c")));
        Assert.DoesNotContain(WarningsSince(before), w => w.Contains("nominal level applied", StringComparison.Ordinal));

        before = ProviderLogger.Warnings.Count;
        Metrics.KrippendorffAlpha().Compute([Sample(1), Sample(L(1)), Sample(L(1, 1))]);
        var warnings = WarningsSince(before);
        Assert.Contains(warnings, w => w.Contains("skipped 1 sample(s) with a non-sequence", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("skipped 1 sample(s) with fewer than 2 ratings", StringComparison.Ordinal));
    }

    // ---- grouped ------------------------------------------------------------------------------------

    private static SampleScore Grouped(ScoreValue value, object? group, string key = "group") => Sample(value, key, group);

    [Fact]
    public void grouped_matches_python()
    {
        IReadOnlyList<SampleScore> scores = [Grouped(1, "A"), Grouped(1, "A"), Grouped(4, "A"), Grouped(2, "B"), Grouped(6, "B"), Grouped(10, "B")];

        var result = Metrics.Grouped(Metrics.Mean(), "group").Compute(scores);
        Assert.Equal(2.0, Entry(result, "A"));
        Assert.Equal(6.0, Entry(result, "B"));
        Assert.Equal(4.0, Entry(result, "all"));
        Assert.Equal(["A", "B", "all"], Dict(result).Keys);

        var custom = Metrics.Grouped(Metrics.Mean(), "group", allLabel: "custom_all").Compute(scores);
        Assert.Equal(4.0, Entry(custom, "custom_all"));

        var template = Metrics.Grouped(Metrics.Mean(), "group", nameTemplate: "mean_{group_name}", allLabel: "mean_all").Compute(scores);
        Assert.Equal(2.0, Entry(template, "mean_A"));
        Assert.Equal(6.0, Entry(template, "mean_B"));
        Assert.Equal(4.0, Entry(template, "mean_all"));
        Assert.Equal("grouped", Metrics.Grouped(Metrics.Mean(), "group").Name);
    }

    [Fact]
    public void grouped_accuracy_samples_groups_and_none()
    {
        IReadOnlyList<SampleScore> scores = [Grouped("I", "A"), Grouped("C", "B"), Grouped("I", "B"), Grouped("I", "C"), Grouped("C", "C"), Grouped("C", "D"), Grouped("C", "D"), Grouped("C", "D")];

        var groups = Metrics.Grouped(Metrics.Accuracy(), "group", GroupedAll.Groups).Compute(scores);
        Assert.Equal(0.0, Entry(groups, "A"));
        Assert.Equal(0.5, Entry(groups, "B"));
        Assert.Equal(0.5, Entry(groups, "C"));
        Assert.Equal(1.0, Entry(groups, "D"));
        Assert.Equal(0.5, Entry(groups, "all"));

        var samples = Metrics.Grouped(Metrics.Accuracy(), "group").Compute(scores);
        Assert.Equal(5.0 / 8.0, Entry(samples, "all"), Tolerance);

        var none = Metrics.Grouped(Metrics.Accuracy(), "group", GroupedAll.None).Compute(scores);
        Assert.Equal(["A", "B", "C", "D"], Dict(none).Keys);
    }

    [Fact]
    public void grouped_names_groups_with_python_str_and_nests_inner_dictionaries()
    {
        IReadOnlyList<SampleScore> scores = [Grouped(0.1, 1, "g"), Grouped(0.4, 1, "g"), Grouped(0.9, 1, "g"), Grouped(0.2, 2.5, "g"), Grouped(0.8, 2.5, "g"), Grouped(1.0, true, "g")];

        var result = Metrics.Grouped(Metrics.Stderr(), "g", nameTemplate: "se_{group_name}", allLabel: "se_all").Compute(scores);
        Assert.Equal(["se_1", "se_2.5", "se_True", "se_all"], Dict(result).Keys);
        Assert.Equal(0.23333333333333336, Entry(result, "se_1"), Tolerance);
        Assert.Equal(0.3, Entry(result, "se_2.5"), Tolerance);
        Assert.Equal(0.0, Entry(result, "se_True"));
        Assert.Equal(0.15634719199411434, Entry(result, "se_all"), Tolerance);

        var nested = Metrics.Grouped(Metrics.Ci(), "g", GroupedAll.None).Compute(scores);
        Assert.Equal(["lower", "upper"], Dict(Dict(nested)["1"]!).Keys);
    }

    [Fact]
    public void grouped_rejects_missing_metadata_and_label_collisions_and_handles_empty_input()
    {
        var metric = Metrics.Grouped(Metrics.Mean(), "group");

        Assert.Contains("has no group metadata", Assert.Throws<ArgumentException>(() => metric.Compute([Grouped(1, "A"), Sample(1)])).Message);
        Assert.Contains("all_label", Assert.Throws<ArgumentException>(() => Metrics.Grouped(Metrics.Accuracy(), "category").Compute([Grouped("C", "easy", "category"), Grouped("I", "all", "category")])).Message);

        var samples = Metrics.Grouped(Metrics.Mean(), "group", GroupedAll.Samples).Compute([]);
        Assert.Equal(0.0, Entry(samples, "all"));
        Assert.Single(Dict(samples));
        var groups = Metrics.Grouped(Metrics.Mean(), "group", GroupedAll.Groups).Compute([]);
        Assert.Equal(0.0, Entry(groups, "all"));
    }

    // ---- frequency / categorical --------------------------------------------------------------------

    [Fact]
    public void frequency_matches_python()
    {
        string[] verdicts = ["yes", "no", "unsure"];

        var explicitCategories = Metrics.Frequency(verdicts).Compute([Sample("yes"), Sample("yes"), Sample("no")]);
        Assert.Equal(["yes", "no", "unsure"], Dict(explicitCategories).Keys);
        Assert.Equal(2.0 / 3.0, Entry(explicitCategories, "yes"), Tolerance);
        Assert.Equal(1.0 / 3.0, Entry(explicitCategories, "no"), Tolerance);
        Assert.Equal(0.0, Entry(explicitCategories, "unsure"));

        var observed = Metrics.Frequency().Compute([Sample("yes"), Sample("yes"), Sample("no"), Sample("weird")]);
        Assert.Equal(["yes", "no", "weird"], Dict(observed).Keys);
        Assert.Equal(0.5, Entry(observed, "yes"));
        Assert.Equal(0.25, Entry(observed, "weird"));

        var unexpected = Metrics.Frequency(verdicts).Compute([Sample("yes"), Sample("no"), Sample("weird")]);
        Assert.Equal(["yes", "no", "unsure", "weird"], Dict(unexpected).Keys);

        var counts = Metrics.Frequency(verdicts, normalize: false).Compute([Sample("yes"), Sample("yes"), Sample("yes"), Sample("no")]);
        Assert.Equal(3.0, Entry(counts, "yes"));
        Assert.Equal(1.0, Entry(counts, "no"));
        Assert.Equal(0.0, Entry(counts, "unsure"));

        var empty = Metrics.Frequency(verdicts).Compute([]);
        Assert.All(Dict(empty).Values, v => Assert.Equal(0.0, Num(v!)));

        var mixed = Metrics.Frequency().Compute([Sample(1), Sample(true), Sample(1.0), Sample("1"), Sample(2.5), new SampleScore(Score.Unscored())]);
        Assert.Equal(["1", "True", "2.5"], Dict(mixed).Keys);
        Assert.Equal(0.6, Entry(mixed, "1"), Tolerance);
    }

    [Fact]
    public void frequency_is_unreduced_and_rejects_containers_and_categorical_wraps_it()
    {
        var metric = Metrics.Frequency();

        Assert.Equal("frequency", metric.Name);
        Assert.Equal(MetricScores.Unreduced, metric.Scores);
        Assert.Equal(MetricScores.Auto, Metrics.Accuracy().Scores);
        Assert.Contains("dict-valued", Assert.Throws<ArgumentException>(() => metric.Compute([Sample(D(("k", "yes")))])).Message);
        Assert.Contains("list-valued", Assert.Throws<ArgumentException>(() => metric.Compute([Sample(L("yes", "no"))])).Message);

        var categorical = Metrics.Categorical(["yes", "no"]);
        Assert.Single(categorical);
        Assert.Equal("frequency", categorical[0].Name);
        Assert.Equal(["yes", "no"], Dict(categorical[0].Compute([Sample("no")])).Keys);
    }

    // ---- aggregate ----------------------------------------------------------------------------------

    [Fact]
    public void aggregate_matches_python()
    {
        IReadOnlyList<SampleScore> scores = [Sample(D(("x", 1), ("y", 10))), Sample(D(("x", 3), ("y", 20)))];

        Assert.Equal(2.0, Num(Metrics.Aggregate("x", Metrics.Mean()).Compute(scores)));
        Assert.Equal(15.0, Num(Metrics.Aggregate("y", Metrics.Mean()).Compute(scores)));
        Assert.Equal(0.22867371223353739, Num(Metrics.Aggregate("x", Metrics.Stderr()).Compute([Sample(D(("x", 0.2))), Sample(D(("x", 0.7))), Sample(D(("x", 1.0))), Sample(D(("x", 0.0)))])), Tolerance);
        Assert.Equal(2.0 / 3.0, Num(Metrics.Aggregate("x", Metrics.Mean(), onMissing: "zero").Compute([Sample(D(("x", 2))), Sample(D(("y", 1))), Sample(D(("x", null)))])), Tolerance);
        Assert.Equal(3.0, Num(Metrics.Aggregate("x", Metrics.Mean(), onMissing: "skip").Compute([Sample(D(("x", 2))), Sample(D(("y", 1))), Sample(D(("x", 4)))])));
        Assert.Equal(0.5, Num(Metrics.Aggregate("v", Metrics.Mean(), ValueToFloat.Default).Compute([Sample(D(("v", "C"))), Sample(D(("v", "I"))), Sample(D(("v", "P")))])), Tolerance);
        Assert.Equal("aggregate", Metrics.Aggregate("x", Metrics.Mean()).Name);
    }

    [Fact]
    public void aggregate_passes_raw_values_to_the_inner_metric_and_skips_nan()
    {
        var inner = new MetricDef("inner", scores => scores.Count(s => s.Score.Value is ScoreValue.Str { Value: "C" }));
        Assert.Equal(2.0, Num(Metrics.Aggregate("v", inner).Compute([Sample(D(("v", "C"))), Sample(D(("v", "I"))), Sample(D(("v", "C")))])));

        Assert.Equal(2.0, Num(Metrics.Aggregate("x", Metrics.Mean()).Compute([Sample(D(("x", 1))), Sample(D(("x", double.NaN))), Sample(D(("x", 3)))])));
        Assert.True(Metrics.Aggregate("x", Metrics.Mean()).Compute([Sample(D(("x", double.NaN)))]).IsNaN);
        Assert.Equal(1.0, Num(Metrics.Aggregate("x", Metrics.Mean(), onMissing: "zero").Compute([Sample(D(("x", 1))), Sample(D(("x", double.NaN)))])));
        Assert.Equal(2.0, Num(Metrics.Aggregate("x", Metrics.Mean()).Compute([Sample(D(("x", 2))), new SampleScore(Score.Unscored())])));
        Assert.True(Metrics.Aggregate("x", Metrics.Mean()).Compute([new SampleScore(Score.Unscored())]).IsNaN);
        Assert.True(Metrics.Aggregate("x", Metrics.Mean(), onMissing: "skip").Compute([Sample(D(("y", 1)))]).IsNaN);
    }

    [Fact]
    public void aggregate_rejects_missing_keys_non_dict_values_and_bad_on_missing()
    {
        var metric = Metrics.Aggregate("x", Metrics.Mean());

        Assert.Contains("is missing", Assert.Throws<ArgumentException>(() => metric.Compute([Sample(D(("y", 1)), id: "s1")])).Message);
        Assert.Contains("is None", Assert.Throws<ArgumentException>(() => metric.Compute([Sample(D(("x", null)))])).Message);
        Assert.Contains("non-dict score value", Assert.Throws<ArgumentException>(() => metric.Compute([Sample(1)])).Message);
        Assert.Contains("on_missing", Assert.Throws<ArgumentException>(() => Metrics.Aggregate("x", Metrics.Mean(), onMissing: "skpi")).Message);
    }

    // ---- perplexity ---------------------------------------------------------------------------------

    private static SampleScore Perplexity(int tokens, double sumLogProbs) =>
        new(new Score(0) { Metadata = new Dictionary<string, object?> { ["num_tokens"] = tokens, ["sum_log_probs"] = sumLogProbs } });

    [Fact]
    public void perplexity_metrics_match_python()
    {
        IReadOnlyList<SampleScore> scores = [Perplexity(4, -8.0), Perplexity(6, -3.0)];

        Assert.Equal(3.0041660239464334, Num(Metrics.PerplexityPerToken().Compute(scores)), Tolerance);
        Assert.Equal(3.4903429574618414, Num(Metrics.PerplexityPerSeq().Compute(scores)), Tolerance);
        Assert.Equal("perplexity_per_token", Metrics.PerplexityPerToken().Name);
        Assert.Equal("perplexity_per_seq", Metrics.PerplexityPerSeq().Name);
    }

    [Fact]
    public void perplexity_metrics_warn_on_missing_metadata_and_handle_overflow()
    {
        var before = ProviderLogger.Warnings.Count;

        Assert.True(Metrics.PerplexityPerToken().Compute([Sample(0)]).IsNaN);
        Assert.True(Metrics.PerplexityPerSeq().Compute([Sample(0)]).IsNaN);
        Assert.Equal(2, WarningsSince(before).Count(w => w.Contains("missing metadata keys", StringComparison.Ordinal)));
        Assert.Equal(3.0041660239464334, Num(Metrics.PerplexityPerToken().Compute([Perplexity(4, -8.0), Perplexity(6, -3.0), Sample(0)])), Tolerance);
        Assert.True(double.IsPositiveInfinity(Num(Metrics.PerplexityPerToken().Compute([Perplexity(1, -1000.0)]))));
        Assert.True(Metrics.PerplexityPerToken().Compute([]).IsNaN);
    }

    // ---- reducers -----------------------------------------------------------------------------------

    [Fact]
    public void pass_k_matches_python()
    {
        Score[] scores = [S(6), S(0), S(0), S(0), S(8), S(4)];

        Assert.Equal(0.2, Num(Reducers.PassK(2)(scores)), Tolerance);
        Assert.Equal(0.05, Num(Reducers.PassK(3, 2)(scores)), Tolerance);
        Assert.Equal(0.0, Num(Reducers.PassK(5)(scores)));
        Assert.Equal(0.0, Num(Reducers.PassK(5, 2)(scores)));
        Assert.Equal(0.5, Num(Reducers.PassK(1)(scores)), Tolerance);
        Assert.Equal(0.2, Num(Reducers.PassK(2)([.. scores, S(double.NaN)])), Tolerance);

        Score[] ten = [S(1), S(0), S(1), S(1), S(0), S(0), S(1), S(1), S(1), S(0)];
        double[] expectedPassK = [0.6, 0.3333333333333333, 0.16666666666666666, 0.07142857142857142, 0.023809523809523808];
        double[] expectedPassAt = [0.5999999999999999, 0.8666666666666667, 0.9666666666666667, 0.9952380952380953, 1.0];
        for (var k = 1; k <= 5; k++)
        {
            Assert.Equal(expectedPassK[k - 1], Num(Reducers.PassK(k)(ten)), Tolerance);
            Assert.Equal(expectedPassAt[k - 1], Num(Reducers.PassAt(k)(ten)), Tolerance);
        }

        Assert.Equal("pass_k_2", Reducers.NameOf(Reducers.PassK(2)));
    }

    [Fact]
    public void pass_k_is_undefined_below_k_scored_epochs_and_reduces_containers()
    {
        Assert.True(Reducers.PassK(3)([S(1.0), S(1.0), S(double.NaN), S(double.NaN)]).Value.IsNaN);
        Assert.Equal(1.0, Num(Reducers.PassK(3)([S(1.0), S(1.0), S(1.0)])));
        Assert.True(Reducers.PassK(2)([]).Value.IsNaN);
        Assert.True(Reducers.PassK(2)([S(double.NaN)]).Value.IsNaN);

        Score[] lists = [S(L(1, 2)), S(L(4, 3)), S(L(3, 1)), S(L(1, 2)), S(L(1, 2))];
        Assert.Equal(L(1, 1), Reducers.PassK(2)(lists).Value);
        Score[] dicts = [S(D(("coolness", 5), ("spiciness", 1))), S(D(("coolness", 4), ("spiciness", 1))), S(D(("coolness", 3), ("spiciness", 1))), S(D(("coolness", 2), ("spiciness", 1))), S(D(("coolness", 1), ("spiciness", 21)))];
        Assert.Equal(D(("coolness", 1), ("spiciness", 1)), Reducers.PassK(2)(dicts).Value);
        Assert.Contains("mismatched lengths", Assert.Throws<ArgumentException>(() => Reducers.PassK(2)([S(L(1, 2, 3)), S(L(1, 2))])).Message);
    }

    [Fact]
    public void majority_requires_more_than_half_and_unscored_withholds_a_vote()
    {
        var majority = Reducers.Majority();
        var correct = S("C");
        var incorrect = S("I");
        var partial = S("P");
        var unscored = Score.Unscored(metadata: new Dictionary<string, object?> { ["unscored_reason"] = "grade_parse_failure" });

        Assert.Equal("C", majority([correct, correct, incorrect]).Value.Text);
        Assert.True(majority([correct, incorrect, partial]).Value.IsNaN);
        Assert.True(majority([correct, incorrect]).Value.IsNaN);
        Assert.True(majority([incorrect, correct]).Value.IsNaN);
        Assert.Equal("C", majority([correct, unscored, correct]).Value.Text);
        Assert.True(majority([correct, unscored, incorrect]).Value.IsNaN);
        Assert.True(majority([incorrect, unscored, correct]).Value.IsNaN);
        Assert.Equal("C", Reducers.Mode()([correct, unscored, incorrect]).Value.Text);
        Assert.Equal("I", Reducers.Mode()([incorrect, unscored, correct]).Value.Text);
        Assert.Equal("majority", Reducers.NameOf(majority));
    }

    [Fact]
    public void majority_records_the_panel_in_metadata()
    {
        Score[] scores =
        [
            new Score("C") { Metadata = new Dictionary<string, object?> { ["grader"] = "a" } },
            Score.Unscored(explanation: "Grade not found in model output: nope", metadata: new Dictionary<string, object?> { ["unscored_reason"] = "grade_parse_failure" }),
            new Score("C"),
        ];

        var reduced = Reducers.Majority()(scores);

        Assert.Equal("C", reduced.Value.Text);
        var panel = Assert.IsType<Dictionary<string, object?>>(reduced.Metadata!["panel"]);
        Assert.Equal(["C", null, "C"], Assert.IsType<List<object?>>(panel["votes"]));
        Assert.Equal(3, panel["size"]);
        var failure = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<List<object?>>(panel["failures"])));
        Assert.Equal(1, failure["index"]);
        Assert.Equal("grade_parse_failure", failure["reason"]);
        Assert.Equal("Grade not found in model output: nope", failure["explanation"]);
        Assert.Equal("a", reduced.Metadata["grader"]);
        Assert.Single(scores[0].Metadata!);

        var withReason = Reducers.Majority()([Score.Unscored(reason: ScoreReason.Refusal), new Score(1)]);
        var reasonFailure = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<List<object?>>(Assert.IsType<Dictionary<string, object?>>(withReason.Metadata!["panel"])["failures"])[0]);
        Assert.Equal("refusal", reasonFailure["reason"]);
    }

    [Fact]
    public void majority_reduces_dicts_and_lists_per_key_against_the_panel_size()
    {
        var majority = Reducers.Majority();

        var dict = Dict(majority([S(D(("cool", 1), ("spicy", 1))), S(D(("cool", 1), ("spicy", 2))), S(D(("cool", 2), ("spicy", 3)))]).Value);
        Assert.Equal(1.0, Num(dict["cool"]!));
        Assert.True(dict["spicy"]!.IsNaN);

        var list = ((ScoreValue.List)majority([S(L(1, 1)), S(L(1, 2)), S(L(2, 3))]).Value).Items;
        Assert.Equal(1.0, Num(list[0]));
        Assert.True(list[1].IsNaN);

        var withRootNan = majority([S(D(("a", 1), ("b", 1))), S(D(("a", 1), ("b", double.NaN))), S(double.NaN)]);
        Assert.Equal(1.0, Num(Dict(withRootNan.Value)["a"]!));
        Assert.True(Dict(withRootNan.Value)["b"]!.IsNaN);
        var votes = Assert.IsType<List<object?>>(Assert.IsType<Dictionary<string, object?>>(withRootNan.Metadata!["panel"])["votes"]);
        Assert.Equal(1.0, Assert.IsType<Dictionary<string, object?>>(votes[0])["a"]);
        Assert.Null(votes[2]);

        var allUnscored = majority([Score.Unscored(), Score.Unscored()]);
        Assert.True(allUnscored.Value.IsNaN);
        Assert.Equal([null, null], Assert.IsType<List<object?>>(Assert.IsType<Dictionary<string, object?>>(allUnscored.Metadata!["panel"])["votes"]));
    }

    [Fact]
    public void collect_gathers_scalars_drops_nan_and_rejects_containers()
    {
        var collect = Reducers.Collect();

        Assert.Equal(L(1, 0, 1), collect([S(1), S(0), S(1)]).Value);
        Assert.Equal(L("a", "b"), collect([S("a"), S("b")]).Value);
        Assert.Equal(L(1, 0), collect([S(1), S(double.NaN), S(0)]).Value);
        Assert.True(collect([S(double.NaN), S(double.NaN)]).Value.IsNaN);
        Assert.True(collect([]).Value.IsNaN);
        Assert.Contains("requires scalar score values", Assert.Throws<ArgumentException>(() => collect([S(1), S(D(("a", 1)))])).Message);
        Assert.Contains("requires scalar score values", Assert.Throws<ArgumentException>(() => collect([S(1), S(L(1, 2))])).Message);

        var same = new Score(1) { Answer = "1", Explanation = "An explanation", Metadata = new Dictionary<string, object?> { ["foo"] = "bar" } };
        var reduced = collect([same, same with { Value = 0 }]);
        Assert.Equal(L(1, 0), reduced.Value);
        Assert.Equal("1", reduced.Answer);
        Assert.Equal("An explanation", reduced.Explanation);
        Assert.Same(same.Metadata, reduced.Metadata);
        Assert.Equal("collect", Reducers.NameOf(collect));
    }

    [Fact]
    public void create_resolves_registry_names_and_k_shorthand()
    {
        Score[] scores = [S(1), S(0), S(1)];

        Assert.Equal("mean", Reducers.NameOf(Reducers.Create("mean")));
        Assert.Equal("median", Reducers.NameOf(Reducers.Create("median")));
        Assert.Equal("mode", Reducers.NameOf(Reducers.Create("mode")));
        Assert.Equal("majority", Reducers.NameOf(Reducers.Create("majority")));
        Assert.Equal("max", Reducers.NameOf(Reducers.Create("max")));
        Assert.Equal(L(1, 0, 1), Reducers.Create("collect")(scores).Value);
        Assert.Equal("at_least_2", Reducers.NameOf(Reducers.Create("at_least_2")));
        Assert.Equal("pass_at_3", Reducers.NameOf(Reducers.Create("pass_at_3")));
        Assert.Equal("pass_k_3", Reducers.NameOf(Reducers.Create("pass_k_3")));
        Assert.Equal(1.0, Num(Reducers.Create("at_least_2")(scores)));
        Assert.Contains("requires a k suffix", Assert.Throws<ArgumentException>(() => Reducers.Create("at_least")).Message);
        Assert.Contains("Unknown score reducer", Assert.Throws<ArgumentException>(() => Reducers.Create("top_5")).Message);
        Assert.Contains("Unknown score reducer", Assert.Throws<ArgumentException>(() => Reducers.Create("bogus")).Message);
    }

    [Fact]
    public void validate_rejects_k_reducers_that_need_more_epochs()
    {
        Reducers.Validate(5, Reducers.PassK(5));
        Reducers.Validate(8, Reducers.PassK(3));
        Reducers.Validate(1, Reducers.Mean());
        Reducers.Validate(1, scores => scores[0]);

        Assert.Contains("pass_k_5", Assert.Throws<PrerequisiteError>(() => Reducers.Validate(3, Reducers.PassK(5))).Message);
        Assert.Contains("at_least_4", Assert.Throws<PrerequisiteError>(() => Reducers.Validate(3, Reducers.AtLeast(4))).Message);
        Assert.Contains("pass_at_2", Assert.Throws<PrerequisiteError>(() => Reducers.Validate(1, Reducers.PassAt(2))).Message);
    }

    // ---- results builder: unreduced views -----------------------------------------------------------

    private static ScorerDef Scorer(string name, params MetricDef[] metrics) => new(name, (_, _, _) => Task.FromResult(new Score("C")), metrics);

    private static IReadOnlyList<IReadOnlyDictionary<string, SampleScore>> Epochs(string scorer, params (object Id, ScoreValue Value)[] scores) =>
        scores.Select(s => (IReadOnlyDictionary<string, SampleScore>)new Dictionary<string, SampleScore> { [scorer] = new(new Score(s.Value), s.Id) }).ToList();

    [Fact]
    public void unreduced_metrics_get_their_own_view_over_every_epoch()
    {
        var scorer = Scorer("grade", Metrics.Accuracy(), Metrics.Frequency());
        var scores = Epochs("grade", (1, "C"), (1, "I"), (2, "C"), (2, "C"));

        var results = EvalResultsBuilder.BuildScores([scorer], ["grade"], scores, null, null);

        Assert.Equal(2, results.Count);
        Assert.Equal("mean", results[0].Reducer);
        Assert.Equal(["accuracy"], results[0].Metrics.Keys);
        Assert.Equal(0.75, results[0].Metrics["accuracy"].Value);
        Assert.Equal(2, results[0].ScoredSamples);
        Assert.Null(results[1].Reducer);
        Assert.Equal(["C", "I"], results[1].Metrics.Keys);
        Assert.Equal(0.75, results[1].Metrics["C"].Value);
        Assert.Equal(0.25, results[1].Metrics["I"].Value);
        Assert.Equal(4, results[1].ScoredSamples);
    }

    [Fact]
    public void unreduced_only_metrics_skip_the_reduced_view_and_reduced_views_keep_their_names()
    {
        var scores = Epochs("grade", (1, "C"), (1, "I"));

        var onlyUnreduced = EvalResultsBuilder.BuildScores([Scorer("grade", Metrics.Frequency())], ["grade"], scores, [Reducers.Mode()], null);
        Assert.Single(onlyUnreduced);
        Assert.Null(onlyUnreduced[0].Reducer);
        Assert.Equal(2, onlyUnreduced[0].ScoredSamples);

        var onlyReduced = EvalResultsBuilder.BuildScores([Scorer("grade", Metrics.Accuracy())], ["grade"], scores, null, null);
        Assert.Single(onlyReduced);
        Assert.Null(onlyReduced[0].Reducer);

        var mixed = EvalResultsBuilder.BuildScores([Scorer("grade", Metrics.Accuracy(), Metrics.Frequency())], ["grade"], scores, [Reducers.Mean(), Reducers.Max()], null);
        Assert.Equal(["mean", "max", null], mixed.Select(r => r.Reducer));
        Assert.Equal(1.0, mixed[1].Metrics["accuracy"].Value);

        var disabled = EvalResultsBuilder.BuildScores([Scorer("grade", Metrics.Accuracy(), Metrics.Frequency())], ["grade"], scores, [], null);
        Assert.Single(disabled);
        Assert.Equal(["accuracy", "C", "I"], disabled[0].Metrics.Keys);
    }

    [Fact]
    public void disabled_reduction_rejects_explicitly_reduced_metrics_over_repeated_samples()
    {
        var reduced = Metrics.Accuracy() with { Scores = MetricScores.Reduced };

        var ex = Assert.Throws<InvalidOperationException>(() => EvalResultsBuilder.BuildScores([Scorer("grade", reduced)], ["grade"], Epochs("grade", (1, "C"), (1, "I")), [], null));
        Assert.Contains("epoch reduction is disabled", ex.Message);
        Assert.Single(EvalResultsBuilder.BuildScores([Scorer("grade", reduced)], ["grade"], Epochs("grade", (1, "C"), (2, "I")), [], null));
    }

    // ---- constants ----------------------------------------------------------------------------------

    [Fact]
    public void score_reason_and_unchanged_constants_match_python()
    {
        Assert.Equal("UNCHANGED", ScoreConstants.Unchanged);
        Assert.Equal(["invalid_response_format", "refusal", "no_response", "grader_failed", "scoring_failed"], ScoreReason.All);
        Assert.Equal("scoring_failed", ScoreReason.ScoringFailed);
    }

    [Theory]
    [InlineData(1.0, "1.0")]
    [InlineData(2.5, "2.5")]
    [InlineData(0.1, "0.1")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1.5e-7, "1.5e-07")]
    [InlineData(123456789012345678.0, "1.2345678901234568e+17")]
    [InlineData(-0.0001, "-0.0001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(1000.0, "1000.0")]
    [InlineData(3.14, "3.14")]
    [InlineData(double.NaN, "nan")]
    [InlineData(double.PositiveInfinity, "inf")]
    public void python_float_repr_matches(double value, string expected)
    {
        Assert.Equal(expected, PythonText.FloatRepr(value));
    }
}

namespace InspectAzureAI.Eval.Scorers;

/// <summary>Port of <c>scorer/_metrics/categorical.py</c>.</summary>
public static partial class Metrics
{
    /// <summary>
    /// Port of <c>frequency(categories, normalize)</c>: the proportion (or count when <paramref name="normalize"/> is
    /// false) of each distinct scalar score value, keyed by its text. Declared <paramref name="categories"/> come first
    /// (reported as 0 when unobserved), then any other observed value in first-seen order. Declared with
    /// <see cref="MetricScores.Unreduced"/>, so every epoch counts as an observation. List and dictionary
    /// values throw: declare per-key metrics for those instead.
    /// </summary>
    public static MetricDef Frequency(IEnumerable<string>? categories = null, bool normalize = true)
    {
        var declared = categories?.ToList();
        return new MetricDef("frequency", scores =>
        {
            ArgumentNullException.ThrowIfNull(scores);
            var counts = new OrderedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var declaredCategory in declared ?? [])
            {
                counts.TryAdd(declaredCategory, 0);
            }

            var total = 0;
            foreach (var sampleScore in scores.Where(s => !s.Score.IsUnscored))
            {
                var value = sampleScore.Score.Value;
                if (value is ScoreValue.Dict or ScoreValue.List)
                {
                    var kind = value is ScoreValue.Dict ? "dict" : "list";
                    throw new ArgumentException(
                        $"frequency() received {kind}-valued scores. For dict-valued scorers, declare per-key metrics instead, "
                        + "e.g. @scorer(metrics={\"*\": [frequency()]}).",
                        nameof(scores));
                }

                var key = value.Text;
                counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
                total++;
            }

            var denominator = normalize && total > 0 ? (double)total : 1.0;
            var result = new OrderedDictionary<string, ScoreValue?>(StringComparer.Ordinal);
            foreach (var (key, count) in counts)
            {
                result[key] = count / denominator;
            }

            return new ScoreValue.Dict(result);
        })
        {
            Scores = MetricScores.Unreduced,
            Options = new Dictionary<string, object?>(StringComparer.Ordinal) { ["categories"] = declared, ["normalize"] = normalize },
        };
    }

    /// <summary>Port of <c>categorical(categories)</c>: the default metrics of a categorical scorer, <c>[frequency(categories)]</c>.</summary>
    public static IReadOnlyList<MetricDef> Categorical(IEnumerable<string>? categories = null) => [Frequency(categories)];
}

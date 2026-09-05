using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Scorers;

/// <summary>Port of <c>scorer/_metrics/krippendorff.py</c>.</summary>
public static partial class Metrics
{
    /// <summary>
    /// Port of <c>krippendorff_alpha(level, to_float)</c>: Krippendorff's α of inter-rater agreement over samples whose
    /// score value is a list of per-judge ratings (produce those with <see cref="Scorers.MultiScorer(IReadOnlyList{ScorerDef}, ScoreReducer)"/> and the
    /// <see cref="Reducers.Collect"/> reducer). <paramref name="level"/> is <c>nominal</c> (any difference is a full
    /// disagreement), <c>ordinal</c> (cumulative-midpoint encoding of ranked categories) or <c>interval</c> (squared
    /// numeric difference). Non-numeric ratings need <paramref name="toFloat"/> for the ordered levels and throw
    /// otherwise. Samples with a non-list value or fewer than two ratings are skipped with a warning; NaN when no
    /// usable sample remains; 1 when every rating is identical.
    /// </summary>
    public static MetricDef KrippendorffAlpha(string level = "nominal", Func<ScoreValue, double>? toFloat = null)
    {
        if (level is not ("nominal" or "ordinal" or "interval"))
        {
            throw new ArgumentException($"krippendorff_alpha: unsupported level '{level}'; expected 'nominal', 'ordinal', or 'interval'", nameof(level));
        }

        return new("krippendorff_alpha", scores =>
        {
            ArgumentNullException.ThrowIfNull(scores);
            var units = ExtractUnits(scores, level, toFloat);
            if (units.Count == 0)
            {
                return ScoreValue.NaN;
            }

            MaybeWarnNominalOnNumeric(level, units);
            if (level == "nominal")
            {
                return Alpha(units.Select(u => u.Select(PythonText.ScalarKey.Of).ToList()).ToList(), NominalDisagreement);
            }

            var numeric = units.Select(u => u.Select(v => ((ScoreValue.Num)v!).Value).ToList()).ToList();
            var prepared = level == "ordinal" ? OrdinalScores(numeric) : numeric;
            return Alpha(prepared, SquaredDisagreement);
        });
    }

    /// <summary>Port of <c>_extract_units</c>: per-sample rating lists, coerced to numbers for the ordered levels.</summary>
    private static List<List<ScoreValue?>> ExtractUnits(IReadOnlyList<SampleScore> scores, string level, Func<ScoreValue, double>? toFloat)
    {
        var units = new List<List<ScoreValue?>>();
        var nonSequence = 0;
        var tooFew = 0;
        foreach (var sampleScore in scores)
        {
            if (sampleScore.Score.Value is not ScoreValue.List list)
            {
                nonSequence++;
                continue;
            }

            if (list.Items.Count < 2)
            {
                tooFew++;
                continue;
            }

            units.Add(level == "nominal"
                ? list.Items.Select(v => (ScoreValue?)v).ToList()
                : list.Items.Select(v => (ScoreValue?)new ScoreValue.Num(CoerceNumeric(v, toFloat, level))).ToList());
        }

        if (nonSequence > 0)
        {
            ProviderLogger.Warning($"krippendorff_alpha: skipped {nonSequence} sample(s) with a non-sequence Score.value (expected a list/tuple of per-judge ratings).");
        }

        if (tooFew > 0)
        {
            ProviderLogger.Warning($"krippendorff_alpha: skipped {tooFew} sample(s) with fewer than 2 ratings.");
        }

        return units;
    }

    private static double CoerceNumeric(ScoreValue value, Func<ScoreValue, double>? toFloat, string level)
    {
        if (ValueToFloat.TryNumber(value, out var number))
        {
            return number;
        }

        if (toFloat is not null)
        {
            return toFloat(value);
        }

        throw new ArgumentException(
            $"krippendorff_alpha(level='{level}'): non-numeric rating {ValueToFloat.Describe(value)} requires a `to_float` mapping to establish ordering. "
            + "Pre-encode your categories or pass `to_float=value_to_float()` for CORRECT/INCORRECT string ratings.",
            nameof(value));
    }

    /// <summary>Port of <c>_nominal_disagreement</c>: <c>n² − Σ_c n_c²</c> over unordered categories.</summary>
    private static double NominalDisagreement(List<PythonText.ScalarKey> ratings)
    {
        var n = ratings.Count;
        var counts = new Dictionary<PythonText.ScalarKey, int>();
        foreach (var rating in ratings)
        {
            counts[rating] = counts.TryGetValue(rating, out var count) ? count + 1 : 1;
        }

        return (double)n * n - counts.Values.Sum(c => (double)c * c);
    }

    /// <summary>Port of <c>_squared_disagreement</c>: <c>Σ_{i≠j} (a − b)² = 2n · Σ (x − x̄)²</c>.</summary>
    private static double SquaredDisagreement(List<double> ratings)
    {
        var n = ratings.Count;
        var mean = ratings.Sum() / n;
        return 2 * n * ratings.Sum(x => (x - mean) * (x - mean));
    }

    /// <summary>Port of <c>_ordinal_scores</c>: cumulative-midpoint re-encoding over the global marginal counts.</summary>
    private static List<List<double>> OrdinalScores(List<List<double>> units)
    {
        var counts = new SortedDictionary<double, int>();
        foreach (var rating in units.SelectMany(u => u))
        {
            counts[rating] = counts.TryGetValue(rating, out var count) ? count + 1 : 1;
        }

        var midpoint = new Dictionary<double, double>();
        var cumulative = 0.0;
        foreach (var (rating, count) in counts)
        {
            midpoint[rating] = cumulative + count / 2.0;
            cumulative += count;
        }

        return units.Select(u => u.Select(v => midpoint[v]).ToList()).ToList();
    }

    /// <summary>Port of <c>_alpha</c>: <c>1 − D_o / D_e</c>; NaN with fewer than two ratings, 1 when <c>D_e</c> is 0.</summary>
    private static double Alpha<T>(List<List<T>> units, Func<List<T>, double> disagreement)
    {
        var flat = units.SelectMany(u => u).ToList();
        var n = flat.Count;
        if (n < 2)
        {
            return double.NaN;
        }

        var observed = units.Sum(u => disagreement(u) / (u.Count - 1)) / n;
        var expected = disagreement(flat) / ((double)n * (n - 1));
        return expected == 0 ? 1.0 : 1.0 - observed / expected;
    }

    /// <summary>Port of <c>_maybe_warn_nominal_on_numeric</c>: nominal over more than two distinct numeric (non-bool) values.</summary>
    private static void MaybeWarnNominalOnNumeric(string level, List<List<ScoreValue?>> units)
    {
        if (level != "nominal")
        {
            return;
        }

        var distinct = units.SelectMany(u => u).Select(PythonText.ScalarKey.Of).Distinct().ToList();
        if (distinct.Count <= 2)
        {
            return;
        }

        if (units.SelectMany(u => u).All(v => v is ScoreValue.Num))
        {
            ProviderLogger.Warning(
                $"krippendorff_alpha: nominal level applied to numeric data with {distinct.Count} distinct values; "
                + "consider level='ordinal' (for ranked categories) or level='interval' (for true numeric distances).");
        }
    }
}

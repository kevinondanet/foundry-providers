using System.Runtime.CompilerServices;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Scorers;

/// <summary>
/// Port of <c>scorer/_reducer/reducer.py</c>: the epoch score reducers. The shape of the first scored
/// (non-NaN) value decides how a reduction runs — scalars reduce directly, lists index by index and
/// dictionaries key by key — and NaN elements are skipped at every level exactly as Python does.
/// </summary>
public static partial class Reducers
{
    /// <summary>Stand-in for the registry names <c>reducer_log_name</c> reads off a reducer (delegates carry no name).</summary>
    private static readonly ConditionalWeakTable<ScoreReducer, string> Names = new();

    /// <summary>Port of <c>reducer_log_name</c>: "mean", "max", "at_least_2", ...; null for a reducer not built here.</summary>
    public static string? NameOf(ScoreReducer reducer)
    {
        ArgumentNullException.ThrowIfNull(reducer);
        return Names.TryGetValue(reducer, out var name) ? name : null;
    }

    /// <summary>Port of <c>mean_score</c>.</summary>
    public static ScoreReducer Mean(Func<ScoreValue, double>? valueToFloat = null) =>
        Named("mean", Statistic(valueToFloat ?? ValueToFloat.Default, values => values.Sum() / values.Count));

    /// <summary>Port of <c>median_score</c> (<c>statistics.median</c>: the two middle values are averaged).</summary>
    public static ScoreReducer Median(Func<ScoreValue, double>? valueToFloat = null) =>
        Named("median", Statistic(valueToFloat ?? ValueToFloat.Default, values =>
        {
            var sorted = values.Order().ToList();
            var mid = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
        }));

    /// <summary>Port of <c>mode_score</c>: the most common value, earliest seen on a tie (Python <c>Counter.most_common</c>).</summary>
    public static ScoreReducer Mode() => Named("mode", Counting(counts => counts.MaxBy(e => e.Count)?.Value));

    /// <summary>Port of <c>max_score</c>: the value with the highest <c>valueToFloat</c> (the first on a tie).</summary>
    public static ScoreReducer Max(Func<ScoreValue, double>? valueToFloat = null)
    {
        var toFloat = valueToFloat ?? ValueToFloat.Default;
        return Named("max", scores =>
        {
            ArgumentNullException.ThrowIfNull(scores);
            var representative = FirstScored(scores);
            if (representative is null)
            {
                return NanScore(scores);
            }

            return representative.Value switch
            {
                ScoreValue.Dict => ReduceDict(scores, values => MaxValue(values, toFloat)),
                ScoreValue.List => ReduceList(scores, values => MaxValue(values, toFloat)),
                _ => ReducedScore(scores.Where(s => !s.Value.IsNaN).MaxBy(s => toFloat(s.Value))!.Value, scores),
            };
        });
    }

    /// <summary>Port of <c>at_least(k, value)</c>: 1 when at least <paramref name="k"/> scores are ≥ <paramref name="value"/>, else 0.</summary>
    public static ScoreReducer AtLeast(int k, double value = 1.0, Func<ScoreValue, double>? valueToFloat = null)
    {
        var toFloat = valueToFloat ?? ValueToFloat.Default;
        return Named($"at_least_{k}", Counting(counts =>
        {
            var count = counts.Where(e => Convert(toFloat, e.Value) >= value).Sum(e => e.Count);
            return count >= k ? 1 : 0;
        }));
    }

    /// <summary>Port of <c>pass_at(k, value)</c>: the unbiased pass@k estimator of Chen et al. 2021; NaN when fewer than k scored epochs remain.</summary>
    public static ScoreReducer PassAt(int k, double value = 1.0, Func<ScoreValue, double>? valueToFloat = null) =>
        Named($"pass_at_{k}", Statistic(valueToFloat ?? ValueToFloat.Default, values =>
        {
            var total = values.Count;
            var correct = values.Count(v => v >= value);
            if (total < k)
            {
                return double.NaN;
            }

            if (total - correct < k)
            {
                return 1.0;
            }

            var product = 1.0;
            for (var i = total - correct + 1; i <= total; i++)
            {
                product *= 1.0 - (double)k / i;
            }

            return 1.0 - product;
        }));

    private static ScoreReducer Named(string name, ScoreReducer reducer)
    {
        Names.AddOrUpdate(reducer, name);
        return reducer;
    }

    /// <summary>One counted scalar of a reduction (Python <c>Counter</c> entry): the first value seen for the key and how often it occurred.</summary>
    internal sealed record CountEntry(ScoreValue? Value, int Count);

    private static ScoreReducer Statistic(Func<ScoreValue, double> toFloat, Func<IReadOnlyList<double>, double> statistic) =>
        scores =>
        {
            ArgumentNullException.ThrowIfNull(scores);
            var representative = FirstScored(scores);
            if (representative is null)
            {
                return NanScore(scores);
            }

            return representative.Value switch
            {
                ScoreValue.Dict => ReduceDict(scores, values => ApplyStatistic(values, toFloat, statistic)),
                ScoreValue.List => ReduceList(scores, values => ApplyStatistic(values, toFloat, statistic)),
                _ => ReduceScalars(scores, values => ApplyStatistic(values, toFloat, statistic)),
            };
        };

    private static ScoreReducer Counting(Func<IReadOnlyList<CountEntry>, ScoreValue?> counterFn) =>
        scores =>
        {
            ArgumentNullException.ThrowIfNull(scores);
            var representative = FirstScored(scores);
            if (representative is null)
            {
                return NanScore(scores);
            }

            return representative.Value switch
            {
                ScoreValue.Dict => ReduceDict(scores, values => ApplyCounter(values, counterFn)),
                ScoreValue.List => ReduceList(scores, values => ApplyCounter(values, counterFn)),
                _ => ReduceScalars(scores, values => ApplyCounter(values, counterFn)),
            };
        };

    private static ScoreValue? ApplyStatistic(IReadOnlyList<ScoreValue?> values, Func<ScoreValue, double> toFloat, Func<IReadOnlyList<double>, double> statistic)
    {
        var floats = values.Select(v => Convert(toFloat, v)).Where(f => !double.IsNaN(f)).ToList();
        return floats.Count == 0 ? ScoreValue.NaN : statistic(floats);
    }

    /// <summary>Port of <c>_count_*</c>: tallies the reducible scalars (Python <c>Counter</c>) and hands the tally to the reducer's counter function.</summary>
    private static ScoreValue? ApplyCounter(IReadOnlyList<ScoreValue?> values, Func<IReadOnlyList<CountEntry>, ScoreValue?> counterFn)
    {
        var entries = new List<CountEntry>();
        var index = new Dictionary<ScalarKey, int>();
        foreach (var value in values.Where(IsReducible))
        {
            if (value is ScoreValue.List or ScoreValue.Dict)
            {
                throw new ArgumentException($"Cannot reduce a {value.GetType().Name} score value as a scalar.", nameof(values));
            }

            var key = ScalarKey.Of(value);
            if (index.TryGetValue(key, out var at))
            {
                entries[at] = entries[at] with { Count = entries[at].Count + 1 };
            }
            else
            {
                index[key] = entries.Count;
                entries.Add(new CountEntry(value, 1));
            }
        }

        return entries.Count == 0 ? ScoreValue.NaN : counterFn(entries);
    }

    private static ScoreValue? MaxValue(IReadOnlyList<ScoreValue?> values, Func<ScoreValue, double> toFloat)
    {
        var reducible = values.Where(IsReducible).ToList();
        return reducible.Count == 0 ? ScoreValue.NaN : reducible.MaxBy(v => Convert(toFloat, v));
    }

    private static Score ReduceScalars(IReadOnlyList<Score> scores, Func<IReadOnlyList<ScoreValue?>, ScoreValue?> reduce)
    {
        var result = reduce(scores.Select(s => (ScoreValue?)s.Value).ToList()) ?? ScoreValue.NaN;
        return result.IsNaN ? NanScore(scores) : ReducedScore(result, scores);
    }

    private static Score ReduceDict(IReadOnlyList<Score> scores, Func<IReadOnlyList<ScoreValue?>, ScoreValue?> reduce)
    {
        var dicts = PartitionDicts(scores);
        if (dicts.Count == 0)
        {
            return NanScore(scores);
        }

        var result = new OrderedDictionary<string, ScoreValue?>(StringComparer.Ordinal);
        foreach (var key in dicts[0].Items.Keys)
        {
            result[key] = reduce(dicts.Select(d => d.Items[key]).ToList());
        }

        return ReducedScore(new ScoreValue.Dict(result), scores);
    }

    private static Score ReduceList(IReadOnlyList<Score> scores, Func<IReadOnlyList<ScoreValue?>, ScoreValue?> reduce)
    {
        var lists = PartitionLists(scores);
        if (lists.Count == 0)
        {
            return NanScore(scores);
        }

        var result = new List<ScoreValue>();
        for (var i = 0; i < lists[0].Items.Count; i++)
        {
            result.Add(reduce(lists.Select(l => (ScoreValue?)l.Items[i]).ToList()) ?? ScoreValue.NaN);
        }

        return ReducedScore(new ScoreValue.List(result), scores);
    }

    /// <summary>Port of <c>_partition_dict_scores</c>: dictionary-shaped scores only, rejecting other shapes and mismatched keys.</summary>
    private static List<ScoreValue.Dict> PartitionDicts(IReadOnlyList<Score> scores)
    {
        var result = new List<ScoreValue.Dict>();
        foreach (var score in scores)
        {
            switch (score.Value)
            {
                case ScoreValue.Dict dict:
                    result.Add(dict);
                    break;
                case { IsNaN: true }:
                    break;
                default:
                    throw new ArgumentException("Attempting to reduce a dictionary score for a non-dictionary value", nameof(scores));
            }
        }

        if (result.Count > 0)
        {
            var keys = result[0].Items.Keys.ToHashSet(StringComparer.Ordinal);
            foreach (var dict in result.Skip(1))
            {
                if (!keys.SetEquals(dict.Items.Keys))
                {
                    throw new ArgumentException(
                        "Cannot reduce dictionary scores with mismatched keys: "
                        + $"[{string.Join(", ", keys.Order(StringComparer.Ordinal))}] vs [{string.Join(", ", dict.Items.Keys.Order(StringComparer.Ordinal))}]. "
                        + "Every epoch must score the same keys; return a NaN score to mark an individual epoch as unscored.",
                        nameof(scores));
                }
            }
        }

        return result;
    }

    /// <summary>Port of <c>_partition_list_scores</c>: list-shaped scores only, rejecting other shapes and mismatched lengths.</summary>
    private static List<ScoreValue.List> PartitionLists(IReadOnlyList<Score> scores)
    {
        var result = new List<ScoreValue.List>();
        foreach (var score in scores)
        {
            switch (score.Value)
            {
                case ScoreValue.List list:
                    result.Add(list);
                    break;
                case { IsNaN: true }:
                    break;
                default:
                    throw new ArgumentException("Attempting to reduce a list score for a non-list value", nameof(scores));
            }
        }

        if (result.Count > 0)
        {
            var length = result[0].Items.Count;
            foreach (var list in result.Skip(1))
            {
                if (list.Items.Count != length)
                {
                    throw new ArgumentException(
                        $"Cannot reduce list scores with mismatched lengths: {length} vs {list.Items.Count}. "
                        + "Every epoch must produce the same number of values; return a NaN score to mark an individual epoch as unscored.",
                        nameof(scores));
                }
            }
        }

        return result;
    }

    /// <summary>Port of <c>_reduced_score</c>: answer, explanation and reason survive only when identical across the epochs; metadata is the first score's.</summary>
    private static Score ReducedScore(ScoreValue value, IReadOnlyList<Score> scores) => new(value)
    {
        Answer = AllEqual(scores, s => s.Answer) ? scores[0].Answer : null,
        Explanation = AllEqual(scores, s => s.Explanation) ? scores[0].Explanation : null,
        Reason = AllEqual(scores, s => s.Reason) ? scores[0].Reason : null,
        Metadata = scores[0].Metadata,
    };

    /// <summary>Port of <c>_nan_score</c>.</summary>
    private static Score NanScore(IReadOnlyList<Score> scores) =>
        scores.Count == 0 ? new Score(ScoreValue.NaN) : ReducedScore(ScoreValue.NaN, scores);

    private static bool AllEqual(IReadOnlyList<Score> scores, Func<Score, string?> select) =>
        scores.Select(select).Distinct(StringComparer.Ordinal).Count() == 1;

    private static Score? FirstScored(IReadOnlyList<Score> scores) => scores.FirstOrDefault(s => !s.Value.IsNaN);

    /// <summary>Port of <c>_is_reducible</c>: anything but a NaN number (a null dictionary entry counts as a value, as Python's <c>None</c> does).</summary>
    private static bool IsReducible(ScoreValue? value) => value is not { IsNaN: true };

    /// <summary>Applies <c>value_to_float</c>; a null dictionary entry is Python's <c>None</c>, which the converter cannot map.</summary>
    private static double Convert(Func<ScoreValue, double> toFloat, ScoreValue? value)
    {
        if (value is not null)
        {
            return toFloat(value);
        }

        ProviderLogger.Warning("Unable to convert value to float: None");
        return 0.0;
    }

    /// <summary>Python hash/equality of a counted scalar: numbers and bools share a key (<c>1 == True == 1.0</c>), strings are ordinal.</summary>
    private readonly record struct ScalarKey(bool IsNumber, double Number, string? Text)
    {
        public static ScalarKey Of(ScoreValue? value) =>
            value is not null && ValueToFloat.TryNumber(value, out var number)
                ? new ScalarKey(true, number, null)
                : new ScalarKey(false, 0, value is ScoreValue.Str s ? s.Value : null);
    }
}

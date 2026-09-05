using System.Globalization;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Scorers;

namespace InspectAzureAI.Eval.Runner;

/// <summary>
/// Port of <c>_eval/task/results.py</c> (<c>eval_results</c>, <c>reduce_scores</c>, <c>scorer_for_metrics</c>,
/// <c>metrics_unique_key</c>) and <c>scorer/_scorer.py</c> <c>unique_scorer_name</c>: epoch scores are reduced
/// per sample (default <c>mean</c>), then each scorer's metrics run over the reduced, scored (non-NaN) samples.
/// </summary>
internal static class EvalResultsBuilder
{
    /// <summary>Port of <c>unique_scorer_name</c>: a scorer name already in use gets a numeric suffix (<c>match</c>, <c>match1</c>, ...).</summary>
    public static IReadOnlyList<string> UniqueScorerNames(IReadOnlyList<ScorerDef> scorers)
    {
        var names = new List<string>(scorers.Count);
        foreach (var scorer in scorers)
        {
            var name = scorer.Name;
            var count = 1;
            while (names.Contains(name, StringComparer.Ordinal))
            {
                name = $"{scorer.Name}{count}";
                count++;
            }

            names.Add(name);
        }

        return names;
    }

    public static IReadOnlyList<EvalScore> BuildScores(
        IReadOnlyList<ScorerDef> scorers,
        IReadOnlyList<string> scorerNames,
        IReadOnlyList<IReadOnlyDictionary<string, SampleScore>> sampleScores,
        IReadOnlyList<ScoreReducer>? reducers,
        IReadOnlyList<MetricDef>? metricsOverride)
    {
        var result = new List<EvalScore>();
        for (var i = 0; i < scorers.Count; i++)
        {
            var name = scorerNames[i];
            var metrics = metricsOverride ?? scorers[i].Metrics;
            var scores = sampleScores.Where(s => s.ContainsKey(name)).Select(s => s[name]).ToList();

            // Python: no reducers → an unnamed mean view; an explicit empty list disables reduction entirely
            if (reducers is { Count: 0 })
            {
                result.Add(ScoreForMetrics(name, scores, metrics, null));
                continue;
            }

            var views = reducers is null
                ? [(Reducers.Mean(), (string?)null)]
                : reducers.Select(reducer => (reducer, Reducers.NameOf(reducer))).ToList();
            foreach (var (reducer, reducerName) in views)
            {
                result.Add(ScoreForMetrics(name, ReduceScores(scores, reducer), metrics, reducerName));
            }
        }

        return result;
    }

    /// <summary>Port of <c>reduce_scores</c>: groups by sample id (in first-seen order) and reduces each group's epochs to one score.</summary>
    internal static List<SampleScore> ReduceScores(IReadOnlyList<SampleScore> scores, ScoreReducer reducer)
    {
        var groups = new OrderedDictionary<string, List<SampleScore>>(StringComparer.Ordinal);
        foreach (var score in scores)
        {
            if (score.SampleId is null)
            {
                continue;
            }

            var key = Eval.SampleIdKey(score.SampleId);
            if (!groups.TryGetValue(key, out var group))
            {
                group = [];
                groups[key] = group;
            }

            group.Add(score);
        }

        return groups.Values
            .Select(group => new SampleScore(reducer(group.Select(s => s.Score).ToList()), group[0].SampleId, group[0].SampleMetadata))
            .ToList();
    }

    /// <summary>Port of <c>scorer_for_metrics</c>: metrics over the scored samples; a dict-valued metric expands per key, a list per index.</summary>
    private static EvalScore ScoreForMetrics(string scorerName, IReadOnlyList<SampleScore> scores, IReadOnlyList<MetricDef> metrics, string? reducerName)
    {
        var scored = scores.Where(s => !s.Score.IsUnscored).ToList();
        var results = new OrderedDictionary<string, EvalMetric>(StringComparer.Ordinal);
        foreach (var metric in metrics)
        {
            var key = UniqueMetricKey(metric.Name, results.Keys);
            var value = scored.Count > 0 ? metric.Compute(scored) : ScoreValue.NaN;
            switch (value)
            {
                case ScoreValue.Dict dict:
                    foreach (var (entryKey, entryValue) in dict.Items)
                    {
                        if (entryValue is not null)
                        {
                            results[UniqueMetricKey(entryKey, results.Keys)] = new EvalMetric(entryKey, MetricValue(entryValue));
                        }
                    }

                    break;
                case ScoreValue.List list:
                    for (var index = 0; index < list.Items.Count; index++)
                    {
                        var count = (index + 1).ToString(CultureInfo.InvariantCulture);
                        results[UniqueMetricKey($"{key}-{count}", results.Keys)] = new EvalMetric(count, MetricValue(list.Items[index]));
                    }

                    break;
                default:
                    results[key] = new EvalMetric(metric.Name, MetricValue(value));
                    break;
            }
        }

        return new EvalScore(scorerName, scorerName)
        {
            Reducer = reducerName,
            ScoredSamples = scored.Count,
            UnscoredSamples = scores.Count - scored.Count,
            Metrics = results,
        };
    }

    /// <summary>Port of <c>metrics_unique_key</c>: a duplicate key gets the next free numeric suffix starting at 2.</summary>
    internal static string UniqueMetricKey(string key, IEnumerable<string> existing)
    {
        var keys = existing.ToList();
        if (!keys.Contains(key, StringComparer.Ordinal))
        {
            return key;
        }

        var keyIndex = 2;
        var pattern = new Regex($"^{Regex.Escape(key)}(\\d+)");
        foreach (var existingKey in keys)
        {
            var match = pattern.Match(existingKey);
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= keyIndex)
            {
                keyIndex = index + 1;
            }
        }

        return $"{key}{keyIndex}";
    }

    /// <summary>Python <c>float(value)</c> for a metric result; a value that is not numeric is logged as NaN (written as JSON null).</summary>
    private static double MetricValue(ScoreValue? value) => value switch
    {
        ScoreValue.Num number => number.Value,
        ScoreValue.Bool flag => flag.Value ? 1.0 : 0.0,
        ScoreValue.Str text when double.TryParse(text.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => double.NaN,
    };
}

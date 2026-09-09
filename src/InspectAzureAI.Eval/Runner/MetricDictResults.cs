using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Scorers;

namespace InspectAzureAI.Eval.Runner;

/// <summary>
/// Port of the dictionary-metrics half of <c>_eval/task/results.py</c>: <c>scorers_from_metric_dict</c>,
/// <c>resolve_glob_metric_keys</c>, the dictionary branch of <c>_filter_metrics_by_scores</c>, <c>_flatten_metrics</c> and
/// <c>fnmatch.translate</c>, plus <c>resolve_scorer_metrics</c> (<c>_eval/task/log.py</c>) for the log header. A
/// dictionary-valued score is split per key: each key's entries become a sample-score list of their own, the key's
/// metrics run over it, and the result is one <see cref="EvalScore"/> named after the key with the scorer recorded in
/// <see cref="EvalScore.Scorer"/>. <see cref="EvalResultsBuilder"/> calls into this for every scorer that has a
/// <see cref="ScorerDef.MetricsByKey"/>.
/// </summary>
internal static class MetricDictResults
{
    /// <summary>Port of <c>_flatten_metrics</c>: the plain metrics followed by every key's metrics.</summary>
    public static IEnumerable<MetricDef> Flatten(IReadOnlyList<MetricDef> metrics, MetricDict? byKey) =>
        byKey is null ? metrics : metrics.Concat(byKey.Values.SelectMany(keyMetrics => keyMetrics));

    /// <summary>
    /// Port of the dictionary branch of <c>_filter_metrics_by_scores</c>: the keys whose metrics pass <paramref name="keep"/>,
    /// each with only those metrics; null when no key keeps any (Python's <c>dict_result or None</c>).
    /// </summary>
    public static MetricDict? Filter(MetricDict metrics, Func<MetricDef, bool> keep)
    {
        var result = new MetricDict();
        foreach (var (key, keyMetrics) in metrics)
        {
            var kept = keyMetrics.Where(keep).ToList();
            if (kept.Count > 0)
            {
                result[key] = kept;
            }
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Port of <c>scorers_from_metric_dict</c>: one <see cref="EvalScore"/> per (glob-resolved) key. A NaN-at-root score is the
    /// unscored sentinel and counts as unscored for every key; a dictionary score contributes its entry for the key (a NaN
    /// or null entry is unscored); a key missing from a dictionary score, or a non-dictionary score, throws with Python's
    /// message. A dictionary-valued metric result expands to <c>&lt;metric&gt;_&lt;key&gt;</c> entries and a list to
    /// <c>&lt;metric&gt;_&lt;index&gt;</c> (zero-based), each grouped under the metric's name — the naming of Python's dict form,
    /// which differs from <c>scorer_for_metrics</c>. Python also records the metric's registry params on every entry;
    /// <see cref="MetricDef"/> has none, so <c>params</c> stays empty.
    /// </summary>
    public static List<EvalScore> ScorersFromMetricDict(string scorerName, IReadOnlyList<SampleScore> sampleScores, MetricDict metrics, string? reducerName)
    {
        ArgumentNullException.ThrowIfNull(sampleScores);
        ArgumentNullException.ThrowIfNull(metrics);

        // Use the first sample with a dict-valued score as the base for key globbing; NaN-at-root samples are skipped here
        var baseScore = sampleScores.Select(sample => sample.Score).FirstOrDefault(score => score.Value is ScoreValue.Dict);
        var resolved = baseScore is null ? metrics : ResolveGlobMetricKeys(metrics, baseScore);

        var results = new List<EvalScore>();
        foreach (var (metricKey, metricList) in resolved)
        {
            var keyScores = new List<SampleScore>();
            var unscoredSamples = 0;
            var scoredSamples = 0;
            foreach (var sampleScore in sampleScores)
            {
                var value = sampleScore.Score.Value;
                if (value.IsNaN)
                {
                    unscoredSamples++;
                    continue;
                }

                if (value is not ScoreValue.Dict dict)
                {
                    throw new InvalidOperationException("A dictionary of metrics specified for a non-dictionary score");
                }

                if (!dict.Items.TryGetValue(metricKey, out var entry))
                {
                    throw new InvalidOperationException($"key '{metricKey}' isn't present in the score value dictionary");
                }

                var keyValue = entry ?? ScoreValue.NaN;
                if (keyValue.IsNaN)
                {
                    unscoredSamples++;
                }
                else
                {
                    scoredSamples++;
                    keyScores.Add(sampleScore with { Score = sampleScore.Score with { Value = keyValue } });
                }
            }

            var resultMetrics = new OrderedDictionary<string, EvalMetric>(StringComparer.Ordinal);
            foreach (var metric in metricList)
            {
                var metricName = metric.Name;
                var value = keyScores.Count > 0 ? metric.Compute(keyScores) : ScoreValue.NaN;
                switch (value)
                {
                    case ScoreValue.Dict valueDict:
                        foreach (var (key, item) in valueDict.Items)
                        {
                            resultMetrics[$"{metricName}_{key}"] = new EvalMetric(key, EvalResultsBuilder.MetricValue(item)) { Group = metricName };
                        }

                        break;
                    case ScoreValue.List valueList:
                        for (var index = 0; index < valueList.Items.Count; index++)
                        {
                            var name = index.ToString(CultureInfo.InvariantCulture);
                            resultMetrics[$"{metricName}_{name}"] = new EvalMetric(name, EvalResultsBuilder.MetricValue(valueList.Items[index])) { Group = metricName };
                        }

                        break;
                    default:
                        resultMetrics[metricName] = new EvalMetric(metricName, EvalResultsBuilder.MetricValue(value));
                        break;
                }
            }

            results.Add(new EvalScore(metricKey, scorerName)
            {
                Reducer = reducerName,
                ScoredSamples = scoredSamples,
                UnscoredSamples = unscoredSamples,
                Metrics = resultMetrics,
            });
        }

        return results;
    }

    /// <summary>
    /// Port of <c>resolve_glob_metric_keys</c>: every metric key is a glob matched against the keys of
    /// <paramref name="baseScore"/>'s dictionary value; each matching score key collects the metrics of every pattern
    /// that matches it (deduplicated by metric name, first pattern wins), in pattern-then-score-key order.
    /// </summary>
    public static MetricDict ResolveGlobMetricKeys(MetricDict metrics, Score baseScore)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(baseScore);
        if (baseScore.Value is not ScoreValue.Dict dict)
        {
            throw new InvalidOperationException(
                "A dictionary of metrics was specified for a non-dictionary score. Dictionaries of metrics are only valid when the score value is a dictionary.");
        }

        var resolved = new OrderedDictionary<string, List<MetricDef>>(StringComparer.Ordinal);
        foreach (var (metricKey, metricList) in metrics)
        {
            var keyGlob = GlobRegex(metricKey);
            foreach (var scoreKey in dict.Items.Keys)
            {
                if (!keyGlob.IsMatch(scoreKey))
                {
                    continue;
                }

                if (!resolved.TryGetValue(scoreKey, out var keyMetrics))
                {
                    keyMetrics = [];
                    resolved[scoreKey] = keyMetrics;
                }

                var existingNames = keyMetrics.Select(metric => metric.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var metric in metricList)
                {
                    if (existingNames.Add(metric.Name))
                    {
                        keyMetrics.Add(metric);
                    }
                }
            }
        }

        return new MetricDict(resolved.Select(pair => new KeyValuePair<string, IReadOnlyList<MetricDef>>(pair.Key, pair.Value)));
    }

    /// <summary>
    /// Port of <c>re.compile(fnmatch.translate(pattern))</c> as used by <c>re.match</c>: <c>*</c> matches any run of characters
    /// (newlines included), <c>?</c> one character, <c>[seq]</c> / <c>[!seq]</c> a character class; every other character is
    /// literal and the whole key must match. Case-sensitive, like <c>fnmatch.translate</c>.
    /// </summary>
    internal static Regex GlobRegex(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var regex = new StringBuilder("^");
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i++];
            switch (c)
            {
                case '*':
                    regex.Append(".*");
                    break;
                case '?':
                    regex.Append('.');
                    break;
                case '[':
                {
                    var j = i;
                    if (j < pattern.Length && pattern[j] == '!')
                    {
                        j++;
                    }

                    if (j < pattern.Length && pattern[j] == ']')
                    {
                        j++;
                    }

                    while (j < pattern.Length && pattern[j] != ']')
                    {
                        j++;
                    }

                    if (j >= pattern.Length)
                    {
                        regex.Append("\\[");
                        break;
                    }

                    var stuff = pattern[i..j].Replace("\\", "\\\\", StringComparison.Ordinal);
                    i = j + 1;
                    if (stuff.Length == 0)
                    {
                        regex.Append("(?!)");
                    }
                    else if (stuff == "!")
                    {
                        regex.Append('.');
                    }
                    else
                    {
                        if (stuff[0] == '!')
                        {
                            stuff = "^" + stuff[1..];
                        }
                        else if (stuff[0] is '^' or '[')
                        {
                            stuff = "\\" + stuff;
                        }

                        regex.Append('[').Append(stuff).Append(']');
                    }

                    break;
                }

                default:
                    regex.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        regex.Append("\\z");
        return new Regex(regex.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Port of <c>resolve_scorer_metrics</c> for <see cref="EvalScorer.Metrics"/>: a list of metric definitions for a plain
    /// scorer, a <c>{key: [definitions]}</c> object for Python's dictionary form, and a list ending with that object for the
    /// list form (<c>[accuracy(), {...}]</c>). <c>options</c> are the metric's <see cref="MetricDef.Options"/> (empty for a
    /// metric without creation parameters).
    /// </summary>
    public static JsonNode HeaderMetrics(ScorerDef scorer)
    {
        ArgumentNullException.ThrowIfNull(scorer);
        var definitions = scorer.Metrics.Select(Definition).ToList();
        if (scorer.MetricsByKey is not { } byKey)
        {
            return new JsonArray([.. definitions]);
        }

        var dictionary = new JsonObject(byKey.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, new JsonArray([.. pair.Value.Select(Definition)]))));
        if (definitions.Count == 0)
        {
            return dictionary;
        }

        definitions.Add(dictionary);
        return new JsonArray([.. definitions]);
    }

    private static JsonNode? Definition(MetricDef metric) => new JsonObject
    {
        ["name"] = metric.Name,
        ["options"] = metric.Options is { } options
            ? new JsonObject(options.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value is null ? null : JsonSerializer.SerializeToNode(pair.Value))))
            : new JsonObject(),
    };
}

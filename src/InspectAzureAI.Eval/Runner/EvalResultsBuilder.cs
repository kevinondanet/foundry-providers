using System.Globalization;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Runner.Scoring;
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
        IReadOnlyList<MetricDef>? metricsOverride,
        MetricDict? metricsByKeyOverride = null) =>
        ComputeViews(scorers, scorerNames, sampleScores, reducers, metricsOverride, metricsByKeyOverride).Scores;

    /// <summary>
    /// Port of <c>eval_results</c>: the <see cref="EvalResults"/> (one <see cref="EvalScore"/> per scorer and reducer view,
    /// the sample counts, the headline resolved against <paramref name="headlineMetric"/>) and the per-view
    /// <see cref="EvalSampleReductions"/> (null when there are no scorers, as in Python). <paramref name="scorerNames"/>
    /// are the unique names the sample scores are keyed by (<see cref="UniqueScorerNames"/>); <paramref name="metricsOverride"/>
    /// (with <paramref name="metricsByKeyOverride"/>, the dictionary part of Python's task-level <c>metrics</c>) replaces
    /// every scorer's metrics; <paramref name="completedSamples"/> is the number of samples that ended without
    /// error, falling back to the number of scored samples when the caller cannot know it. Shared by the runner and
    /// <see cref="ScoreLogs"/> so re-scoring and recomputation agree with the run.
    /// </summary>
    public static ComputedResults ComputeResults(
        int totalSamples,
        IReadOnlyList<IReadOnlyDictionary<string, SampleScore>> sampleScores,
        IReadOnlyList<ScorerDef> scorers,
        IReadOnlyList<string> scorerNames,
        IReadOnlyList<ScoreReducer>? reducers,
        IReadOnlyList<MetricDef>? metricsOverride,
        EarlyStoppingSummary? earlyStopping = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        int? completedSamples = null,
        HeadlineMetric? headlineMetric = null,
        MetricDict? metricsByKeyOverride = null)
    {
        ArgumentNullException.ThrowIfNull(sampleScores);
        ArgumentNullException.ThrowIfNull(scorers);
        ArgumentNullException.ThrowIfNull(scorerNames);
        var views = ComputeViews(scorers, scorerNames, sampleScores, reducers, metricsOverride, metricsByKeyOverride);
        var results = new EvalResults
        {
            TotalSamples = totalSamples,
            CompletedSamples = completedSamples ?? sampleScores.Count,
            EarlyStopping = earlyStopping,
            Scores = views.Scores,
            Metadata = metadata,
        };
        if (HeadlineMetrics.Resolve(results, headlineMetric) is { } headline)
        {
            results = results with { Headline = HeadlineMetrics.Ref(headline) };
        }

        return new ComputedResults(results, scorers.Count > 0 ? views.Reductions : null);
    }

    /// <summary>
    /// Port of <c>reducer_log_names</c> for the run header: the names of the epoch reducers, or null when there are none
    /// or one of them has no registry name (a custom delegate; Python cannot record those either).
    /// </summary>
    public static IReadOnlyList<string>? EpochsReducerNames(IReadOnlyList<ScoreReducer>? reducers)
    {
        if (reducers is not { Count: > 0 })
        {
            return null;
        }

        var names = reducers.Select(Reducers.NameOf).ToList();
        return names.Any(name => name is null) ? null : names.Select(name => name!).ToList();
    }

    /// <summary>The scores and epoch reductions of every scorer's reducer views (<c>compute_eval_scores_for_views</c> over all scorers).</summary>
    private sealed record ScoreViews(IReadOnlyList<EvalScore> Scores, IReadOnlyList<EvalSampleReductions> Reductions);

    private static ScoreViews ComputeViews(
        IReadOnlyList<ScorerDef> scorers,
        IReadOnlyList<string> scorerNames,
        IReadOnlyList<IReadOnlyDictionary<string, SampleScore>> sampleScores,
        IReadOnlyList<ScoreReducer>? reducers,
        IReadOnlyList<MetricDef>? metricsOverride,
        MetricDict? metricsByKeyOverride)
    {
        var result = new List<EvalScore>();
        var reductions = new List<EvalSampleReductions>();
        for (var i = 0; i < scorers.Count; i++)
        {
            var name = scorerNames[i];
            // Python's Metrics union: a task-level override (list part, dictionary part or both) replaces the scorer's metrics, dictionary form included
            var overridden = metricsOverride is not null || metricsByKeyOverride is not null;
            var metrics = overridden ? metricsOverride ?? [] : scorers[i].Metrics;
            var byKey = overridden ? metricsByKeyOverride : scorers[i].MetricsByKey;
            // metrics={...} (the dictionary form) produces only per-key scores; metrics=[..., {...}] the scorer's score as well
            var dictForm = byKey is not null && metrics.Count == 0;
            var scores = sampleScores.Where(s => s.ContainsKey(name)).Select(s => s[name]).ToList();

            // Python: no reducers → an unnamed mean view; an explicit empty list disables reduction entirely
            if (reducers is { Count: 0 })
            {
                if (MetricDictResults.Flatten(metrics, byKey).Any(m => m.Scores == MetricScores.Reduced) && HasRepeatedSampleIds(scores))
                {
                    throw new InvalidOperationException(
                        $"Scorer '{scorers[i].Name}' has metrics with @metric(scores=\"reduced\") but epoch reduction is disabled. "
                        + "Configure an epochs reducer or use scores=\"auto\"/\"unreduced\".");
                }

                result.AddRange(ComputeEvalScores(name, scores, metrics, byKey, dictForm, null));
                continue;
            }

            // Port of compute_eval_scores_for_views: "unreduced" metrics see every epoch in their own view (no reducer)
            var reducedMetrics = metrics.Where(m => m.Scores != MetricScores.Unreduced).ToList();
            var unreducedMetrics = metrics.Where(m => m.Scores == MetricScores.Unreduced).ToList();
            var reducedByKey = byKey is null ? null : MetricDictResults.Filter(byKey, m => m.Scores != MetricScores.Unreduced);
            var unreducedByKey = byKey is null ? null : MetricDictResults.Filter(byKey, m => m.Scores == MetricScores.Unreduced);
            var hasReduced = reducedMetrics.Count > 0 || reducedByKey is not null;
            var hasUnreduced = unreducedMetrics.Count > 0 || unreducedByKey is not null;
            var mixedViews = hasReduced && hasUnreduced;
            if (hasReduced)
            {
                var views = reducers is null
                    ? [(Reducers.Mean(), mixedViews ? "mean" : null)]
                    : reducers.Select(reducer => (reducer, Reducers.NameOf(reducer))).ToList();
                foreach (var (reducer, reducerName) in views)
                {
                    var reduced = ReduceScores(scores, reducer);
                    reductions.Add(new EvalSampleReductions(name, reduced.Select(s => new EvalSampleScore(s.Score) { SampleId = s.SampleId }).ToList()) { Reducer = reducerName });
                    result.AddRange(ComputeEvalScores(name, reduced, reducedMetrics, reducedByKey, dictForm, reducerName));
                }
            }

            if (hasUnreduced)
            {
                result.AddRange(ComputeEvalScores(name, scores, unreducedMetrics, unreducedByKey, dictForm, null));
            }
        }

        return new ScoreViews(result, reductions);
    }

    /// <summary>
    /// Port of <c>compute_eval_scores</c>: the list form yields the scorer's own score over its plain metrics (even when
    /// none of them apply to this view, as Python does) followed by one score per key of its dictionary entry; the
    /// dictionary form (<paramref name="dictForm"/>) yields the per-key scores only.
    /// </summary>
    private static IEnumerable<EvalScore> ComputeEvalScores(string scorerName, IReadOnlyList<SampleScore> scores, IReadOnlyList<MetricDef> metrics, MetricDict? byKey, bool dictForm, string? reducerName)
    {
        if (!dictForm)
        {
            yield return ScoreForMetrics(scorerName, scores, metrics, reducerName);
        }

        if (byKey is not null)
        {
            foreach (var score in MetricDictResults.ScorersFromMetricDict(scorerName, scores, byKey, reducerName))
            {
                yield return score;
            }
        }
    }

    /// <summary>Port of <c>_has_repeated_sample_ids</c>: whether any sample id (ignoring null) occurs more than once.</summary>
    private static bool HasRepeatedSampleIds(IReadOnlyList<SampleScore> scores)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return scores.Any(score => score.SampleId is not null && !seen.Add(Eval.SampleIdKey(score.SampleId)));
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

    /// <summary>
    /// Port of <c>scorer_for_metrics</c>: metrics over the scored samples; a dict-valued metric expands per key, a list
    /// per index, each entry carrying the metric's name as its <c>group</c>. Python also records the metric's registry
    /// params on every entry; <see cref="MetricDef"/> has none, so <c>params</c> stays empty.
    /// </summary>
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
                            results[UniqueMetricKey(entryKey, results.Keys)] = new EvalMetric(entryKey, MetricValue(entryValue)) { Group = metric.Name };
                        }
                    }

                    break;
                case ScoreValue.List list:
                    for (var index = 0; index < list.Items.Count; index++)
                    {
                        var count = (index + 1).ToString(CultureInfo.InvariantCulture);
                        results[UniqueMetricKey($"{key}-{count}", results.Keys)] = new EvalMetric(count, MetricValue(list.Items[index])) { Group = metric.Name };
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
    internal static double MetricValue(ScoreValue? value) => value switch
    {
        ScoreValue.Num number => number.Value,
        ScoreValue.Bool flag => flag.Value ? 1.0 : 0.0,
        ScoreValue.Str text when double.TryParse(text.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => double.NaN,
    };
}

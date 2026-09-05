using InspectAzureAI.Eval.Log;

namespace InspectAzureAI.Eval.Runner.Scoring;

/// <summary>Port of <c>log/_headline.py</c> <c>ResolvedHeadlineMetric</c>: the score and metric a headline declaration resolves to.</summary>
internal sealed record ResolvedHeadlineMetric(EvalScore Score, string Name, EvalMetric Metric);

/// <summary>Port of <c>log/_headline.py</c>: resolving a task's declared headline metric against eval results.</summary>
internal static class HeadlineMetrics
{
    /// <summary>
    /// Port of <c>resolve_headline_metric</c>: each set field of <paramref name="declared"/> narrows the candidate scores in
    /// turn (reducer, scorer, score), then <c>metric</c> picks within them. A declaration matching nothing is abandoned whole
    /// and the convention applies: the first metric of the first score (Python also warns once). Null when there is nothing.
    /// </summary>
    public static ResolvedHeadlineMetric? Resolve(EvalResults? results, HeadlineMetric? declared)
    {
        if (results is null || results.Scores.Count == 0)
        {
            return null;
        }

        if (declared is not null && ResolveDeclared(results.Scores, declared) is { } resolved)
        {
            return resolved;
        }

        return FirstMetric(results.Scores[0]);
    }

    /// <summary>Port of <c>headline_metric(log)</c>: the headline resolved at scoring time when present, else the task's declaration.</summary>
    public static ResolvedHeadlineMetric? ForLog(EvalLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return log.Results is null ? null : Resolve(log.Results, log.Results.Headline ?? log.Eval.HeadlineMetric);
    }

    /// <summary>Port of <c>headline_metric_ref</c>: the fully qualified reference (scorer, score, metric, reducer) of a resolved headline.</summary>
    public static HeadlineMetric Ref(ResolvedHeadlineMetric resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        return new HeadlineMetric
        {
            Scorer = resolved.Score.Scorer,
            Score = resolved.Score.Name,
            Metric = resolved.Name,
            Reducer = resolved.Score.Reducer,
        };
    }

    private static ResolvedHeadlineMetric? ResolveDeclared(IReadOnlyList<EvalScore> scores, HeadlineMetric declared)
    {
        IReadOnlyList<EvalScore> candidates = scores;
        var selectors = new (string? Value, Func<EvalScore, string?> Field)[]
        {
            (declared.Reducer, score => score.Reducer),
            (declared.Scorer, score => score.Scorer),
            (declared.Score, score => score.Name),
        };
        foreach (var (value, field) in selectors)
        {
            if (value is null)
            {
                continue;
            }

            var matched = candidates.Where(score => string.Equals(field(score), value, StringComparison.Ordinal)).ToList();
            if (matched.Count == 0)
            {
                return null;
            }

            candidates = matched;
        }

        if (declared.Metric is { } metricName)
        {
            foreach (var score in candidates)
            {
                if (score.Metrics.TryGetValue(metricName, out var metric))
                {
                    return new ResolvedHeadlineMetric(score, metricName, metric);
                }
            }

            return null;
        }

        return FirstMetric(candidates[0]);
    }

    /// <summary>Python tests for absence, not truthiness: <c>""</c> is a legal metric key.</summary>
    private static ResolvedHeadlineMetric? FirstMetric(EvalScore score)
    {
        foreach (var (name, metric) in score.Metrics)
        {
            return new ResolvedHeadlineMetric(score, name, metric);
        }

        return null;
    }
}

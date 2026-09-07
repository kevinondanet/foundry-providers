using System.Globalization;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Scorers;

namespace InspectAzureAI.Eval.Runner.Scoring;

/// <summary>
/// Port of the log-header helpers of <c>_eval/score.py</c> (<c>metrics_from_log_header</c>, <c>metric_from_log</c>,
/// <c>reducers_from_log_header</c>) and <c>_eval/task/log.py</c> <c>resolve_eval_scorers</c>. Python re-instantiates
/// metrics and reducers through its registry; this port has a fixed table of the built-in metrics and
/// <see cref="Reducers.Create"/> for reducers.
/// </summary>
internal static class LogHeader
{
    /// <summary>
    /// Port of <c>metrics_from_log_header</c>: the task-level metrics recorded in <see cref="EvalSpec.Metrics"/>, re-created
    /// by name (null when the header records none). Metric groups (a dict of metric lists) are not supported by this
    /// port's flat metric lists and throw <see cref="NotSupportedException"/>, as does a metric it cannot re-create.
    /// </summary>
    public static IReadOnlyList<MetricDef>? MetricsFromLogHeader(EvalLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        switch (log.Eval.Metrics)
        {
            case null:
                return null;
            case JsonArray { Count: 0 }:
                return null;
            case JsonArray items:
            {
                var metrics = new List<MetricDef>(items.Count);
                foreach (var item in items)
                {
                    if (item is not JsonObject definition || definition["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name))
                    {
                        throw new NotSupportedException("The log header declares a group of metrics (a dict of metric lists), which this port cannot re-create; pass the metrics explicitly.");
                    }

                    metrics.Add(MetricFromLog(name, definition["options"] as JsonObject));
                }

                return metrics;
            }

            default:
                throw new NotSupportedException("The log header declares metric groups (a dict of metric lists), which this port cannot re-create; pass the metrics explicitly.");
        }
    }

    /// <summary>
    /// Port of <c>metric_from_log</c> for the built-in metrics: <c>accuracy</c>, <c>mean</c>, <c>stderr</c> (<c>cluster</c>),
    /// <c>std</c>, <c>var</c>, <c>bootstrap_stderr</c> (<c>num_samples</c>), <c>ci_wilson</c> (<c>level</c>, <c>cluster</c>),
    /// <c>perplexity_per_token</c> and <c>perplexity_per_seq</c>. An <c>inspect_ai/</c> prefix is accepted. Any other metric,
    /// or an option the table does not cover, is a <see cref="NotSupportedException"/> rather than a silently different metric.
    /// </summary>
    public static MetricDef MetricFromLog(string name, JsonObject? options)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        const string prefix = "inspect_ai/";
        var metricName = name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
        var opts = options ?? [];
        return metricName switch
        {
            "accuracy" => Only(opts, name, [], () => Metrics.Accuracy()),
            "mean" => Only(opts, name, [], () => Metrics.Mean()),
            "stderr" => Only(opts, name, ["cluster"], () => Metrics.Stderr(cluster: Str(opts, "cluster"))),
            "std" => Only(opts, name, [], () => Metrics.Std()),
            "var" => Only(opts, name, [], () => Metrics.Var()),
            "bootstrap_stderr" => Only(opts, name, ["num_samples"], () => Metrics.BootstrapStderr(Int(opts, "num_samples") ?? 1000)),
            "ci_wilson" => Only(opts, name, ["level", "cluster"], () => Metrics.CiWilson(Dbl(opts, "level") ?? 0.95, cluster: Str(opts, "cluster"))),
            "perplexity_per_token" => Only(opts, name, [], Metrics.PerplexityPerToken),
            "perplexity_per_seq" => Only(opts, name, [], Metrics.PerplexityPerSeq),
            _ => throw new NotSupportedException($"The metric '{name}' recorded in the log header cannot be re-created by this port; pass the metrics explicitly."),
        };
    }

    /// <summary>Port of <c>reducers_from_log_header</c> (<c>create_reducers(config.epochs_reducer)</c>): null when the header records none.</summary>
    public static IReadOnlyList<ScoreReducer>? ReducersFromLogHeader(EvalLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return log.Eval.Config.EpochsReducer?.Select(Reducers.Create).ToList();
    }

    /// <summary>
    /// Port of <c>reducer_log_names</c>: the registry names to record in <see cref="EvalConfig.EpochsReducer"/>. A reducer
    /// without one (not built by <see cref="Reducers"/>) cannot be recorded, which Python reports as an error too.
    /// </summary>
    public static IReadOnlyList<string> ReducerLogNames(IReadOnlyList<ScoreReducer> reducers)
    {
        ArgumentNullException.ThrowIfNull(reducers);
        var names = new List<string>(reducers.Count);
        foreach (var reducer in reducers)
        {
            names.Add(Reducers.NameOf(reducer) ?? throw new ArgumentException("An epochs reducer has no registry name and cannot be recorded in the log header; build it with the Reducers factory methods.", nameof(reducers)));
        }

        return names;
    }

    /// <summary>
    /// Port of <c>resolve_eval_scorers([as_scorer_spec(s) ...])</c>: the <see cref="EvalScorer"/> header entries for the scorers
    /// applied. A <see cref="ScorerDef"/> carries no instantiation arguments or metadata, so <c>options</c> and <c>metadata</c>
    /// are the empty dicts Python writes for an argument-less scorer.
    /// </summary>
    public static IReadOnlyList<EvalScorer> ToEvalScorers(IEnumerable<ScorerDef> scorers)
    {
        ArgumentNullException.ThrowIfNull(scorers);
        return scorers.Select(scorer => new EvalScorer(scorer.Name)
        {
            Options = new Dictionary<string, object?>(StringComparer.Ordinal),
            Metrics = new JsonArray(scorer.Metrics.Select(metric => (JsonNode)new JsonObject { ["name"] = metric.Name, ["options"] = new JsonObject() }).ToArray()),
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal),
        }).ToList();
    }

    private static MetricDef Only(JsonObject options, string name, string[] allowed, Func<MetricDef> create)
    {
        foreach (var (key, _) in options)
        {
            if (!allowed.Contains(key, StringComparer.Ordinal))
            {
                throw new NotSupportedException($"The metric '{name}' recorded in the log header has the option '{key}', which this port cannot re-create; pass the metrics explicitly.");
            }
        }

        return create();
    }

    private static string? Str(JsonObject options, string key) => options[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Int(JsonObject options, string key) => options[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static double? Dbl(JsonObject options, string key) =>
        options[key] is JsonValue value
            ? value.TryGetValue<double>(out var number) ? number : double.Parse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture)
            : null;
}

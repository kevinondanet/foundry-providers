using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Runner.Scoring;

namespace InspectAzureAI.Eval.Analysis;

/// <summary>Port of <c>analysis/_dataframe/evals/columns.py</c> <c>EvalColumn</c>: a column read from an <see cref="EvalLog"/>.</summary>
public class EvalColumn : Column
{
    private readonly Func<EvalLog, JsonNode?>? _extract;

    /// <summary>A column read from a JSONPath into the log (e.g. <c>eval.task</c>).</summary>
    public EvalColumn(string name, string path, bool required = false, object? defaultValue = null, ColumnType? type = null, Func<JsonNode?, JsonNode?>? value = null)
        : base(name, path, required, defaultValue, type, value)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
    }

    /// <summary>A column whose value is computed from the log.</summary>
    public EvalColumn(string name, Func<EvalLog, JsonNode?> extract, bool required = false, object? defaultValue = null, ColumnType? type = null, Func<JsonNode?, JsonNode?>? value = null)
        : base(name, null, required, defaultValue, type, value)
    {
        ArgumentNullException.ThrowIfNull(extract);
        _extract = extract;
    }

    internal override JsonNode? Extract(ImportTarget target) =>
        _extract is not null && target.Log is { } log ? _extract(log) : throw new InvalidOperationException("column must have path or extract function");
}

/// <summary>Port of the column groups of <c>analysis/_dataframe/evals/columns.py</c> and the extractors of <c>evals/extract.py</c>.</summary>
public static class EvalColumns
{
    /// <summary>Port of <c>EvalId</c>.</summary>
    public static IReadOnlyList<Column> Id { get; } =
    [
        new EvalColumn("eval_id", "eval.eval_id", required: true),
    ];

    /// <summary>Port of <c>EvalLogPath</c>: the <c>log</c> column.</summary>
    public static IReadOnlyList<Column> LogPath { get; } =
    [
        new EvalColumn("log", EvalLogLocation, required: true),
    ];

    /// <summary>Port of <c>EvalInfo</c>: eval basic information columns.</summary>
    public static IReadOnlyList<Column> Info { get; } =
    [
        new EvalColumn("eval_set_id", "eval.eval_set_id"),
        new EvalColumn("run_id", "eval.run_id", required: true),
        new EvalColumn("task_id", "eval.task_id", required: true),
        .. LogPath,
        new EvalColumn("created", "eval.created", type: ColumnType.DateTime, required: true),
        new EvalColumn("tags", "tags", defaultValue: "", value: Extract.ListAsStr),
        new EvalColumn("git_origin", "eval.revision.origin"),
        new EvalColumn("git_commit", "eval.revision.commit"),
        new EvalColumn("packages", "eval.packages"),
        new EvalColumn("metadata", "metadata"),
    ];

    /// <summary>Port of <c>EvalTask</c>: eval task configuration columns.</summary>
    public static IReadOnlyList<Column> Task { get; } =
    [
        new EvalColumn("task_name", "eval.task", required: true, value: Extract.RemoveNamespace),
        new EvalColumn("task_display_name", EvalLogTaskDisplayName),
        new EvalColumn("task_version", "eval.task_version", required: true),
        new EvalColumn("task_file", "eval.task_file"),
        new EvalColumn("task_attribs", "eval.task_attribs"),
        new EvalColumn("task_arg_*", "eval.task_args"),
        new EvalColumn("solver", "eval.solver"),
        new EvalColumn("solver_args", "eval.solver_args"),
        new EvalColumn("sandbox_type", "eval.sandbox.type"),
        new EvalColumn("sandbox_config", "eval.sandbox.config"),
    ];

    /// <summary>Port of <c>EvalModel</c>: eval model columns (<c>model_args</c> reads <c>eval.model_base_url</c>, as in Python).</summary>
    public static IReadOnlyList<Column> Model { get; } =
    [
        new EvalColumn("model", "eval.model", required: true),
        new EvalColumn("model_base_url", "eval.model_base_url"),
        new EvalColumn("model_args", "eval.model_base_url"),
        new EvalColumn("model_generate_config", "eval.model_generate_config"),
        new EvalColumn("model_roles", "eval.model_roles"),
    ];

    /// <summary>Port of <c>EvalDataset</c>: eval dataset columns.</summary>
    public static IReadOnlyList<Column> Dataset { get; } =
    [
        new EvalColumn("dataset_name", "eval.dataset.name"),
        new EvalColumn("dataset_location", "eval.dataset.location"),
        new EvalColumn("dataset_samples", "eval.dataset.samples"),
        new EvalColumn("dataset_sample_ids", "eval.dataset.sample_ids"),
        new EvalColumn("dataset_shuffled", "eval.dataset.shuffled"),
    ];

    /// <summary>Port of <c>EvalConfiguration</c>: eval configuration columns.</summary>
    public static IReadOnlyList<Column> Configuration { get; } =
    [
        new EvalColumn("epochs", "eval.config.epochs"),
        new EvalColumn("epochs_reducer", "eval.config.epochs_reducer"),
        new EvalColumn("approval", "eval.config.approval"),
        new EvalColumn("message_limit", "eval.config.message_limit"),
        new EvalColumn("token_limit", "eval.config.token_limit"),
        new EvalColumn("token_limit_type", "eval.config.token_limit_type"),
        new EvalColumn("turn_limit", "eval.config.turn_limit"),
        new EvalColumn("time_limit", "eval.config.time_limit"),
        new EvalColumn("working_limit", "eval.config.working_limit"),
    ];

    /// <summary>Port of <c>EvalResults</c>: status, error, sample counts and the headline metric.</summary>
    public static IReadOnlyList<Column> Results { get; } =
    [
        new EvalColumn("status", "status", required: true),
        new EvalColumn("error_message", "error.message"),
        new EvalColumn("error_traceback", "error.traceback"),
        new EvalColumn("total_samples", "results.total_samples"),
        new EvalColumn("completed_samples", "results.completed_samples"),
        new EvalColumn("score_headline_name", EvalLogHeadlineName),
        new EvalColumn("score_headline_score", EvalLogHeadlineScore),
        new EvalColumn("score_headline_metric", EvalLogHeadlineMetric),
        new EvalColumn("score_headline_value", EvalLogHeadlineValue),
        new EvalColumn("score_headline_stderr", EvalLogHeadlineStderr),
    ];

    /// <summary>Port of <c>EvalScores</c>: one <c>score_{scorer}_{metric}</c> column per metric.</summary>
    public static IReadOnlyList<Column> Scores { get; } =
    [
        new EvalColumn("score_*_*", EvalLogScoresDict),
    ];

    /// <summary>Port of <c>EvalColumns</c>: the default columns of the evals table.</summary>
    public static IReadOnlyList<Column> Default { get; } =
    [
        .. Info,
        .. Task,
        .. Model,
        .. Dataset,
        .. Configuration,
        .. Results,
        .. Scores,
    ];

    /// <summary>
    /// Port of <c>eval_log_location</c>: the log's local path (<c>file://</c> stripped). A log with no location
    /// yields the current directory, which is what Python's <c>native_path(None)</c> resolves to.
    /// </summary>
    public static JsonNode? EvalLogLocation(EvalLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var location = log.Location;
        if (string.IsNullOrEmpty(location))
        {
            return JsonValue.Create(Directory.GetCurrentDirectory());
        }

        if (location.StartsWith("file://", StringComparison.Ordinal))
        {
            location = new Uri(location).LocalPath;
        }

        return JsonValue.Create(location);
    }

    /// <summary>Port of <c>eval_log_task_display_name</c>: the display name, else the task name without its namespace.</summary>
    public static JsonNode? EvalLogTaskDisplayName(EvalLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return log.Eval.TaskDisplayName is { } display ? JsonValue.Create(display) : Extract.RemoveNamespace(JsonValue.Create(log.Eval.Task));
    }

    /// <summary>
    /// Port of <c>eval_log_scores_dict</c>: one <c>{score: {metric: value}}</c> dictionary per score, keyed
    /// <c>{name}_{reducer}</c> only where the same score name and metric key would otherwise collide (e.g.
    /// <c>epochs_reducer=["mean", "max"]</c>); null when the log has no results.
    /// </summary>
    public static JsonNode? EvalLogScoresDict(EvalLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (log.Results is null)
        {
            return null;
        }

        var counts = new Dictionary<(string, string), int>();
        foreach (var score in log.Results.Scores)
        {
            foreach (var metricKey in score.Metrics.Keys)
            {
                var key = (score.Name, metricKey);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        var metrics = new JsonArray();
        foreach (var score in log.Results.Scores)
        {
            var scoreMetrics = new JsonObject();
            foreach (var (metricKey, metric) in score.Metrics)
            {
                var scoreKey = counts[(score.Name, metricKey)] > 1 && score.Reducer is not null ? $"{score.Name}_{score.Reducer}" : score.Name;
                if (scoreMetrics[scoreKey] is not JsonObject bucket)
                {
                    bucket = new JsonObject();
                    scoreMetrics[scoreKey] = bucket;
                }

                bucket[metricKey] = MetricValue(metric.Value);
            }

            metrics.Add(scoreMetrics);
        }

        return metrics;
    }

    /// <summary>Port of <c>eval_log_headline_name</c>: the scorer of the headline metric (which differs from the score for dict-valued scorers).</summary>
    public static JsonNode? EvalLogHeadlineName(EvalLog log) => Headline(log) is { } resolved ? JsonValue.Create(resolved.Score.Scorer) : null;

    /// <summary>Port of <c>eval_log_headline_score</c>: the score of the headline metric.</summary>
    public static JsonNode? EvalLogHeadlineScore(EvalLog log) => Headline(log) is { } resolved ? JsonValue.Create(resolved.Score.Name) : null;

    /// <summary>Port of <c>eval_log_headline_metric</c>: the metric key of the headline metric.</summary>
    public static JsonNode? EvalLogHeadlineMetric(EvalLog log) => Headline(log) is { } resolved ? JsonValue.Create(resolved.Name) : null;

    /// <summary>Port of <c>eval_log_headline_value</c>.</summary>
    public static JsonNode? EvalLogHeadlineValue(EvalLog log) => Headline(log) is { } resolved ? MetricValue(resolved.Metric.Value) : null;

    /// <summary>Port of <c>eval_log_headline_stderr</c>: the <c>stderr</c> metric of the headline score, when it has one.</summary>
    public static JsonNode? EvalLogHeadlineStderr(EvalLog log) =>
        Headline(log) is { } resolved && resolved.Score.Metrics.TryGetValue("stderr", out var stderr) ? JsonValue.Create(stderr.Value) : null;

    private static ResolvedHeadlineMetric? Headline(EvalLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return HeadlineMetrics.ForLog(log);
    }

    /// <summary>A metric value as JSON (always a float: <see cref="EvalMetric.Value"/> is a double, where Python keeps <c>int | float</c>); non-finite values become the log-format sentinels.</summary>
    private static JsonNode? MetricValue(double value)
    {
        if (double.IsNaN(value))
        {
            return JsonValue.Create(Log.Json.PythonJsonFormat.NaNSentinel);
        }

        if (double.IsPositiveInfinity(value))
        {
            return JsonValue.Create(Log.Json.PythonJsonFormat.PositiveInfinitySentinel);
        }

        if (double.IsNegativeInfinity(value))
        {
            return JsonValue.Create(Log.Json.PythonJsonFormat.NegativeInfinitySentinel);
        }

        return JsonValue.Create(value);
    }
}

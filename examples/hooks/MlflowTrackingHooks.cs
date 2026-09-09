using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Hooks;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Examples.Hooks;

using Hooks = InspectAzureAI.Eval.Hooks.Hooks;

/// <summary>Port of the per-model accumulator of <c>MlflowTrackingHooks.on_model_usage</c>.</summary>
public sealed record ModelUsageStats(int Calls, long InputTokens, long OutputTokens, long TotalTokens, double TotalDuration);

/// <summary>
/// Port of <c>examples/hooks/mlflow_tracking.py</c> <c>MlflowTrackingHooks</c> (<c>@hooks(name="mlflow_tracking",
/// description="MLflow Tracking")</c>): tracks evaluations in MLflow with one parent run per eval invocation and a
/// nested child run per task; logs the task configuration as parameters, per-sample scores and timings as step
/// metrics, model and tool events, aggregate results and usage, and the sample table and eval log JSON as artifacts.
/// Enabled when <c>MLFLOW_TRACKING_URI</c> is set (optionally <c>MLFLOW_EXPERIMENT_NAME</c>,
/// <c>MLFLOW_INSPECT_LOG_ARTIFACTS=false</c>). Deviations: the <c>mlflow</c> client is <see cref="MlflowClient"/> over
/// the REST API; per-sample and per-event metrics go to the task's run by <c>eval_id</c> rather than MLflow's
/// "active run"; artifact files are named <c>sample_results_&lt;eval_id&gt;.json</c> / <c>eval_log_&lt;eval_id&gt;.json</c>
/// (no <c>mkstemp</c> suffix); explicit settings can replace the environment lookup.
/// </summary>
public sealed class MlflowTrackingHooks : Hooks
{
    public const string HookName = "mlflow_tracking";

    public const string HookDescription = "MLflow Tracking";

    /// <summary>MLflow's param value limit (<c>_safe_log_params</c>).</summary>
    public const int ParamLimit = 500;

    private readonly Func<MlflowSettings?> _settings;

    private readonly HttpMessageHandler? _handler;

    private readonly object _sync = new();

    private readonly Dictionary<string, MlflowRun> _taskRuns = new(StringComparer.Ordinal);

    private readonly Dictionary<string, EvalSpec> _tasks = new(StringComparer.Ordinal);

    private readonly Dictionary<string, int> _sampleCounts = new(StringComparer.Ordinal);

    private readonly Dictionary<string, ModelUsageStats> _modelUsage = new(StringComparer.Ordinal);

    private readonly Dictionary<string, Dictionary<string, int>> _eventCounts = new(StringComparer.Ordinal);

    private MlflowClient? _client;

    private string? _experimentId;

    private MlflowRun? _parentRun;

    /// <summary>The CLI's constructor: settings from the environment, a real HTTP client (Python's module-level <c>os.getenv</c> checks).</summary>
    public MlflowTrackingHooks() : this(MlflowSettings.FromEnvironment)
    {
    }

    /// <summary>Explicit settings (the hook is then enabled), optionally over a fake server's handler.</summary>
    public MlflowTrackingHooks(MlflowSettings settings, HttpMessageHandler? handler = null) : this(() => settings, handler)
    {
        ArgumentNullException.ThrowIfNull(settings);
    }

    public MlflowTrackingHooks(Func<MlflowSettings?> settings, HttpMessageHandler? handler = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _handler = handler;
    }

    /// <summary>Port of <c>enabled()</c>: <c>MLFLOW_TRACKING_URI</c> is set (or explicit settings were given).</summary>
    public override bool Enabled => _settings() is not null;

    /// <summary>The parent run of the current eval invocation, if one is open.</summary>
    public MlflowRun? ParentRun
    {
        get
        {
            lock (_sync)
            {
                return _parentRun;
            }
        }
    }

    /// <summary>The per-model usage accumulated by <see cref="OnModelUsageAsync"/> (Python only accumulates it; it is cleared at run end).</summary>
    public IReadOnlyDictionary<string, ModelUsageStats> ModelUsage
    {
        get
        {
            lock (_sync)
            {
                return new Dictionary<string, ModelUsageStats>(_modelUsage, StringComparer.Ordinal);
            }
        }
    }

    public override async Task OnRunStartAsync(RunStart data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (_settings() is not { } settings)
        {
            return;
        }

        var client = new MlflowClient(settings.TrackingUri, _handler);
        var experimentId = await client.GetOrCreateExperimentAsync(settings.ExperimentName, cancellationToken).ConfigureAwait(false);
        var parent = await client.CreateRunAsync(
            experimentId,
            $"inspect-{Prefix(data.RunId, 8)}",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["inspect.run_id"] = data.RunId,
                ["inspect.task_count"] = data.TaskNames.Count.ToString(CultureInfo.InvariantCulture),
                ["inspect.tasks"] = string.Join(", ", data.TaskNames),
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _client = client;
            _experimentId = experimentId;
            _parentRun = parent;
        }
    }

    public override async Task OnRunEndAsync(RunEnd data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        MlflowClient? client;
        MlflowRun? parent;
        List<MlflowRun> remaining;
        lock (_sync)
        {
            client = _client;
            parent = _parentRun;
            remaining = _taskRuns.Values.ToList();
            _taskRuns.Clear();
            _parentRun = null;
            _client = null;
            _experimentId = null;
            _tasks.Clear();
            _sampleCounts.Clear();
            _modelUsage.Clear();
            _eventCounts.Clear();
        }

        if (client is null)
        {
            return;
        }

        using (client)
        {
            // End any remaining task runs (shouldn't happen normally)
            foreach (var run in remaining)
            {
                await client.UpdateRunAsync(run.RunId, "FINISHED", cancellationToken).ConfigureAwait(false);
            }

            // Log run-level summary on parent
            if (parent is not null)
            {
                await client.UpdateRunAsync(parent.RunId, data.Exception is null ? "FINISHED" : "FAILED", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public override async Task OnTaskStartAsync(TaskStart data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        MlflowClient? client;
        string? experimentId;
        MlflowRun? parent;
        lock (_sync)
        {
            _tasks[data.EvalId] = data.Spec;
            _sampleCounts[data.EvalId] = 0;
            client = _client;
            experimentId = _experimentId;
            parent = _parentRun;
        }

        if (client is null || experimentId is null || parent is null)
        {
            return;
        }

        // Start a nested child run for this task
        var taskRun = await client.CreateRunAsync(
            experimentId,
            data.Spec.Task,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["inspect.eval_id"] = data.EvalId,
                ["inspect.run_id"] = data.RunId,
                ["inspect.task"] = data.Spec.Task,
                ["inspect.model"] = data.Spec.Model,
            },
            parent.RunId,
            cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _taskRuns[data.EvalId] = taskRun;
        }

        // Log task configuration as parameters
        await SafeLogParamsAsync(client, taskRun.RunId, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["task"] = data.Spec.Task,
            ["model"] = data.Spec.Model,
            ["task_version"] = data.Spec.TaskVersion,
            ["dataset.name"] = data.Spec.Dataset.Name ?? "",
            ["dataset.samples"] = data.Spec.Dataset.Samples?.ToString(CultureInfo.InvariantCulture) ?? "",
            ["solver"] = data.Spec.Solver ?? "",
        }, cancellationToken).ConfigureAwait(false);

        // Log task args (user-provided)
        if (data.Spec.TaskArgsPassed is { Count: > 0 } taskArgs)
        {
            await SafeLogParamsAsync(client, taskRun.RunId, taskArgs.ToDictionary(pair => $"task_arg.{pair.Key}", pair => PythonStr(pair.Value), StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
        }

        // Log generate config
        var config = data.Spec.ModelGenerateConfig;
        var genParams = new Dictionary<string, string>(StringComparer.Ordinal);
        if (config.Temperature is { } temperature)
        {
            genParams["temperature"] = MlflowClient.Str(temperature);
        }

        if (config.TopP is { } topP)
        {
            genParams["top_p"] = MlflowClient.Str(topP);
        }

        if (config.MaxTokens is { } maxTokens)
        {
            genParams["max_tokens"] = maxTokens.ToString(CultureInfo.InvariantCulture);
        }

        if (genParams.Count > 0)
        {
            await SafeLogParamsAsync(client, taskRun.RunId, genParams, cancellationToken).ConfigureAwait(false);
        }

        // Log tags if present
        if (data.Spec.Tags is { Count: > 0 } tags)
        {
            await SafeLogParamsAsync(client, taskRun.RunId, new Dictionary<string, string>(StringComparer.Ordinal) { ["tags"] = string.Join(", ", tags) }, cancellationToken).ConfigureAwait(false);
        }
    }

    public override async Task OnTaskEndAsync(TaskEnd data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        MlflowClient? client;
        MlflowRun? taskRun;
        Dictionary<string, int>? eventCounts;
        MlflowSettings? settings = _settings();
        lock (_sync)
        {
            client = _client;
            _taskRuns.TryGetValue(data.EvalId, out taskRun);
            _eventCounts.TryGetValue(data.EvalId, out eventCounts);
        }

        if (client is null || taskRun is null)
        {
            return;
        }

        var log = data.Log;

        // Log aggregate results
        if (log.Results is { Scores.Count: > 0 } results)
        {
            foreach (var evalScore in results.Scores)
            {
                foreach (var (metricName, metric) in evalScore.Metrics)
                {
                    await SafeLogMetricAsync(client, taskRun.RunId, $"{evalScore.Name}/{metricName}", metric.Value, null, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // Log completion stats
        if (log.Results is { } stats)
        {
            await SafeLogMetricAsync(client, taskRun.RunId, "total_samples", stats.TotalSamples, null, cancellationToken).ConfigureAwait(false);
            await SafeLogMetricAsync(client, taskRun.RunId, "completed_samples", stats.CompletedSamples, null, cancellationToken).ConfigureAwait(false);
        }

        // Log aggregate model usage from stats
        foreach (var (modelName, usage) in log.Stats.ModelUsage)
        {
            var prefix = $"usage/{modelName}";
            await SafeLogMetricAsync(client, taskRun.RunId, $"{prefix}/input_tokens", usage.InputTokens, null, cancellationToken).ConfigureAwait(false);
            await SafeLogMetricAsync(client, taskRun.RunId, $"{prefix}/output_tokens", usage.OutputTokens, null, cancellationToken).ConfigureAwait(false);
            await SafeLogMetricAsync(client, taskRun.RunId, $"{prefix}/total_tokens", usage.TotalTokens, null, cancellationToken).ConfigureAwait(false);
        }

        // Log event counts
        if (eventCounts is { Count: > 0 })
        {
            await SafeLogMetricAsync(client, taskRun.RunId, "total_model_calls", eventCounts.GetValueOrDefault("model_calls"), null, cancellationToken).ConfigureAwait(false);
            await SafeLogMetricAsync(client, taskRun.RunId, "total_tool_calls", eventCounts.GetValueOrDefault("tool_calls"), null, cancellationToken).ConfigureAwait(false);
        }

        // Log eval artifacts (sample results table + eval log JSON)
        if (settings?.LogArtifacts != false)
        {
            await LogEvalArtifactsAsync(client, taskRun, log, cancellationToken).ConfigureAwait(false);
        }

        // Log eval status
        await client.UpdateRunAsync(taskRun.RunId, log.Status == EvalStatus.Success ? "FINISHED" : "FAILED", cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _taskRuns.Remove(data.EvalId);
            _tasks.Remove(data.EvalId);
            _eventCounts.Remove(data.EvalId);
        }
    }

    public override async Task OnSampleEndAsync(SampleEnd data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        MlflowClient? client;
        MlflowRun? taskRun;
        int sampleIdx;
        lock (_sync)
        {
            client = _client;
            if (!_taskRuns.TryGetValue(data.EvalId, out taskRun))
            {
                return;
            }

            // Increment sample counter
            sampleIdx = _sampleCounts.GetValueOrDefault(data.EvalId);
            _sampleCounts[data.EvalId] = sampleIdx + 1;
        }

        if (client is null)
        {
            return;
        }

        var sample = data.Sample;

        // Log per-sample scores as step metrics
        foreach (var (scorerName, score) in sample.Scores ?? new Dictionary<string, Score>())
        {
            if (ScoreToNumeric(score.Value) is { } numeric)
            {
                await SafeLogMetricAsync(client, taskRun.RunId, $"sample/{scorerName}", numeric, sampleIdx, cancellationToken).ConfigureAwait(false);
            }
        }

        // Log sample timing
        if (sample.TotalTime is { } totalTime)
        {
            await SafeLogMetricAsync(client, taskRun.RunId, "sample/total_time", totalTime, sampleIdx, cancellationToken).ConfigureAwait(false);
        }
    }

    public override async Task OnSampleEventAsync(SampleEvent data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        MlflowClient? client;
        MlflowRun? taskRun;
        int step;
        var evt = data.Event;
        lock (_sync)
        {
            client = _client;
            if (!_taskRuns.TryGetValue(data.EvalId, out taskRun))
            {
                return;
            }

            // Initialize per-task event counters
            if (!_eventCounts.TryGetValue(data.EvalId, out var counters))
            {
                counters = new Dictionary<string, int>(StringComparer.Ordinal) { ["model_calls"] = 0, ["tool_calls"] = 0 };
                _eventCounts[data.EvalId] = counters;
            }

            switch (evt)
            {
                case ModelEvent:
                    step = counters["model_calls"]++;
                    break;
                case ToolEvent:
                    step = counters["tool_calls"]++;
                    break;
                default:
                    return;
            }
        }

        if (client is null)
        {
            return;
        }

        switch (evt)
        {
            case ModelEvent model:
                await SafeLogMetricAsync(client, taskRun.RunId, "event/model_call", step, step, cancellationToken).ConfigureAwait(false);
                if (model.Output.Usage is { } usage)
                {
                    await SafeLogMetricAsync(client, taskRun.RunId, "event/input_tokens", usage.InputTokens, step, cancellationToken).ConfigureAwait(false);
                    await SafeLogMetricAsync(client, taskRun.RunId, "event/output_tokens", usage.OutputTokens, step, cancellationToken).ConfigureAwait(false);
                }

                if (model.WorkingTime is { } modelTime)
                {
                    await SafeLogMetricAsync(client, taskRun.RunId, "event/model_time", modelTime, step, cancellationToken).ConfigureAwait(false);
                }

                break;

            case ToolEvent tool:
                await SafeLogMetricAsync(client, taskRun.RunId, "event/tool_call", step, step, cancellationToken).ConfigureAwait(false);
                await SafeLogParamsAsync(client, taskRun.RunId, new Dictionary<string, string>(StringComparer.Ordinal) { [$"tool_call.{step}.function"] = Prefix(tool.Function, ParamLimit) }, cancellationToken).ConfigureAwait(false);
                if (tool.Error is not null)
                {
                    await SafeLogMetricAsync(client, taskRun.RunId, "event/tool_error", 1, step, cancellationToken).ConfigureAwait(false);
                }

                if (tool.Working is { } toolTime)
                {
                    await SafeLogMetricAsync(client, taskRun.RunId, "event/tool_time", toolTime.TotalSeconds, step, cancellationToken).ConfigureAwait(false);
                }

                break;
        }
    }

    public override Task OnModelUsageAsync(ModelUsageData data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        // Accumulate model usage for the current context
        lock (_sync)
        {
            var stats = _modelUsage.GetValueOrDefault(data.ModelName) ?? new ModelUsageStats(0, 0, 0, 0, 0.0);
            _modelUsage[data.ModelName] = stats with
            {
                Calls = stats.Calls + 1,
                InputTokens = stats.InputTokens + data.Usage.InputTokens,
                OutputTokens = stats.OutputTokens + data.Usage.OutputTokens,
                TotalTokens = stats.TotalTokens + data.Usage.TotalTokens,
                TotalDuration = stats.TotalDuration + data.CallDuration,
            };
        }

        return Task.CompletedTask;
    }

    /// <summary>Port of <c>_score_to_numeric</c>: numbers as they are; C/I/P and correct/incorrect mapped; anything else null.</summary>
    public static double? ScoreToNumeric(ScoreValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            ScoreValue.Num num => num.Value,
            ScoreValue.Bool flag => flag.Value ? 1.0 : 0.0,
            ScoreValue.Str str => str.Value switch
            {
                "C" => 1.0,
                "I" => 0.0,
                "P" => 0.5,
                "correct" => 1.0,
                "incorrect" => 0.0,
                _ => null,
            },
            _ => null,
        };
    }

    /// <summary>Port of <c>_truncate(text, max_len)</c>: at most <paramref name="maxLength"/> characters, the last three replaced by an ellipsis.</summary>
    public static string Truncate(string? text, int maxLength = 500)
    {
        var s = text ?? "";
        return s.Length > maxLength ? s[..(maxLength - 3)] + "..." : s;
    }

    /// <summary>Port of the value truncation of <c>_safe_log_params</c>: 500 characters, the last three replaced by an ellipsis.</summary>
    public static string TruncateParam(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length > ParamLimit ? value[..(ParamLimit - 3)] + "..." : value;
    }

    /// <summary>Port of <c>_log_sample_table</c>'s rows: id, epoch, input, target, total_time, error, output and the per-scorer value/explanation columns.</summary>
    public static JsonArray SampleTable(EvalLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var rows = new JsonArray();
        foreach (var sample in log.Samples ?? [])
        {
            var row = new JsonObject
            {
                ["id"] = IdNode(sample.Id),
                ["epoch"] = sample.Epoch,
                ["input"] = Truncate(sample.Input.ToString(), 500),
                ["target"] = Truncate(TargetText(sample.Target), 300),
                ["total_time"] = sample.TotalTime,
                ["error"] = sample.Error?.Message,
                ["output"] = sample.Output.Choices.Count > 0 ? Truncate(sample.Output.Choices[0].Message.Text, 500) : "",
            };
            foreach (var (scorerName, score) in sample.Scores ?? new Dictionary<string, Score>())
            {
                row[$"score/{scorerName}"] = score.Value.ToJson();
                if (!string.IsNullOrEmpty(score.Explanation))
                {
                    row[$"explanation/{scorerName}"] = Truncate(score.Explanation, 300);
                }
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Port of <c>log.model_dump(mode="json", exclude={"samples"})</c> as indented JSON.</summary>
    public static string EvalLogJson(EvalLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var node = JsonNode.Parse(EvalLogWriter.Serialize(log)) as JsonObject ?? [];
        node.Remove("samples");
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task LogEvalArtifactsAsync(MlflowClient client, MlflowRun taskRun, EvalLog log, CancellationToken cancellationToken)
    {
        var evalId = log.Eval.EvalId;
        try
        {
            if (log.Samples is { Count: > 0 })
            {
                var table = SampleTable(log).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                await client.LogArtifactAsync(taskRun, "sample_results", $"sample_results_{evalId}.json", Encoding.UTF8.GetBytes(table), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ProviderLogger.Info($"Failed to log sample results artifact: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            await client.LogArtifactAsync(taskRun, "eval_logs", $"eval_log_{evalId}.json", Encoding.UTF8.GetBytes(EvalLogJson(log)), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ProviderLogger.Info($"Failed to log eval log artifact: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Port of <c>_safe_log_params</c>: each param truncated to 500 characters, failures ignored.</summary>
    private static async Task SafeLogParamsAsync(MlflowClient client, string runId, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        foreach (var (key, value) in parameters)
        {
            try
            {
                await client.LogParamAsync(runId, key, TruncateParam(value), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Python: except Exception: pass
            }
        }
    }

    private static async Task SafeLogMetricAsync(MlflowClient client, string runId, string key, double value, int? step, CancellationToken cancellationToken)
    {
        try
        {
            await client.LogMetricAsync(runId, key, value, step, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Python: except Exception: pass
        }
    }

    /// <summary>A sample id as a typed JSON value (a boxed <c>object</c> would need the reflection serializer).</summary>
    public static JsonNode IdNode(object id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return id switch
        {
            int number => JsonValue.Create(number),
            long number => JsonValue.Create(number),
            string text => JsonValue.Create(text),
            _ => JsonValue.Create(id.ToString() ?? ""),
        };
    }

    private static string TargetText(Target target) => target.Count == 1 ? target.Text : "[" + string.Join(", ", target.Values.Select(v => $"'{v}'")) + "]";

    private static string Prefix(string text, int length) => text.Length > length ? text[..length] : text;

    /// <summary>Python's <c>str()</c> for a task arg value.</summary>
    private static string PythonStr(object? value) => value switch
    {
        null => "None",
        bool flag => flag ? "True" : "False",
        string text => text,
        double number => MlflowClient.Str(number),
        float number => MlflowClient.Str(number),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        JsonNode node => node.ToJsonString(),
        _ => value.ToString() ?? "",
    };
}

using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Hooks;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Examples.Hooks;

using Hooks = InspectAzureAI.Eval.Hooks.Hooks;

/// <summary>
/// Port of <c>examples/hooks/mlflow_tracing.py</c> <c>MlflowTracingHooks</c> (<c>@hooks(name="mlflow_tracing",
/// description="MLflow Tracing")</c>): maps the evaluation's execution flow to MLflow trace spans, one trace per eval
/// run with a span tree mirroring the eval hierarchy — <c>eval_run:&lt;run&gt;</c> (CHAIN) → <c>task:&lt;name&gt;</c> (CHAIN) →
/// <c>sample:&lt;id&gt;</c> (CHAIN) → <c>model:&lt;model&gt;</c> (LLM), <c>tool:&lt;function&gt;</c> (TOOL), <c>score</c>
/// (EVALUATOR) and the transcript's own spans. Enabled when <c>MLFLOW_TRACKING_URI</c> is set and
/// <c>MLFLOW_INSPECT_TRACING</c> is <c>true</c>. Deviation: <c>mlflow.start_span_no_context</c> has no REST twin, so the
/// spans are <see cref="MlflowSpan"/> objects kept in an <see cref="MlflowTrace"/> and logged together when the run
/// span ends (the trace info to the v3 traces endpoint, the spans as its <c>traces.json</c> artifact), which is also
/// when the Python client's exporter ships a trace; span failures are swallowed like Python's <c>logger.debug</c>.
/// </summary>
public sealed class MlflowTracingHooks : Hooks
{
    public const string HookName = "mlflow_tracing";

    public const string HookDescription = "MLflow Tracing";

    private readonly Func<MlflowSettings?> _settings;

    private readonly HttpMessageHandler? _handler;

    private readonly object _sync = new();

    private readonly Dictionary<string, MlflowTrace> _traces = new(StringComparer.Ordinal); // run_id -> trace

    private readonly Dictionary<string, MlflowSpan> _runSpans = new(StringComparer.Ordinal); // run_id -> span

    private readonly Dictionary<string, MlflowSpan> _taskSpans = new(StringComparer.Ordinal); // eval_id -> span

    private readonly Dictionary<string, MlflowSpan> _sampleSpans = new(StringComparer.Ordinal); // sample_id -> span

    private readonly Dictionary<string, MlflowSpan> _inspectSpans = new(StringComparer.Ordinal); // inspect span_id -> span

    private MlflowClient? _client;

    private string? _experimentId;

    /// <summary>The CLI's constructor: settings from the environment, a real HTTP client.</summary>
    public MlflowTracingHooks() : this(MlflowSettings.FromEnvironment)
    {
    }

    /// <summary>Explicit settings (enabled when <see cref="MlflowSettings.Tracing"/> is true), optionally over a fake server's handler.</summary>
    public MlflowTracingHooks(MlflowSettings settings, HttpMessageHandler? handler = null) : this(() => settings, handler)
    {
        ArgumentNullException.ThrowIfNull(settings);
    }

    public MlflowTracingHooks(Func<MlflowSettings?> settings, HttpMessageHandler? handler = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _handler = handler;
    }

    /// <summary>Port of <c>enabled()</c>: <c>MLFLOW_TRACKING_URI</c> set and <c>MLFLOW_INSPECT_TRACING</c> true.</summary>
    public override bool Enabled => _settings() is { Tracing: true };

    /// <summary>The traces logged so far (one per completed run), newest last.</summary>
    public IReadOnlyList<MlflowTraceInfo> LoggedTraces { get; private set; } = [];

    /// <summary>The trace of the run in progress (for tests), if any.</summary>
    public MlflowTrace? CurrentTrace
    {
        get
        {
            lock (_sync)
            {
                return _traces.Values.FirstOrDefault();
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
        try
        {
            var trace = new MlflowTrace(experimentId, $"eval_run:{Prefix(data.RunId, 8)}");
            var span = trace.StartSpan(
                name: $"eval_run:{Prefix(data.RunId, 8)}",
                spanType: "CHAIN",
                inputs: new JsonObject { ["task_names"] = new JsonArray(data.TaskNames.Select(name => (JsonNode?)name).ToArray()) },
                attributes: new JsonObject
                {
                    ["inspect.run_id"] = data.RunId,
                    ["inspect.task_count"] = data.TaskNames.Count,
                });
            lock (_sync)
            {
                _client = client;
                _experimentId = experimentId;
                _traces[data.RunId] = trace;
                _runSpans[data.RunId] = span;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ProviderLogger.Info($"Failed to start run span: {ex.Message}");
        }
    }

    public override async Task OnRunEndAsync(RunEnd data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        MlflowSpan? span;
        MlflowTrace? trace;
        MlflowClient? client;
        lock (_sync)
        {
            _runSpans.Remove(data.RunId, out span);
            _traces.Remove(data.RunId, out trace);
            client = _client;
            _taskSpans.Clear();
            _sampleSpans.Clear();
            _inspectSpans.Clear();
            if (_traces.Count == 0)
            {
                _client = null;
                _experimentId = null;
            }
        }

        if (span is null || trace is null)
        {
            return;
        }

        try
        {
            var status = data.Exception is null ? "OK" : "ERROR";
            var outputs = new JsonObject { ["status"] = status };
            if (data.Exception is { } exception)
            {
                span.RecordException(exception.ToString());
                outputs["error"] = exception.Message;
            }

            span.End(outputs, status);
            trace.End(status);
            if (client is not null)
            {
                var logged = await client.LogTraceAsync(trace, cancellationToken).ConfigureAwait(false);
                LoggedTraces = [.. LoggedTraces, logged];
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ProviderLogger.Info($"Failed to end run span: {ex.Message}");
        }
        finally
        {
            if (client is not null)
            {
                lock (_sync)
                {
                    if (_client is null)
                    {
                        client.Dispose();
                    }
                }
            }
        }
    }

    public override Task OnTaskStartAsync(TaskStart data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_sync)
        {
            if (!_runSpans.TryGetValue(data.RunId, out var parent) || !_traces.TryGetValue(data.RunId, out var trace))
            {
                return Task.CompletedTask;
            }

            try
            {
                var span = trace.StartSpan(
                    name: $"task:{data.Spec.Task}",
                    spanType: "CHAIN",
                    parent: parent,
                    inputs: new JsonObject
                    {
                        ["task"] = data.Spec.Task,
                        ["model"] = data.Spec.Model,
                        ["dataset"] = data.Spec.Dataset.Name ?? "",
                    },
                    attributes: new JsonObject
                    {
                        ["inspect.eval_id"] = data.EvalId,
                        ["inspect.task"] = data.Spec.Task,
                        ["inspect.model"] = data.Spec.Model,
                    });
                _taskSpans[data.EvalId] = span;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ProviderLogger.Info($"Failed to start task span: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }

    public override Task OnTaskEndAsync(TaskEnd data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        MlflowSpan? span;
        lock (_sync)
        {
            _taskSpans.Remove(data.EvalId, out span);
        }

        if (span is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            var log = data.Log;
            var outputs = new JsonObject { ["status"] = log.Status.ToString().ToLowerInvariant() };
            if (log.Results is { Scores.Count: > 0 } results)
            {
                var scores = new JsonObject();
                foreach (var evalScore in results.Scores)
                {
                    foreach (var (metricName, metric) in evalScore.Metrics)
                    {
                        scores[$"{evalScore.Name}/{metricName}"] = metric.Value;
                    }
                }

                outputs["scores"] = scores;
            }

            if (log.Results is { } stats)
            {
                outputs["total_samples"] = stats.TotalSamples;
                outputs["completed_samples"] = stats.CompletedSamples;
            }

            span.End(outputs, log.Status == EvalStatus.Success ? "OK" : "ERROR");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ProviderLogger.Info($"Failed to end task span: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public override Task OnSampleStartAsync(SampleStart data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_sync)
        {
            if (!_taskSpans.TryGetValue(data.EvalId, out var parent))
            {
                return Task.CompletedTask;
            }

            try
            {
                var span = parent.Trace.StartSpan(
                    name: $"sample:{Prefix(data.SampleId, 8)}",
                    spanType: "CHAIN",
                    parent: parent,
                    inputs: new JsonObject { ["sample_id"] = data.SampleId },
                    attributes: new JsonObject
                    {
                        ["inspect.sample_id"] = data.SampleId,
                        ["inspect.eval_id"] = data.EvalId,
                    });
                _sampleSpans[data.SampleId] = span;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ProviderLogger.Info($"Failed to start sample span: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }

    public override Task OnSampleEndAsync(SampleEnd data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        MlflowSpan? span;
        lock (_sync)
        {
            _sampleSpans.Remove(data.SampleId, out span);
        }

        if (span is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            var sample = data.Sample;
            var outputs = new JsonObject();
            if (sample.Scores is { Count: > 0 } scores)
            {
                var values = new JsonObject();
                foreach (var (name, score) in scores)
                {
                    values[name] = score.Value.ToJson();
                }

                outputs["scores"] = values;
            }

            if (sample.TotalTime is { } totalTime)
            {
                outputs["total_time"] = totalTime;
            }

            if (sample.Output.Choices.Count > 0)
            {
                outputs["output"] = Truncate(sample.Output.Choices[0].Message.Text, 500);
            }

            var hasError = sample.Error is not null;
            if (hasError)
            {
                span.RecordException(sample.Error!.Message);
            }

            span.End(outputs, hasError ? "ERROR" : "OK");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ProviderLogger.Info($"Failed to end sample span: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public override Task OnSampleEventAsync(SampleEvent data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_sync)
        {
            if (!_sampleSpans.TryGetValue(data.SampleId, out var sampleSpan))
            {
                return Task.CompletedTask;
            }

            try
            {
                switch (data.Event)
                {
                    case SpanBeginEvent begin:
                        HandleSpanBegin(begin, sampleSpan);
                        break;
                    case SpanEndEvent end:
                        HandleSpanEnd(end);
                        break;
                    case ModelEvent model:
                        HandleModelEvent(model, sampleSpan);
                        break;
                    case ToolEvent tool:
                        HandleToolEvent(tool, sampleSpan);
                        break;
                    case ScoreEvent score:
                        HandleScoreEvent(score, sampleSpan);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ProviderLogger.Info($"Failed to handle sample event: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Port of <c>_truncate(text, max_len=200)</c>.</summary>
    public static string Truncate(string? text, int maxLength = 200)
    {
        var s = text ?? "";
        return s.Length > maxLength ? s[..(maxLength - 3)] + "..." : s;
    }

    private void HandleSpanBegin(SpanBeginEvent begin, MlflowSpan sampleSpan)
    {
        var parent = sampleSpan;
        if (begin.ParentId is { } parentId && _inspectSpans.TryGetValue(parentId, out var known))
        {
            parent = known;
        }

        var span = parent.Trace.StartSpan(
            name: begin.Name,
            spanType: string.IsNullOrEmpty(begin.Type) ? "UNKNOWN" : begin.Type,
            parent: parent,
            attributes: new JsonObject { ["inspect.span_id"] = begin.Id });
        _inspectSpans[begin.Id] = span;
    }

    private void HandleSpanEnd(SpanEndEvent end)
    {
        if (_inspectSpans.Remove(end.Id, out var span))
        {
            span.End(status: "OK");
        }
    }

    private void HandleModelEvent(ModelEvent model, MlflowSpan sampleSpan)
    {
        var parent = ParentFor(model, sampleSpan);
        var attrs = new JsonObject { ["inspect.model"] = model.Model };
        var inputs = new JsonObject { ["model"] = model.Model };
        var outputs = new JsonObject();

        if (model.Config.Temperature is { } temperature)
        {
            attrs["temperature"] = temperature;
        }

        if (model.Config.MaxTokens is { } maxTokens)
        {
            attrs["max_tokens"] = maxTokens;
        }

        if (model.Output.Usage is { } usage)
        {
            attrs["input_tokens"] = usage.InputTokens;
            attrs["output_tokens"] = usage.OutputTokens;
            attrs["total_tokens"] = usage.TotalTokens;
            outputs["tokens"] = new JsonObject
            {
                ["input"] = usage.InputTokens,
                ["output"] = usage.OutputTokens,
                ["total"] = usage.TotalTokens,
            };
        }

        if (model.WorkingTime is { } workingTime)
        {
            attrs["working_time"] = workingTime;
        }

        if (model.Cache is { } cache)
        {
            attrs["cache"] = cache.ToString().ToLowerInvariant();
        }

        if (model.Input.Count > 0)
        {
            inputs["messages"] = model.Input.Count;
        }

        if (model.Output.Choices.Count > 0)
        {
            outputs["response"] = Truncate(model.Output.Choices[0].Message.Text, 500);
        }

        var span = parent.Trace.StartSpan($"model:{model.Model}", "LLM", parent, inputs, attrs);
        var status = model.Error is null ? "OK" : "ERROR";
        if (model.Error is { } error)
        {
            span.RecordException(error);
        }

        span.End(outputs, status);
    }

    private void HandleToolEvent(ToolEvent tool, MlflowSpan sampleSpan)
    {
        var parent = ParentFor(tool, sampleSpan);
        var inputs = new JsonObject
        {
            ["function"] = tool.Function,
            ["arguments"] = tool.Arguments.DeepClone(),
        };
        var outputs = new JsonObject();
        var attrs = new JsonObject { ["inspect.tool_id"] = tool.Id };

        if (tool.Result is { } result)
        {
            outputs["result"] = Truncate(result, 500);
        }

        if (tool.Working is { } working)
        {
            attrs["working_time"] = working.TotalSeconds;
        }

        var span = parent.Trace.StartSpan($"tool:{tool.Function}", "TOOL", parent, inputs, attrs);
        var hasError = tool.Error is not null || tool.Failed == true;
        if (tool.Error is { } error)
        {
            span.RecordException(error.Message);
        }

        span.End(outputs, hasError ? "ERROR" : "OK");
    }

    private void HandleScoreEvent(ScoreEvent score, MlflowSpan sampleSpan)
    {
        var parent = ParentFor(score, sampleSpan);
        var inputs = new JsonObject();
        if (score.Target is { } target && target.Text.Length > 0)
        {
            inputs["target"] = Truncate(target.Text, 200);
        }

        var outputs = new JsonObject { ["value"] = score.Score.Value.ToJson() };
        if (!string.IsNullOrEmpty(score.Score.Explanation))
        {
            outputs["explanation"] = Truncate(score.Score.Explanation, 500);
        }

        var attrs = new JsonObject { ["intermediate"] = score.Intermediate };
        var span = parent.Trace.StartSpan("score", "EVALUATOR", parent, inputs, attrs);
        span.End(outputs, "OK");
    }

    private MlflowSpan ParentFor(TranscriptEvent evt, MlflowSpan sampleSpan) =>
        evt.SpanId is { } spanId && _inspectSpans.TryGetValue(spanId, out var known) ? known : sampleSpan;

    private static string Prefix(string text, int length) => text.Length > length ? text[..length] : text;
}

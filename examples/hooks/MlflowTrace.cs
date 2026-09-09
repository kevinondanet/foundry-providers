using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace InspectAzureAI.Examples.Hooks;

/// <summary>
/// The stand-in for <c>mlflow.start_span_no_context(...)</c>'s <c>LiveSpan</c>: a span of an <see cref="MlflowTrace"/>
/// with the inputs, outputs, attributes, status and recorded exceptions the tracing hook sets, serialised in the shape
/// of MLflow's span JSON (<c>Span.to_dict()</c>: OTel-style ids, nanosecond times, JSON-encoded attribute values with
/// the <c>mlflow.spanType</c>/<c>mlflow.spanInputs</c>/<c>mlflow.spanOutputs</c>/<c>mlflow.traceRequestId</c> keys).
/// </summary>
public sealed class MlflowSpan
{
    private readonly List<(long Timestamp, string Message)> _exceptions = [];

    internal MlflowSpan(MlflowTrace trace, string? parentSpanId, string name, string spanType, JsonObject? inputs, JsonObject? attributes)
    {
        Trace = trace;
        SpanId = MlflowTrace.NewSpanId();
        ParentSpanId = parentSpanId;
        Name = name;
        SpanType = spanType;
        StartTimeUnixNano = MlflowTrace.NowUnixNano();
        Inputs = inputs ?? [];
        Attributes = attributes ?? [];
    }

    public MlflowTrace Trace { get; }

    public string SpanId { get; }

    public string? ParentSpanId { get; }

    public string Name { get; }

    public string SpanType { get; }

    public long StartTimeUnixNano { get; }

    public long? EndTimeUnixNano { get; private set; }

    /// <summary><c>OK</c>, <c>ERROR</c> or <c>UNSET</c> (while open).</summary>
    public string Status { get; private set; } = "UNSET";

    public JsonObject Inputs { get; }

    public JsonObject Outputs { get; private set; } = [];

    public JsonObject Attributes { get; }

    public IReadOnlyList<string> Exceptions => _exceptions.Select(e => e.Message).ToList();

    public bool Ended => EndTimeUnixNano is not null;

    /// <summary>Port of <c>span.record_exception(...)</c>: an <c>exception</c> event on the span.</summary>
    public void RecordException(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _exceptions.Add((MlflowTrace.NowUnixNano(), message));
    }

    /// <summary>Port of <c>span.end(outputs=..., status=...)</c>; ending twice is a no-op, as MLflow warns and ignores.</summary>
    public void End(JsonObject? outputs = null, string status = "OK")
    {
        ArgumentNullException.ThrowIfNull(status);
        if (Ended)
        {
            return;
        }

        if (outputs is not null)
        {
            Outputs = outputs;
        }

        Status = status;
        EndTimeUnixNano = MlflowTrace.NowUnixNano();
    }

    /// <summary>The span in MLflow's <c>traces.json</c> shape.</summary>
    public JsonObject ToJson()
    {
        var attributes = new JsonObject
        {
            ["mlflow.traceRequestId"] = Encode(Trace.TraceId),
            ["mlflow.spanType"] = Encode(SpanType),
            ["mlflow.spanInputs"] = Inputs.ToJsonString(),
            ["mlflow.spanOutputs"] = Outputs.ToJsonString(),
        };
        foreach (var (key, value) in Attributes)
        {
            attributes[key] = value is null ? "null" : value.ToJsonString();
        }

        var events = new JsonArray();
        foreach (var (timestamp, message) in _exceptions)
        {
            events.Add(new JsonObject
            {
                ["name"] = "exception",
                ["timestamp"] = timestamp,
                ["attributes"] = new JsonObject { ["exception.message"] = message, ["exception.type"] = "Exception" },
            });
        }

        return new JsonObject
        {
            ["trace_id"] = MlflowTrace.EncodeId(Trace.TraceId),
            ["span_id"] = MlflowTrace.EncodeId(SpanId),
            ["parent_span_id"] = ParentSpanId is null ? null : MlflowTrace.EncodeId(ParentSpanId),
            ["name"] = Name,
            ["start_time_unix_nano"] = StartTimeUnixNano,
            ["end_time_unix_nano"] = EndTimeUnixNano,
            ["status"] = new JsonObject
            {
                ["code"] = Status switch { "OK" => "STATUS_CODE_OK", "ERROR" => "STATUS_CODE_ERROR", _ => "STATUS_CODE_UNSET" },
                ["message"] = "",
            },
            ["attributes"] = attributes,
            ["events"] = events,
        };
    }

    private static string Encode(string value) => JsonValue.Create(value)!.ToJsonString();
}

/// <summary>
/// One MLflow trace: the tree of spans the tracing hook builds for a run, logged to the server when the run ends (as
/// the Python client's exporter logs a trace when its root span ends). Ids follow MLflow: a <c>tr-</c> + 32-hex trace
/// id and 16-hex span ids, base64-encoded in the span JSON.
/// </summary>
public sealed class MlflowTrace(string experimentId, string name)
{
    private readonly List<MlflowSpan> _spans = [];

    public string TraceId { get; } = "tr-" + NewHex(16);

    public string ExperimentId { get; } = experimentId ?? throw new ArgumentNullException(nameof(experimentId));

    /// <summary>The <c>mlflow.traceName</c> tag (the root span's name).</summary>
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    public DateTimeOffset RequestTime { get; } = DateTimeOffset.UtcNow;

    /// <summary><c>IN_PROGRESS</c> until <see cref="End"/>, then <c>OK</c> or <c>ERROR</c>.</summary>
    public string State { get; private set; } = "IN_PROGRESS";

    public TimeSpan? ExecutionDuration { get; private set; }

    public IReadOnlyList<MlflowSpan> Spans => _spans;

    public MlflowSpan? RootSpan => _spans.FirstOrDefault(span => span.ParentSpanId is null);

    /// <summary>Port of <c>mlflow.start_span_no_context(name, span_type, parent_span, inputs, attributes)</c>.</summary>
    public MlflowSpan StartSpan(string name, string spanType, MlflowSpan? parent = null, JsonObject? inputs = null, JsonObject? attributes = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(spanType);
        var span = new MlflowSpan(this, parent?.SpanId, name, spanType, inputs, attributes);
        lock (_spans)
        {
            _spans.Add(span);
        }

        return span;
    }

    /// <summary>Marks the trace complete with <paramref name="state"/> (<c>OK</c> or <c>ERROR</c>).</summary>
    public void End(string state)
    {
        ArgumentNullException.ThrowIfNull(state);
        State = state;
        ExecutionDuration = DateTimeOffset.UtcNow - RequestTime;
    }

    /// <summary>The <c>trace_info</c> of <c>POST api/3.0/mlflow/traces</c> (MLflow's <c>TraceInfo</c> v3).</summary>
    public JsonObject TraceInfoJson()
    {
        var root = RootSpan;
        var metadata = new JsonObject();
        if (root is not null)
        {
            metadata["mlflow.traceInputs"] = root.Inputs.ToJsonString();
            metadata["mlflow.traceOutputs"] = root.Outputs.ToJsonString();
        }

        var info = new JsonObject
        {
            ["trace_id"] = TraceId,
            ["trace_location"] = new JsonObject
            {
                ["type"] = "MLFLOW_EXPERIMENT",
                ["mlflow_experiment"] = new JsonObject { ["experiment_id"] = ExperimentId },
            },
            ["request_time"] = RequestTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["state"] = State,
            ["trace_metadata"] = metadata,
            ["tags"] = new JsonObject { ["mlflow.traceName"] = Name },
        };
        if (ExecutionDuration is { } duration)
        {
            info["execution_duration"] = duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";
        }

        return info;
    }

    /// <summary>The <c>traces.json</c> artifact: every ended span (open spans are not exported, as in MLflow).</summary>
    public JsonObject TraceDataJson()
    {
        var spans = new JsonArray();
        lock (_spans)
        {
            foreach (var span in _spans.Where(span => span.Ended))
            {
                spans.Add(span.ToJson());
            }
        }

        return new JsonObject { ["spans"] = spans };
    }

    public static string NewSpanId() => NewHex(8);

    public static long NowUnixNano() => (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).Ticks * 100;

    /// <summary>MLflow's span JSON carries ids as base64 of the raw bytes; a <c>tr-</c> prefix is dropped first.</summary>
    public static string EncodeId(string hexId)
    {
        ArgumentNullException.ThrowIfNull(hexId);
        var hex = hexId.StartsWith("tr-", StringComparison.Ordinal) ? hexId[3..] : hexId;
        return Convert.ToBase64String(Convert.FromHexString(hex));
    }

    private static string NewHex(int bytes) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));
}

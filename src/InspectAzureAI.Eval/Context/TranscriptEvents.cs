using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Context;

/// <summary>
/// Port of <c>event/_base.py</c> <c>BaseEvent</c>. Every event exposes its Python discriminator through
/// <see cref="Event"/> so plain System.Text.Json serialization carries the type without polymorphism attributes.
/// </summary>
public abstract record TranscriptEvent
{
    public abstract string Event { get; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Id of the enclosing <see cref="Transcript.Span"/>, stamped by <see cref="Transcript.Add"/>.</summary>
    public string? SpanId { get; init; }

    /// <summary>Port of <c>BaseEvent.uuid</c>: generated on construction (Python's <c>model_post_init</c>); null only for events read from a log that lacks it.</summary>
    public string? Uuid { get; init; } = ShortUuid.Generate();

    /// <summary>Port of <c>BaseEvent.working_start</c>: sample working time (seconds) at which the event occurred, stamped by <see cref="Transcript.Add"/> when zero.</summary>
    public double WorkingStart { get; init; }

    /// <summary>Port of <c>BaseEvent.metadata</c>.</summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    /// <summary>Port of <c>BaseEvent.pending</c>: set while a model or tool call is in flight.</summary>
    public bool? Pending { get; init; }
}

/// <summary>Port of <c>event/_tool.py</c> <c>ToolEvent</c>.</summary>
public sealed record ToolEvent(
    string Id,
    string Function,
    JsonObject Arguments,
    string? Result,
    ToolCallError? Error = null,
    ToolTruncation? Truncated = null,
    TimeSpan? Working = null) : TranscriptEvent
{
    public override string Event => "tool";

    /// <summary>Port of <c>ToolEvent.type</c> (currently only "function").</summary>
    public string Type { get; init; } = "function";

    public ToolCallContent? View { get; init; }

    /// <summary>A content-list result (Python's <c>list[Content]</c> form of <c>ToolResult</c>); <see cref="Result"/> then holds its text or is null.</summary>
    public IReadOnlyList<Content>? ResultContent { get; init; }

    /// <summary>Deprecated nested events (always empty in current logs).</summary>
    public IReadOnlyList<TranscriptEvent> Events { get; init; } = [];

    public DateTimeOffset? Completed { get; init; }

    /// <summary>Port of <c>ToolEvent.working_time</c>: <see cref="Working"/> in seconds.</summary>
    public double? WorkingTime => Working?.TotalSeconds;

    /// <summary>Name of the agent if the tool call was an agent handoff.</summary>
    public string? Agent { get; init; }

    public string? AgentSpanId { get; init; }

    /// <summary>Did the tool call fail with a hard error?</summary>
    public bool? Failed { get; init; }

    /// <summary>Id of the <see cref="ChatMessageTool"/> associated with this event.</summary>
    public string? MessageId { get; init; }
}

/// <summary>Port of the <c>ToolEvent.truncated</c> tuple: raw output bytes and the byte limit applied.</summary>
public sealed record ToolTruncation(int Raw, int Shown);

/// <summary>
/// Port of <c>event/_sandbox.py</c> <c>SandboxEvent</c>; <see cref="Action"/> is "exec", "write_file" or "read_file".
/// <see cref="Input"/> and <see cref="Output"/> are truncated to 100 lines by Python's producers.
/// </summary>
public sealed record SandboxEvent(
    string Action,
    string? Cmd = null,
    JsonObject? Options = null,
    string? File = null,
    string? Input = null,
    int? Result = null,
    string? Output = null,
    DateTimeOffset? Completed = null) : TranscriptEvent
{
    /// <summary>
    /// The pre-port shape (raw input and result objects): <c>cmd</c> (string or argv), <c>file</c>, <c>input</c> /
    /// <c>contents</c> and the remaining keys as <see cref="Options"/>; <c>returncode</c> and <c>stdout</c> /
    /// <c>stderr</c> from the result.
    /// </summary>
    public SandboxEvent(string action, JsonObject input, JsonObject? result = null)
        : this(
            action,
            Cmd: CommandText(input["cmd"]),
            Options: RemainingOptions(input),
            File: input["file"]?.GetValue<string>(),
            Input: (input["input"] ?? input["contents"])?.GetValue<string>(),
            Result: ReturnCode(result),
            Output: OutputText(result))
    {
    }

    public override string Event => "sandbox";

    private static string? CommandText(JsonNode? cmd) => cmd switch
    {
        null => null,
        JsonArray argv => string.Join(" ", argv.Select(item => item?.ToString() ?? "")),
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => cmd.ToJsonString(),
    };

    private static JsonObject? RemainingOptions(JsonObject input)
    {
        var options = new JsonObject();
        foreach (var pair in input)
        {
            if (pair.Key is not ("cmd" or "file" or "input" or "contents"))
            {
                options[pair.Key] = pair.Value?.DeepClone();
            }
        }

        return options.Count > 0 ? options : null;
    }

    private static int? ReturnCode(JsonObject? result) =>
        (result?["returncode"] ?? result?["return_code"] ?? result?["exit_code"]) is JsonValue value && value.TryGetValue<int>(out var code) ? code : null;

    private static string? OutputText(JsonObject? result)
    {
        var stdout = result?["stdout"]?.GetValue<string>();
        var stderr = result?["stderr"]?.GetValue<string>();
        return string.IsNullOrEmpty(stderr) ? stdout : string.IsNullOrEmpty(stdout) ? stderr : stdout + stderr;
    }
}

/// <summary>Port of <c>event/_score.py</c> <c>ScoreEvent</c>.</summary>
public sealed record ScoreEvent(Score Score, Target? Target = null, bool Intermediate = false) : TranscriptEvent
{
    public override string Event => "score";

    /// <summary>Name of the scorer that produced this score (unique within the task).</summary>
    public string? Scorer { get; init; }

    /// <summary>Arguments the scorer was instantiated with (null for scores set directly by a solver).</summary>
    public IReadOnlyDictionary<string, object?>? ScorerArgs { get; init; }

    /// <summary>Cumulative model usage at the time of this score.</summary>
    public IReadOnlyDictionary<string, ModelUsage>? ModelUsage { get; init; }

    /// <summary>Cumulative model usage by role at the time of this score.</summary>
    public IReadOnlyDictionary<string, ModelUsage>? RoleUsage { get; init; }
}

/// <summary>Port of <c>event/_info.py</c> <c>InfoEvent</c>.</summary>
public sealed record InfoEvent(string? Source, JsonNode? Data) : TranscriptEvent
{
    public override string Event => "info";
}

/// <summary>Port of <c>event/_error.py</c> <c>ErrorEvent</c>.</summary>
public sealed record ErrorEvent(string Message, string? Traceback = null) : TranscriptEvent
{
    /// <summary>Builds the event from the sample's <see cref="EvalError"/>.</summary>
    public ErrorEvent(EvalError error)
        : this(error.Message, string.IsNullOrEmpty(error.Traceback) ? null : error.Traceback)
    {
        TracebackAnsi = string.IsNullOrEmpty(error.TracebackAnsi) ? null : error.TracebackAnsi;
    }

    public override string Event => "error";

    public string? TracebackAnsi { get; init; }

    /// <summary>Port of <c>ErrorEvent.error</c>: the nested <see cref="EvalError"/> as written to the log.</summary>
    public EvalError Error => new(Message, Traceback ?? "", TracebackAnsi ?? "");
}

/// <summary>Port of <c>event/_span.py</c> <c>SpanBeginEvent</c>.</summary>
public sealed record SpanBeginEvent(string Id, string Name, string Type, string? ParentId = null) : TranscriptEvent
{
    public override string Event => "span_begin";
}

/// <summary>Port of <c>event/_span.py</c> <c>SpanEndEvent</c>.</summary>
public sealed record SpanEndEvent(string Id) : TranscriptEvent
{
    public override string Event => "span_end";
}

/// <summary>Port of <c>event/_step.py</c> <c>StepEvent</c>; <see cref="Type"/> is "solver" or "scorer", <see cref="Action"/> "begin" or "end".</summary>
public sealed record StepEvent(string Name, string Type, string Action) : TranscriptEvent
{
    public override string Event => "step";
}

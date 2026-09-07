using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Context;

/// <summary>Port of <c>event/_sample_init.py</c> <c>SampleInitEvent</c>: the beginning of processing a sample.</summary>
public sealed record SampleInitEvent(Sample Sample, JsonNode? State = null) : TranscriptEvent
{
    public override string Event => "sample_init";
}

/// <summary>
/// Port of <c>event/_sample_limit.py</c> <c>SampleLimitEvent</c>; <see cref="Type"/> is one of "message", "time",
/// "working", "token", "turn", "cost", "operator", "custom".
/// </summary>
public sealed record SampleLimitEvent(string Type, string Message, double? Limit = null) : TranscriptEvent
{
    public override string Event => "sample_limit";
}

/// <summary>
/// Port of <c>event/_state.py</c> <c>StateEvent</c>: a change to the <c>TaskState</c> as a raw RFC 6902 patch
/// (<c>list[JsonChange]</c>: <c>op</c>, <c>path</c>, <c>from</c>, <c>value</c>, <c>replaced</c>). Patching is not
/// implemented here; <see cref="Changes"/> is the JSON array as written.
/// </summary>
public sealed record StateEvent(JsonElement Changes) : TranscriptEvent
{
    public override string Event => "state";

    /// <summary>Builds the event from a patch array.</summary>
    public static StateEvent FromChanges(JsonArray changes) => new(TranscriptEventJson.ToElement(changes));

    /// <summary>Builds the event from <see cref="JsonChange"/>s (the output of <see cref="JsonChanges.Diff"/>).</summary>
    public static StateEvent FromChanges(IEnumerable<JsonChange> changes) => FromChanges(JsonChanges.ToJson(changes));

    /// <summary>Port of <c>StateEvent.changes</c>: <see cref="Changes"/> parsed as <see cref="JsonChange"/>s.</summary>
    public IReadOnlyList<JsonChange> GetChanges() => JsonChanges.FromJson(Changes);
}

/// <summary>Port of <c>event/_store.py</c> <c>StoreEvent</c>: a change to the <c>Store</c> as a raw RFC 6902 patch (see <see cref="StateEvent"/>).</summary>
public sealed record StoreEvent(JsonElement Changes) : TranscriptEvent
{
    public override string Event => "store";

    /// <summary>Builds the event from a patch array.</summary>
    public static StoreEvent FromChanges(JsonArray changes) => new(TranscriptEventJson.ToElement(changes));

    /// <summary>Builds the event from <see cref="JsonChange"/>s (the output of <see cref="JsonChanges.Diff"/>).</summary>
    public static StoreEvent FromChanges(IEnumerable<JsonChange> changes) => FromChanges(JsonChanges.ToJson(changes));

    /// <summary>Port of <c>StoreEvent.changes</c>: <see cref="Changes"/> parsed as <see cref="JsonChange"/>s.</summary>
    public IReadOnlyList<JsonChange> GetChanges() => JsonChanges.FromJson(Changes);
}

/// <summary>Port of <c>tool/_tool_call.py</c> <c>ToolCallContent</c>: a custom rendering of a tool call (<see cref="Format"/> is "text" or "markdown").</summary>
public sealed record ToolCallContent(string Format, string Content = "")
{
    public string? Title { get; init; }
}

/// <summary>Port of <c>tool/_tool_call.py</c> <c>ToolCallView</c>: the view presented for approval.</summary>
public sealed record ToolCallView
{
    public ToolCallContent? Context { get; init; }

    public ToolCallContent? Call { get; init; }
}

/// <summary>
/// Port of <c>event/_approval.py</c> <c>ApprovalEvent</c>; <see cref="Decision"/> is one of "approve", "modify",
/// "reject", "escalate", "terminate".
/// </summary>
public sealed record ApprovalEvent(string Message, ToolCall Call, string Approver, string Decision) : TranscriptEvent
{
    public override string Event => "approval";

    public ToolCallView? View { get; init; }

    /// <summary>Modified tool call for decision "modify".</summary>
    public ToolCall? Modified { get; init; }

    public string? Explanation { get; init; }
}

/// <summary>Port of <c>event/_subtask.py</c> <c>SubtaskEvent</c>.</summary>
public sealed record SubtaskEvent(string Name, JsonObject Input) : TranscriptEvent
{
    public override string Event => "subtask";

    public string? Type { get; init; }

    public JsonNode? Result { get; init; }

    /// <summary>Deprecated nested events (always empty in current logs; all events live in the main transcript).</summary>
    public IReadOnlyList<TranscriptEvent> Events { get; init; } = [];

    public DateTimeOffset? Completed { get; init; }

    /// <summary>Seconds of working time (time not spent waiting on semaphores or retries).</summary>
    public double? WorkingTime { get; init; }
}

/// <summary>Port of <c>event/_compaction.py</c> <c>CompactionEvent</c>; <see cref="Type"/> is "summary", "edit" or "trim".</summary>
public sealed record CompactionEvent : TranscriptEvent
{
    public override string Event => "compaction";

    public string Type { get; init; } = "summary";

    /// <summary>Model role whose conversation was compacted.</summary>
    public string? Role { get; init; }

    public int? TokensBefore { get; init; }

    public int? TokensAfter { get; init; }

    /// <summary>Compaction source (e.g. "inspect", "claude_code").</summary>
    public string? Source { get; init; }
}

/// <summary>
/// Port of <c>event/_logger.py</c> <c>LoggingMessage</c>; <see cref="Level"/> is one of "debug", "trace", "http",
/// "sandbox", "info", "warning", "error", "critical". <see cref="Created"/> is milliseconds since the epoch.
/// </summary>
public sealed record LoggingMessage(string Level, string Message, double Created)
{
    /// <summary>Logger name (e.g. "httpx"); Python drops names that are not a single identifier.</summary>
    public string? Name { get; init; }

    public string Filename { get; init; } = "unknown";

    public string Module { get; init; } = "unknown";

    public int Lineno { get; init; }
}

/// <summary>Port of <c>event/_logger.py</c> <c>LoggerEvent</c>: a message recorded with the Python logger.</summary>
public sealed record LoggerEvent(LoggingMessage Message) : TranscriptEvent
{
    public override string Event => "logger";
}

/// <summary>Port of <c>event/_input.py</c> <c>InputField</c>; <see cref="Type"/> is a JSON Schema type name.</summary>
public sealed record InputField(string Name, string Type)
{
    public string? Description { get; init; }
}

/// <summary>Port of <c>event/_input.py</c> <c>InputEvent</c>: an input screen interaction.</summary>
public sealed record InputEvent(string Input, string InputAnsi) : TranscriptEvent
{
    public override string Event => "input";

    /// <summary>Prompt shown to the user.</summary>
    public string? Message { get; init; }

    public IReadOnlyList<InputField>? Fields { get; init; }

    /// <summary>"accepted", "declined" or "cancelled".</summary>
    public string? Outcome { get; init; }

    /// <summary>Structured answer when <see cref="Outcome"/> is "accepted".</summary>
    public IReadOnlyDictionary<string, object?>? Content { get; init; }
}

/// <summary>Port of <c>event/_anchor.py</c> <c>AnchorEvent</c>: a rollback-able point in a replayable trajectory.</summary>
public sealed record AnchorEvent(string AnchorId) : TranscriptEvent
{
    public override string Event => "anchor";

    /// <summary>Qualified name of the recorded function that produced the anchored value.</summary>
    public string? Source { get; init; }
}

/// <summary>Port of <c>event/_branch.py</c> <c>BranchEvent</c>: where a branched trajectory's unique content begins.</summary>
public sealed record BranchEvent(string FromAnchor = "") : TranscriptEvent
{
    public override string Event => "branch";
}

/// <summary>
/// Port of <c>event/_interrupt.py</c> <c>InterruptEvent</c>; <see cref="Source"/> is "user_cancel", "limit" or
/// "system" and <see cref="Interrupted"/> is "generate", "tool_call" or "between_turns".
/// </summary>
public sealed record InterruptEvent(string Source, string Interrupted) : TranscriptEvent
{
    public override string Event => "interrupt";

    public string? InterruptedToolCallId { get; init; }

    public string? InterruptedModelEventId { get; init; }
}

/// <summary>
/// One field of a <see cref="ScoreEdit"/>: either unchanged (Python's <c>"UNCHANGED"</c> sentinel) or set to a
/// value, where a null value clears the field.
/// </summary>
public readonly record struct Edited<T>(bool IsSet, T? Value)
{
    /// <summary>The field is left as it was.</summary>
    public static Edited<T> Unchanged => default;

    /// <summary>The field is set to <paramref name="value"/>.</summary>
    public static Edited<T> Set(T? value) => new(true, value);

    public static implicit operator Edited<T>(T? value) => Set(value);
}

/// <summary>Port of <c>scorer/_metric.py</c> <c>ScoreEdit</c>: an edit to a score, recorded in <c>Score.history</c>.</summary>
public sealed record ScoreEdit
{
    public Edited<ScoreValue> Value { get; init; }

    public Edited<string> Answer { get; init; }

    public Edited<string> Explanation { get; init; }

    public Edited<string> Reason { get; init; }

    public Edited<IReadOnlyDictionary<string, object?>> Metadata { get; init; }

    public ProvenanceData? Provenance { get; init; }
}

/// <summary>Port of <c>event/_score_edit.py</c> <c>ScoreEditEvent</c>.</summary>
public sealed record ScoreEditEvent(string ScoreName, ScoreEdit Edit) : TranscriptEvent
{
    public override string Event => "score_edit";
}

/// <summary>Port of <c>util/_checkpoint/_layout/schemas.py</c> <c>SnapshotDetails</c>.</summary>
public sealed record SnapshotDetails(string SnapshotId, long SizeBytes, int DurationMs)
{
    public IReadOnlyList<string>? Files { get; init; }

    /// <summary>Files beyond those listed in <see cref="Files"/>.</summary>
    public int? AdditionalFiles { get; init; }
}

/// <summary>
/// Port of <c>event/_checkpoint.py</c> <c>CheckpointEvent</c>: a successful checkpoint commit with the
/// <c>Checkpoint</c> payload flattened into the event. Keys this port does not model are kept in <see cref="Extra"/>
/// (the Python model allows extra fields) and written back verbatim.
/// </summary>
public sealed record CheckpointEvent(int CheckpointId, string Trigger, int Turn, DateTimeOffset CreatedAt, int DurationMs, long SizeBytes, SnapshotDetails Host) : TranscriptEvent
{
    public override string Event => "checkpoint";

    public JsonObject? TriggerMetadata { get; init; }

    public IReadOnlyDictionary<string, SnapshotDetails> Sandboxes { get; init; } = new Dictionary<string, SnapshotDetails>(StringComparer.Ordinal);

    public JsonObject? Extra { get; init; }
}

/// <summary>Port of <c>ModelEvent.input_refs</c> entries: a <c>(start, end_exclusive)</c> range into the message pool.</summary>
public readonly record struct MessageRange(int Start, int End);

/// <summary>Helpers for the raw-JSON members of events.</summary>
public static class TranscriptEventJson
{
    /// <summary>A detached <see cref="JsonElement"/> for <paramref name="node"/> (valid after the parse document is gone).</summary>
    public static JsonElement ToElement(JsonNode? node)
    {
        using var document = JsonDocument.Parse(node?.ToJsonString() ?? "null");
        return document.RootElement.Clone();
    }
}

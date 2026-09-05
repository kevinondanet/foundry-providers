using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider.Core;

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
}

/// <summary>Port of the <c>ToolEvent.truncated</c> tuple: raw output bytes and the byte limit applied.</summary>
public sealed record ToolTruncation(int Raw, int Shown);

/// <summary>Port of <c>event/_sandbox.py</c> <c>SandboxEvent</c>; <see cref="Action"/> is "exec", "write_file" or "read_file".</summary>
public sealed record SandboxEvent(string Action, JsonObject Input, JsonObject? Result = null) : TranscriptEvent
{
    public override string Event => "sandbox";
}

/// <summary>Port of <c>event/_score.py</c> <c>ScoreEvent</c>.</summary>
public sealed record ScoreEvent(Score Score, Target? Target = null, bool Intermediate = false) : TranscriptEvent
{
    public override string Event => "score";
}

/// <summary>Port of <c>event/_info.py</c> <c>InfoEvent</c>.</summary>
public sealed record InfoEvent(string? Source, JsonNode? Data) : TranscriptEvent
{
    public override string Event => "info";
}

/// <summary>Port of <c>event/_error.py</c> <c>ErrorEvent</c>.</summary>
public sealed record ErrorEvent(string Message, string? Traceback = null) : TranscriptEvent
{
    public override string Event => "error";
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

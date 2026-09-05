using InspectAzureAI.Eval.Context;

namespace InspectAzureAI.Eval.Model.Compaction;

/// <summary>
/// Port of <c>event/_compaction.py</c> <c>CompactionEvent</c>: compaction of conversation history. Serialized with
/// the Python field names (<c>type</c>, <c>role</c>, <c>tokens_before</c>, <c>tokens_after</c>, <c>source</c>,
/// <c>metadata</c>) under <c>event: "compaction"</c>.
/// </summary>
public sealed record CompactionEvent : TranscriptEvent
{
    public override string Event => "compaction";

    /// <summary>Compaction type: <c>summary</c>, <c>edit</c> or <c>trim</c>.</summary>
    public string Type { get; init; } = "summary";

    /// <summary>Model role whose conversation was compacted (the .NET <see cref="Model"/> carries no role, so the orchestrator leaves this null).</summary>
    public string? Role { get; init; }

    /// <summary>Tokens before compaction.</summary>
    public int? TokensBefore { get; init; }

    /// <summary>Tokens after compaction.</summary>
    public int? TokensAfter { get; init; }

    /// <summary>Compaction source (e.g. <c>inspect</c>, <c>claude_code</c>).</summary>
    public string? Source { get; init; }

    /// <summary>
    /// Additional event metadata (<c>BaseEvent.metadata</c>); the orchestrator records <c>strategy</c>,
    /// <c>messages_before</c>, <c>messages_after</c> and <c>trigger</c> (<c>threshold</c> or <c>forced</c>).
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

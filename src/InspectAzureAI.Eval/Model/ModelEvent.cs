using InspectAzureAI.Eval.Context;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Model;

/// <summary>Port of <c>event/_model.py</c> <c>ModelEvent</c>: one provider attempt (input, output or error, raw call, timing).</summary>
public sealed record ModelEvent : TranscriptEvent
{
    public override string Event => "model";

    public required string Model { get; init; }

    /// <summary>Port of <c>ModelEvent.role</c>: the model role, if the call went through one.</summary>
    public string? Role { get; init; }

    public required IReadOnlyList<ChatMessage> Input { get; init; }

    /// <summary>Port of <c>ModelEvent.input_refs</c>: message-pool ranges for <see cref="Input"/> in condensed logs (kept verbatim, not resolved).</summary>
    public IReadOnlyList<MessageRange>? InputRefs { get; init; }

    public IReadOnlyList<ToolInfo> Tools { get; init; } = [];

    public required ToolChoice ToolChoice { get; init; }

    public required GenerateConfig Config { get; init; }

    /// <summary>The output, or an empty <see cref="ModelOutput"/> when the attempt failed.</summary>
    public required ModelOutput Output { get; init; }

    public ModelCall? Call { get; init; }

    /// <summary>Retries that preceded this attempt (0 for the first).</summary>
    public int? Retries { get; init; }

    public string? Error { get; init; }

    /// <summary>Error traceback (plain text).</summary>
    public string? Traceback { get; init; }

    /// <summary>Error traceback with ANSI color codes.</summary>
    public string? TracebackAnsi { get; init; }

    /// <summary>Port of <c>ModelEvent.cache</c>: "read" or "write" when the call hit the cache.</summary>
    public string? Cache { get; init; }

    public DateTimeOffset? Completed { get; init; }

    /// <summary>Seconds spent in the provider call.</summary>
    public double? WorkingTime { get; init; }
}

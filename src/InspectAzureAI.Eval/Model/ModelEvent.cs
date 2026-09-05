using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Model;

/// <summary>Port of <c>event/_model.py</c> <c>ModelEvent</c>: one provider attempt (input, output or error, raw call, timing).</summary>
public sealed record ModelEvent : TranscriptEvent
{
    public override string Event => "model";

    public required string Model { get; init; }

    public required IReadOnlyList<ChatMessage> Input { get; init; }

    public IReadOnlyList<ToolInfo> Tools { get; init; } = [];

    public required ToolChoice ToolChoice { get; init; }

    public required GenerateConfig Config { get; init; }

    /// <summary>The output, or an empty <see cref="ModelOutput"/> when the attempt failed.</summary>
    public required ModelOutput Output { get; init; }

    public ModelCall? Call { get; init; }

    /// <summary>Retries that preceded this attempt (0 for the first).</summary>
    public int? Retries { get; init; }

    public string? Error { get; init; }

    /// <summary>Port of <c>cache</c>: <see cref="CacheMode.Read"/> when the output came from the prompt cache, <see cref="CacheMode.Write"/> when the attempt ran under a cache policy, null otherwise.</summary>
    public CacheMode? Cache { get; init; }

    public DateTimeOffset? Completed { get; init; }

    /// <summary>Seconds spent in the provider call.</summary>
    public double? WorkingTime { get; init; }
}

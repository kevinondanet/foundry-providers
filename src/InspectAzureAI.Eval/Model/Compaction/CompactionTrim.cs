using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Model.Compaction;

/// <summary>
/// Port of <c>model/_compaction/trim.py</c> <c>CompactionTrim</c>: message trimming compaction. Compacts messages
/// by trimming the history to preserve a percentage of messages (see <see cref="TrimMessages.Trim"/>): all
/// system messages and the sample's input messages are retained, a proportion of the remaining messages is
/// preserved (<see cref="Preserve"/>, 0.8 by default), every assistant tool call keeps its tool message, and the
/// sequence never ends with an assistant message.
/// </summary>
public sealed class CompactionTrim : CompactionStrategy
{
    /// <param name="threshold">Token count or fraction of the context window that triggers compaction (default 0.9).</param>
    /// <param name="memory">Warn the model to save critical content to memory before compaction when the memory tool is available.</param>
    /// <param name="preserve">Ratio of conversation messages to preserve (defaults to 0.8).</param>
    public CompactionTrim(CompactionThreshold threshold = default, bool memory = true, double preserve = 0.8)
        : base("trim", threshold, memory)
    {
        Preserve = preserve;
    }

    /// <summary>Ratio of conversation messages to preserve.</summary>
    public double Preserve { get; }

    public override IReadOnlyDictionary<string, object?> ReprParams()
    {
        var parameters = new Dictionary<string, object?>(base.ReprParams(), StringComparer.Ordinal) { ["preserve"] = Preserve };
        return parameters;
    }

    /// <summary>Trims the history (and, with <see cref="CompactionStrategy.Memory"/>, clears saved memory content from what survives).</summary>
    public override Task<CompactionResult> CompactAsync(
        Model model,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolInfo> tools,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);
        cancellationToken.ThrowIfCancellationRequested();

        var trimmed = TrimMessages.Trim(messages, Preserve);
        if (Memory)
        {
            trimmed = CompactionMemory.ClearMemoryContent(trimmed);
        }

        return Task.FromResult(new CompactionResult(trimmed, null));
    }
}

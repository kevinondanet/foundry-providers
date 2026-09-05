using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Model.Compaction;

/// <summary>
/// Port of <c>model/_trim.py</c> <c>PartitionedMessages</c>: the system, sample-input and conversation partitions
/// of a message list.
/// </summary>
public sealed record PartitionedMessages
{
    /// <summary>System messages, in order.</summary>
    public List<ChatMessage> System { get; init; } = [];

    /// <summary>
    /// The sample's input messages: those with <c>source == "input"</c>, or (when none are marked) the leading
    /// messages up to and including the first user message.
    /// </summary>
    public List<ChatMessage> Input { get; init; } = [];

    /// <summary>Everything else, including compaction summaries.</summary>
    public List<ChatMessage> Conversation { get; init; } = [];
}

/// <summary>
/// Port of <c>model/_trim.py</c>: <see cref="Trim"/> keeps system and input messages plus a proportion of the
/// conversation, and repairs tool-call pairing so the result is acceptable to every provider.
/// </summary>
public static class TrimMessages
{
    /// <summary>Metadata key that marks a compaction summary message.</summary>
    public const string SummaryMetadataKey = "summary";

    /// <summary>
    /// Port of <c>trim_messages</c>. Trims the list by retaining all system messages, retaining the sample's
    /// input messages, preserving <paramref name="preserve"/> of the remaining conversation (taken from the
    /// end), dropping tool messages whose assistant tool call was trimmed away, dropping assistant tool calls
    /// whose results were trimmed away (some APIs, e.g. Anthropic, require every tool use to have a result),
    /// and never ending on an assistant message.
    /// </summary>
    /// <param name="messages">Messages to trim.</param>
    /// <param name="preserve">Ratio of conversation messages to preserve (defaults to 0.7).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="preserve"/> is outside [0, 1] (Python raises <c>ValueError</c>).</exception>
    public static IReadOnlyList<ChatMessage> Trim(IReadOnlyList<ChatMessage> messages, double preserve = 0.7)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (double.IsNaN(preserve) || preserve < 0 || preserve > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(preserve), preserve, $"preserve must be in range [0,1], got {preserve}");
        }

        var partitioned = Partition(messages);

        // slice messages from the beginning of the conversation as-per preserve
        var startIdx = (int)(partitioned.Conversation.Count * (1 - preserve));
        var preserved = partitioned.Conversation.Skip(startIdx);

        // tool messages need a parent assistant message with a matching tool_call id
        var conversation = new List<ChatMessage>();
        var activeToolIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in preserved)
        {
            switch (message)
            {
                case ChatMessageAssistant assistant:
                    activeToolIds = (assistant.ToolCalls ?? []).Select(tc => tc.Id).ToHashSet(StringComparer.Ordinal);
                    conversation.Add(message);
                    break;
                case ChatMessageTool { ToolCallId: { } toolCallId } when activeToolIds.Contains(toolCallId):
                    conversation.Add(message);
                    break;
                case ChatMessageUser:
                    activeToolIds = new HashSet<string>(StringComparer.Ordinal);
                    conversation.Add(message);
                    break;
            }
        }

        // assistant tool_calls without results are orphans: drop them (a fresh id marks the edited copy)
        var toolIdsWithResults = conversation.OfType<ChatMessageTool>()
            .Select(m => m.ToolCallId)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var sanitized = new List<ChatMessage>(conversation.Count);
        foreach (var message in conversation)
        {
            if (message is ChatMessageAssistant { ToolCalls: { Count: > 0 } toolCalls } assistant)
            {
                var valid = toolCalls.Where(tc => toolIdsWithResults.Contains(tc.Id)).ToList();
                if (valid.Count != toolCalls.Count)
                {
                    sanitized.Add(assistant with { Id = ShortUuid.Generate(), ToolCalls = valid.Count > 0 ? valid : null });
                    continue;
                }
            }

            sanitized.Add(message);
        }

        conversation = sanitized;

        if (conversation.Count > 0 && conversation[^1] is ChatMessageAssistant)
        {
            conversation.RemoveAt(conversation.Count - 1);
        }

        return StripCitations([.. partitioned.System, .. partitioned.Input, .. conversation]);
    }

    /// <summary>
    /// Port of <c>partition_messages</c>: system messages, input messages (<c>source == "input"</c>, otherwise the
    /// leading messages through the first user message) and the conversation. Summary messages always belong to
    /// the conversation.
    /// </summary>
    public static PartitionedMessages Partition(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var partitioned = new PartitionedMessages();
        foreach (var message in messages)
        {
            if (message is ChatMessageSystem)
            {
                partitioned.System.Add(message);
            }
            else if (IsSummaryMessage(message))
            {
                partitioned.Conversation.Add(message);
            }
            else if (message.Source == "input")
            {
                partitioned.Input.Add(message);
            }
            else
            {
                partitioned.Conversation.Add(message);
            }
        }

        if (partitioned.Input.Count == 0)
        {
            while (partitioned.Conversation.Count > 0 && !IsSummaryMessage(partitioned.Conversation[0]))
            {
                var message = partitioned.Conversation[0];
                partitioned.Conversation.RemoveAt(0);
                partitioned.Input.Add(message);
                if (message is ChatMessageUser)
                {
                    break;
                }
            }
        }

        return partitioned;
    }

    /// <summary>Port of <c>_is_summary_message</c>: whether the message metadata carries a <c>summary</c> key.</summary>
    public static bool IsSummaryMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Metadata?.ContainsKey(SummaryMetadataKey) == true;
    }

    /// <summary>
    /// Port of <c>strip_citations</c>. Python strips <c>ContentText.citations</c> because they reference server-side
    /// tool results by index and dangle once compaction removes those results. The .NET <see cref="ContentText"/>
    /// carries no citations (the Azure providers never produce them), so there is nothing to strip and the input
    /// is returned as-is; the method exists so call sites mirror Python and gain the behaviour if citations arrive.
    /// </summary>
    public static IReadOnlyList<ChatMessage> StripCitations(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages;
    }
}

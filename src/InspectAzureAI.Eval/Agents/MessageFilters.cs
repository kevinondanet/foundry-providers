using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Agents;

/// <summary>Port of <c>agent/_filter.py</c> <c>MessageFilter</c>: rewrites the messages sent to or received from an agent handoff.</summary>
public delegate Task<IReadOnlyList<ChatMessage>> MessageFilter(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken);

/// <summary>Port of <c>model/_trim.py</c> <c>PartitionedMessages</c>: system messages, the sample input and the rest of the conversation.</summary>
public sealed record PartitionedMessages(IReadOnlyList<ChatMessage> System, IReadOnlyList<ChatMessage> Input, IReadOnlyList<ChatMessage> Conversation);

/// <summary>
/// Port of the built-in filters of <c>agent/_filter.py</c> (<c>content_only</c>, <c>remove_tools</c>,
/// <c>last_message</c>) and of <c>model/_trim.py</c> <c>trim_messages</c> (the react agent's
/// <c>truncation="auto"</c>).
/// </summary>
public static class MessageFilters
{
    /// <summary>
    /// Port of <c>content_only</c>, the default handoff output filter: drops system messages and reasoning,
    /// turns tool messages into user messages (keeping their ids) and renders assistant tool calls as text so
    /// the parent model is not confounded by tools it does not have.
    /// </summary>
    public static MessageFilter ContentOnly { get; } = (messages, _) => Task.FromResult(ContentOnlyMessages(messages));

    /// <summary>Port of <c>remove_tools</c>: drops tool messages and the <c>tool_calls</c> of assistant messages.</summary>
    public static MessageFilter RemoveTools { get; } = (messages, _) => Task.FromResult(RemoveToolsMessages(messages));

    /// <summary>Port of <c>last_message</c>: keeps only the last message.</summary>
    public static MessageFilter LastMessage { get; } = (messages, _) => Task.FromResult<IReadOnlyList<ChatMessage>>(messages.Count == 0 ? [] : [messages[^1]]);

    /// <summary>Passes the messages through unchanged: the way to ask <c>Handoff</c> for Python's <c>output_filter=None</c>.</summary>
    public static MessageFilter Identity { get; } = (messages, _) => Task.FromResult(messages);

    /// <summary>Port of <c>trim_messages</c> with the default <c>preserve=0.7</c> (the react agent's <c>truncation="auto"</c>).</summary>
    public static MessageFilter TrimMessages { get; } = (messages, _) => Task.FromResult(TrimMessagesTo(messages));

    /// <summary>Synchronous <see cref="ContentOnly"/>.</summary>
    public static IReadOnlyList<ChatMessage> ContentOnlyMessages(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var filtered = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            switch (message)
            {
                case ChatMessageSystem:
                    continue;
                case ChatMessageUser user:
                    filtered.Add(user);
                    break;
                case ChatMessageTool tool:
                    filtered.Add(new ChatMessageUser(tool.Content) { Id = tool.Id, Source = tool.Source, Metadata = tool.Metadata });
                    break;
                case ChatMessageAssistant assistant:
                    var content = assistant.ContentList.ToList();
                    var toolCalls = string.Join("\n", (assistant.ToolCalls ?? []).Select(call => ModelGraded.FormatFunctionCall(call.Function, call.Arguments)));
                    if (toolCalls.Length > 0)
                    {
                        content.Add(new ContentText(toolCalls));
                    }

                    // Python also clears `internal` on each item and renders server-side tool use as text; this
                    // port's Content carries neither, so only the reasoning removal remains.
                    content = content.Where(c => c is not ContentReasoning).ToList();
                    filtered.Add(assistant with { Content = MessageContent.FromItems(content), ToolCalls = null });
                    break;
                default:
                    filtered.Add(message);
                    break;
            }
        }

        return filtered;
    }

    /// <summary>Synchronous <see cref="RemoveTools"/>.</summary>
    public static IReadOnlyList<ChatMessage> RemoveToolsMessages(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var filtered = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            switch (message)
            {
                case ChatMessageTool:
                    continue;
                case ChatMessageAssistant assistant:
                    filtered.Add(assistant with { ToolCalls = null });
                    break;
                default:
                    filtered.Add(message);
                    break;
            }
        }

        return filtered;
    }

    /// <summary>
    /// Port of <c>trim_messages(messages, preserve)</c>: keeps the system messages and the sample input, the
    /// last <paramref name="preserve"/> share of the rest, drops tool messages whose call was trimmed away and
    /// tool calls whose result was, and never ends on an assistant message. Python also strips citations,
    /// which this port's content model does not carry.
    /// </summary>
    public static IReadOnlyList<ChatMessage> TrimMessagesTo(IReadOnlyList<ChatMessage> messages, double preserve = 0.7)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (preserve is < 0 or > 1 || double.IsNaN(preserve))
        {
            throw new ArgumentOutOfRangeException(nameof(preserve), preserve, $"preserve must be in range [0,1], got {preserve}");
        }

        var partitioned = PartitionMessages(messages);
        var startIndex = (int)(partitioned.Conversation.Count * (1 - preserve));
        var preserved = partitioned.Conversation.Skip(startIndex).ToList();

        var conversation = new List<ChatMessage>(preserved.Count);
        var activeToolIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in preserved)
        {
            switch (message)
            {
                case ChatMessageAssistant assistant:
                    activeToolIds = (assistant.ToolCalls ?? []).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
                    conversation.Add(assistant);
                    break;
                case ChatMessageTool tool when tool.ToolCallId is { } id && activeToolIds.Contains(id):
                    conversation.Add(tool);
                    break;
                case ChatMessageUser user:
                    activeToolIds.Clear();
                    conversation.Add(user);
                    break;
            }
        }

        var idsWithResults = conversation.OfType<ChatMessageTool>().Select(t => t.ToolCallId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < conversation.Count; i++)
        {
            if (conversation[i] is ChatMessageAssistant { ToolCalls: { Count: > 0 } calls } assistant)
            {
                var valid = calls.Where(c => idsWithResults.Contains(c.Id)).ToList();
                if (valid.Count != calls.Count)
                {
                    conversation[i] = assistant with { Id = ShortUuid.Generate(), ToolCalls = valid.Count > 0 ? valid : null };
                }
            }
        }

        if (conversation.Count > 0 && conversation[^1] is ChatMessageAssistant)
        {
            conversation.RemoveAt(conversation.Count - 1);
        }

        return [.. partitioned.System, .. partitioned.Input, .. conversation];
    }

    /// <summary>
    /// Port of <c>partition_messages</c>: system messages, then the messages with <c>source == "input"</c> (or,
    /// when there are none, everything up to and including the first user message), then the conversation.
    /// Messages carrying a <c>summary</c> metadata key always belong to the conversation.
    /// </summary>
    public static PartitionedMessages PartitionMessages(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var system = new List<ChatMessage>();
        var input = new List<ChatMessage>();
        var conversation = new List<ChatMessage>();
        foreach (var message in messages)
        {
            if (message is ChatMessageSystem)
            {
                system.Add(message);
            }
            else if (IsSummaryMessage(message))
            {
                conversation.Add(message);
            }
            else if (message.Source == "input")
            {
                input.Add(message);
            }
            else
            {
                conversation.Add(message);
            }
        }

        if (input.Count == 0)
        {
            while (conversation.Count > 0 && !IsSummaryMessage(conversation[0]))
            {
                var message = conversation[0];
                conversation.RemoveAt(0);
                input.Add(message);
                if (message is ChatMessageUser)
                {
                    break;
                }
            }
        }

        return new PartitionedMessages(system, input, conversation);
    }

    private static bool IsSummaryMessage(ChatMessage message) => message.Metadata?.ContainsKey("summary") == true;
}

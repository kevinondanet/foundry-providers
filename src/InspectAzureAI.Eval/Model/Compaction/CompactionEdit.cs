using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Model.Compaction;

/// <summary>
/// Port of <c>model/_compaction/edit.py</c> <c>CompactionEdit</c>: message editing compaction. Compacts messages by
/// editing the history to remove tool call results and thinking blocks from older turns; tool results receive a
/// placeholder to indicate they were removed.
/// </summary>
public sealed class CompactionEdit : CompactionStrategy
{
    /// <summary>Port of <c>TOOL_RESULT_REMOVED</c>: placeholder written over a cleared tool result.</summary>
    public const string ToolResultRemoved = "(Tool result removed)";

    /// <summary>Port of <c>MCP_LIST_TOOLS_NAME</c>: a server-side tool that provides tool context, never cleared.</summary>
    public const string McpListToolsName = "mcp_list_tools";

    /// <summary>Port of <c>TOOL_SEARCH_NAME</c>: a tool whose result carries tool definitions (context), never cleared.</summary>
    public const string ToolSearchName = "tool_search";

    /// <param name="threshold">Token count or fraction of the context window that triggers compaction (default 0.9).</param>
    /// <param name="memory">Warn the model to save critical content to memory before compaction when the memory tool is available.</param>
    /// <param name="keepThinkingTurns">
    /// How many recent assistant turns keep their thinking blocks: N keeps the blocks within the last N turns,
    /// null (Python <c>"all"</c>) keeps every block. Defaults to 1. Some providers do not support thinking
    /// compaction (see <see cref="ICompactionModelApi.CompactReasoningHistory"/>).
    /// </param>
    /// <param name="keepToolUses">
    /// How many recent tool use/result pairs to keep after clearing. The oldest interactions are cleared first;
    /// tool output is replaced with placeholder text so the model knows a result was removed.
    /// </param>
    /// <param name="keepToolInputs">
    /// Whether the tool call parameters stay when results are cleared. By default only the results are cleared;
    /// when false both the call and its result are removed and replaced with placeholder text.
    /// </param>
    /// <param name="excludeTools">Tool names whose uses and results are never cleared.</param>
    public CompactionEdit(
        CompactionThreshold threshold = default,
        bool memory = true,
        int? keepThinkingTurns = 1,
        int keepToolUses = 3,
        bool keepToolInputs = true,
        IReadOnlyList<string>? excludeTools = null)
        : base("edit", threshold, memory)
    {
        if (keepThinkingTurns is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keepThinkingTurns), keepThinkingTurns, "keepThinkingTurns must be null (all) or non-negative.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(keepToolUses);
        KeepThinkingTurns = keepThinkingTurns;
        KeepToolUses = keepToolUses;
        KeepToolInputs = keepToolInputs;
        ExcludeTools = excludeTools;
    }

    /// <summary>Recent assistant turns that keep thinking blocks; null means all.</summary>
    public int? KeepThinkingTurns { get; }

    /// <summary>Recent tool use/result pairs kept after clearing.</summary>
    public int KeepToolUses { get; }

    /// <summary>Whether tool call parameters survive result clearing.</summary>
    public bool KeepToolInputs { get; }

    /// <summary>Tool names never cleared.</summary>
    public IReadOnlyList<string>? ExcludeTools { get; }

    public override IReadOnlyDictionary<string, object?> ReprParams()
    {
        var parameters = new Dictionary<string, object?>(base.ReprParams(), StringComparer.Ordinal)
        {
            ["keep_thinking_turns"] = KeepThinkingTurns is { } turns ? turns : "all",
            ["keep_tool_uses"] = KeepToolUses,
            ["keep_tool_inputs"] = KeepToolInputs,
            ["exclude_tools"] = ExcludeTools,
        };
        return parameters;
    }

    /// <summary>
    /// Clears reasoning from assistant turns older than <see cref="KeepThinkingTurns"/>, clears the oldest tool
    /// results beyond <see cref="KeepToolUses"/> (removing the calls too when <see cref="KeepToolInputs"/> is
    /// false), clears saved memory content, and drops trailing assistant messages. Returns no summary message.
    /// </summary>
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

        var result = messages.ToList();

        // Phase 1: clear thinking blocks from older turns
        var canClearThinking = KeepThinkingTurns is not null && model.CompactReasoningHistory();
        if (canClearThinking)
        {
            var keepThinkingTurns = KeepThinkingTurns!.Value;
            var assistantTurnCount = 0;
            for (var i = result.Count - 1; i >= 0; i--)
            {
                if (result[i] is ChatMessageAssistant assistant)
                {
                    assistantTurnCount++;
                    if (assistantTurnCount > keepThinkingTurns)
                    {
                        result[i] = ClearReasoning(assistant);
                    }
                }
            }
        }

        // Phase 2: collect tool uses (the .NET content model has no server-side ContentToolUse, so only
        // client-side calls share the keep_tool_uses budget)
        var allToolUses = new List<ClientToolUse>();
        for (var i = 0; i < result.Count; i++)
        {
            if (result[i] is not ChatMessageAssistant { ToolCalls: { Count: > 0 } toolCalls })
            {
                continue;
            }

            foreach (var toolCall in toolCalls)
            {
                if (toolCall.Function == ToolSearchName || (ExcludeTools?.Contains(toolCall.Function) ?? false))
                {
                    continue;
                }

                var toolMessageIndex = FindToolMessage(result, toolCall.Id, i);
                if (toolMessageIndex is { } found)
                {
                    allToolUses.Add(new ClientToolUse(i, toolCall, found));
                }
            }
        }

        List<ClientToolUse> toolUsesToClear;
        if (KeepToolUses > 0 && allToolUses.Count > KeepToolUses)
        {
            toolUsesToClear = allToolUses.Take(allToolUses.Count - KeepToolUses).ToList();
        }
        else if (KeepToolUses == 0)
        {
            toolUsesToClear = allToolUses;
        }
        else
        {
            toolUsesToClear = [];
        }

        // Phase 3: apply clearing (in reverse so removals keep earlier indices valid)
        for (var i = toolUsesToClear.Count - 1; i >= 0; i--)
        {
            var (assistantIndex, toolCall, toolIndex) = toolUsesToClear[i];
            if (KeepToolInputs)
            {
                result[toolIndex] = result[toolIndex] with { Id = ShortUuid.Generate(), Content = ToolResultRemoved };
            }
            else
            {
                result.RemoveAt(toolIndex);
                result[assistantIndex] = ReplaceToolCallWithText((ChatMessageAssistant)result[assistantIndex], toolCall);
            }
        }

        // Phase 4: clear content from memory tool calls
        if (Memory)
        {
            result = CompactionMemory.ClearMemoryContent(result).ToList();
        }

        // Phase 5: strip citations
        result = TrimMessages.StripCitations(result).ToList();

        // some APIs (e.g. Anthropic) require the conversation to end with a user message
        while (result.Count > 0 && result[^1] is ChatMessageAssistant)
        {
            result.RemoveAt(result.Count - 1);
        }

        return Task.FromResult(new CompactionResult(result, null));
    }

    private readonly record struct ClientToolUse(int AssistantIndex, ToolCall ToolCall, int ToolIndex);

    /// <summary>
    /// Port of <c>_clear_reasoning</c>: removes <see cref="ContentReasoning"/> parts (Python additionally keeps
    /// Google replay anchors, which the .NET reasoning part cannot carry). String content has no reasoning and is
    /// returned as-is; a message left empty gets empty string content.
    /// </summary>
    internal static ChatMessageAssistant ClearReasoning(ChatMessageAssistant message)
    {
        if (message.Content.IsString)
        {
            return message;
        }

        var newContent = message.Content.Items!.Where(c => c is not ContentReasoning).ToList();
        return newContent.Count == 0
            ? message with { Id = ShortUuid.Generate(), Content = "" }
            : message with { Id = ShortUuid.Generate(), Content = MessageContent.FromItems(newContent) };
    }

    /// <summary>Port of <c>_find_tool_message</c>: index of the tool message answering <paramref name="toolCallId"/> after <paramref name="startIndex"/>.</summary>
    private static int? FindToolMessage(List<ChatMessage> messages, string toolCallId, int startIndex)
    {
        for (var i = startIndex + 1; i < messages.Count; i++)
        {
            if (messages[i] is ChatMessageTool tool && tool.ToolCallId == toolCallId)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>Port of <c>_replace_tool_call_with_text</c>: drops the call and appends a placeholder text part.</summary>
    private static ChatMessageAssistant ReplaceToolCallWithText(ChatMessageAssistant message, ToolCall toolCall)
    {
        var newToolCalls = (message.ToolCalls ?? []).Where(tc => tc.Id != toolCall.Id).ToList();
        var placeholder = new ContentText($"[Tool call: {toolCall.Function} (parameters and results removed from history)]");

        List<Content> newContent;
        if (message.Content.IsString)
        {
            var text = message.Content.Text!;
            newContent = text.Length > 0 ? [new ContentText(text), placeholder] : [placeholder];
        }
        else
        {
            newContent = [.. message.Content.Items!, placeholder];
        }

        return message with
        {
            Id = ShortUuid.Generate(),
            ToolCalls = newToolCalls.Count > 0 ? newToolCalls : null,
            Content = MessageContent.FromItems(newContent),
        };
    }
}

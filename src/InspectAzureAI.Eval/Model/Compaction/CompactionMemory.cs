using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Model.Compaction;

/// <summary>
/// Port of <c>model/_compaction/memory.py</c>: the memory-tool integration shared by the strategies and the
/// orchestrator (clearing saved content from the history, detecting memory calls, the pre-compaction warning).
/// </summary>
public static class CompactionMemory
{
    /// <summary>Port of <c>MEMORY_TOOL</c>: the name of the memory tool.</summary>
    public const string MemoryTool = "memory";

    /// <summary>Port of <c>MEMORY_CONTENT_ARGS</c>: memory tool arguments that carry large content.</summary>
    public static readonly IReadOnlyList<string> MemoryContentArgs = ["file_text", "insert_text", "new_str"];

    /// <summary>Placeholder written over a cleared content argument.</summary>
    public const string ContentSavedPlaceholder = "(content saved to memory)";

    /// <summary>Port of the text of <c>memory_warning_message()</c>.</summary>
    public const string MemoryWarningText =
        "Context compaction approaching. Use memory() to save concise notes on:\n"
        + "- Key decisions made and why\n"
        + "- Important discoveries (APIs, data structures, error solutions)\n"
        + "- Critical file paths and changes made\n"
        + "- Next steps to continue the task\n\n"
        + "Do NOT save raw tool outputs or full file contents—keep notes brief and synthesized.";

    /// <summary>
    /// Port of <c>clear_memory_content</c>. When memory integration is active the model may save content to memory
    /// before compaction; that content sits in the tool call arguments and would otherwise persist after
    /// compaction. Clears the large content arguments while keeping the metadata arguments (command, path, ...)
    /// so the model knows what it saved and where. Edited assistant messages are copies with a fresh id.
    /// </summary>
    public static IReadOnlyList<ChatMessage> ClearMemoryContent(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var result = messages.ToList();
        for (var i = 0; i < result.Count; i++)
        {
            if (result[i] is not ChatMessageAssistant { ToolCalls: { Count: > 0 } toolCalls } assistant)
            {
                continue;
            }

            var newToolCalls = new List<ToolCall>(toolCalls.Count);
            var modified = false;
            foreach (var toolCall in toolCalls)
            {
                if (toolCall.Function == MemoryTool && MemoryContentArgs.Any(toolCall.Arguments.ContainsKey))
                {
                    var newArguments = new JsonObject();
                    foreach (var (key, value) in toolCall.Arguments)
                    {
                        newArguments[key] = MemoryContentArgs.Contains(key) ? JsonValue.Create(ContentSavedPlaceholder) : value?.DeepClone();
                    }

                    newToolCalls.Add(new ToolCall(toolCall.Id, toolCall.Function, newArguments));
                    modified = true;
                }
                else
                {
                    newToolCalls.Add(toolCall);
                }
            }

            if (modified)
            {
                result[i] = assistant with { Id = ShortUuid.Generate(), ToolCalls = newToolCalls };
            }
        }

        return result;
    }

    /// <summary>Port of <c>has_memory_calls</c>: whether any assistant message calls the memory tool.</summary>
    public static bool HasMemoryCalls(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages.OfType<ChatMessageAssistant>().Any(m => (m.ToolCalls ?? []).Any(tc => tc.Function == MemoryTool));
    }

    /// <summary>Port of <c>memory_warning_message()</c>: the user message urging the model to save notes before compaction.</summary>
    public static ChatMessageUser MemoryWarningMessage() => new(MemoryWarningText);
}

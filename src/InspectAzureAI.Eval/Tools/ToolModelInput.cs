using InspectAzureAI.Eval.Model;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tools;

/// <summary>
/// Port of <c>tool/_tool_call.py</c> <c>ToolCallModelInputHints</c>: lets a tool adapt its model-input hook to a
/// model limitation without coupling it to the provider.
/// </summary>
/// <param name="DisableComputerScreenshotTruncation">The model does not support the truncation/redaction of computer screenshots.</param>
public sealed record ToolCallModelInputHints(bool DisableComputerScreenshotTruncation = false)
{
    public static readonly ToolCallModelInputHints None = new();
}

/// <summary>
/// Port of <c>tool/_tool_call.py</c> <c>ToolCallModelInput</c>: determines how a tool call result is played back as
/// model input. <paramref name="messageIndex"/> is the index of this result among the tool's results in the
/// message history and <paramref name="messageTotal"/> the total number of tool results in the history (Python
/// passes <c>len(tool_messages)</c>, all tools counted). Returning the same <see cref="MessageContent"/> instance
/// leaves the message untouched.
/// </summary>
public delegate MessageContent ToolCallModelInput(int messageIndex, int messageTotal, MessageContent content, ToolCallModelInputHints hints);

/// <summary>
/// Port of <c>ModelAPI.disable_computer_screenshot_truncation()</c>: an <see cref="IModelApi"/> implements this to
/// ask the computer tool not to redact old screenshots (Python: the OpenAI Responses API with native computer use).
/// Deviation: Python defines the method on the <c>ModelAPI</c> base class; here it is an optional interface, and neither
/// Foundry route implements it.
/// </summary>
public interface IToolModelInputHintsApi
{
    bool DisableComputerScreenshotTruncation();
}

/// <summary>Port of <c>model/_model.py</c> <c>resolve_tool_model_input</c>: applies the tools' <see cref="ToolDef.ModelInput"/> hooks to a conversation.</summary>
public static class ToolModelInput
{
    /// <summary>The hints for <paramref name="api"/> (a <see cref="FallbackModelApi"/> answers for its current api).</summary>
    public static ToolCallModelInputHints HintsFor(IModelApi api) => api switch
    {
        IToolModelInputHintsApi hints => new ToolCallModelInputHints(hints.DisableComputerScreenshotTruncation()),
        FallbackModelApi fallback => HintsFor(fallback.Current),
        _ => ToolCallModelInputHints.None,
    };

    /// <summary>
    /// Port of <c>resolve_tool_model_input</c>: for every tool with a <see cref="ToolDef.ModelInput"/> hook, calls it on
    /// each <see cref="ChatMessageTool"/> of that tool (index among the tool's own results, total over all tool
    /// results) and replaces the content when the hook returns a different instance, giving the message a fresh id.
    /// The input list is never mutated; it is returned as is when no tool has a hook.
    /// </summary>
    public static IReadOnlyList<ChatMessage> Resolve(IReadOnlyList<ToolDef> tools, IReadOnlyList<ChatMessage> messages, ToolCallModelInputHints? hints = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(messages);
        var handlers = tools.Where(t => t.ModelInput is not null).ToArray();
        if (handlers.Length == 0)
        {
            return messages;
        }

        hints ??= ToolCallModelInputHints.None;
        var resolved = messages.ToArray();
        var toolIndexes = new List<int>();
        for (var i = 0; i < resolved.Length; i++)
        {
            if (resolved[i] is ChatMessageTool)
            {
                toolIndexes.Add(i);
            }
        }

        foreach (var tool in handlers)
        {
            var own = toolIndexes.Where(i => ((ChatMessageTool)resolved[i]).Function == tool.Name).ToArray();
            for (var index = 0; index < own.Length; index++)
            {
                var message = (ChatMessageTool)resolved[own[index]];
                var content = tool.ModelInput!(index, toolIndexes.Count, message.Content, hints);
                if (!ReferenceEquals(content, message.Content))
                {
                    resolved[own[index]] = message with { Content = content, Id = ShortUuid.Generate() };
                }
            }
        }

        return resolved;
    }
}

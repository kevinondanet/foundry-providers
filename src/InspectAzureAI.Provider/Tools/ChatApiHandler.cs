using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Provider.Tools;

/// <summary>A <c>{"role": ..., "content": ...}</c> pair (port of <c>ChatAPIMessage</c> in <c>util/chatapi.py</c>).</summary>
public sealed record ChatApiMessage(string Role, string Content);

/// <summary>
/// Base tool-emulation handler (port of <c>ChatAPIHandler</c> in
/// <c>src/inspect_ai/model/_providers/util/chatapi.py</c>). The azureai provider only calls
/// <see cref="InputWithTools"/>, <see cref="AssistantMessage"/> and <see cref="ParseAssistantResponse"/>;
/// <see cref="ToolMessage"/> is ported for completeness.
/// </summary>
public class ChatApiHandler(string model)
{
    /// <summary>Model name stamped onto parsed assistant messages.</summary>
    public string Model { get; } = model;

    /// <summary>Default: returns the input unchanged.</summary>
    public virtual IReadOnlyList<ChatMessage> InputWithTools(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools) => input;

    /// <summary>Default: the whole response is plain assistant text.</summary>
    public virtual ChatMessageAssistant ParseAssistantResponse(string response, IReadOnlyList<ToolInfo> tools) =>
        new(response, model: Model, source: "generate");

    /// <summary>Default: <c>{"role": "assistant", "content": message.text}</c>.</summary>
    public virtual ChatApiMessage AssistantMessage(ChatMessageAssistant message) => new("assistant", message.Text);

    /// <summary>Default: <c>{"role": "tool", "content": "Error: ..." | message.text}</c>.</summary>
    public virtual ChatApiMessage ToolMessage(ChatMessageTool message) =>
        new("tool", message.Error is not null ? $"Error: {message.Error.Message}" : message.Text);
}

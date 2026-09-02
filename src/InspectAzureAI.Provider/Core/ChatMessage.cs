using System.Diagnostics.CodeAnalysis;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Provider.Core;

/// <summary>
/// Base chat message (port of <c>ChatMessageBase</c> in
/// <c>src/inspect_ai/model/_chat_message.py</c>). <see cref="Id"/> is auto-filled with a
/// short uuid, mirroring <c>model_post_init</c>.
/// </summary>
public abstract record ChatMessage
{
    /// <summary>Unique identifier for the message.</summary>
    public string? Id { get; init; } = ShortUuid.Generate();

    /// <summary>Content (simple string or list of content objects).</summary>
    public required MessageContent Content { get; init; }

    /// <summary>Source of the message: <c>input</c>, <c>generate</c> or <c>operator</c>.</summary>
    public string? Source { get; init; }

    /// <summary>Additional message metadata.</summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    /// <summary>Role discriminator (<c>system</c>, <c>user</c>, <c>assistant</c>, <c>tool</c>).</summary>
    public abstract string Role { get; }

    /// <summary>
    /// Port of the <c>text</c> property: the string content as-is, or the <c>text</c> of every
    /// <see cref="ContentText"/> item joined with a newline (non-text items are dropped).
    /// </summary>
    public string Text =>
        Content.IsString
            ? Content.Text!
            : string.Join("\n", Content.Items!.OfType<ContentText>().Select(c => c.Text));

    /// <summary>Port of <c>content_list</c>: the content as a list of content objects.</summary>
    public IReadOnlyList<Content> ContentList =>
        Content.IsString ? [new ContentText(Content.Text!)] : Content.Items!;
}

/// <summary>System chat message (port of <c>ChatMessageSystem</c>).</summary>
public sealed record ChatMessageSystem : ChatMessage
{
    public ChatMessageSystem()
    {
    }

    [SetsRequiredMembers]
    public ChatMessageSystem(MessageContent content)
    {
        Content = content;
    }

    public override string Role => "system";
}

/// <summary>User chat message (port of <c>ChatMessageUser</c>).</summary>
public sealed record ChatMessageUser : ChatMessage
{
    public ChatMessageUser()
    {
    }

    [SetsRequiredMembers]
    public ChatMessageUser(MessageContent content)
    {
        Content = content;
    }

    public override string Role => "user";

    /// <summary>Ids of tool calls this message carries media results for.</summary>
    public IReadOnlyList<string>? ToolCallId { get; init; }
}

/// <summary>Assistant chat message (port of <c>ChatMessageAssistant</c>).</summary>
public sealed record ChatMessageAssistant : ChatMessage
{
    public ChatMessageAssistant()
    {
    }

    [SetsRequiredMembers]
    public ChatMessageAssistant(MessageContent content, IReadOnlyList<ToolCall>? toolCalls = null, string? model = null, string? source = null)
    {
        Content = content;
        ToolCalls = toolCalls;
        Model = model;
        Source = source;
    }

    public override string Role => "assistant";

    /// <summary>Tool calls made by the model.</summary>
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>Model used to generate the message.</summary>
    public string? Model { get; init; }
}

/// <summary>Tool result chat message (port of <c>ChatMessageTool</c>).</summary>
public sealed record ChatMessageTool : ChatMessage
{
    public ChatMessageTool()
    {
    }

    [SetsRequiredMembers]
    public ChatMessageTool(MessageContent content, string? toolCallId = null, string? function = null, ToolCallError? error = null)
    {
        Content = content;
        ToolCallId = toolCallId;
        Function = function;
        Error = error;
    }

    public override string Role => "tool";

    /// <summary>Id of the tool call this message answers.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Name of the function called.</summary>
    public string? Function { get; init; }

    /// <summary>Error which occurred during tool execution, if any.</summary>
    public ToolCallError? Error { get; init; }
}

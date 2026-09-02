using System.ClientModel.Primitives;
using System.Text.Json.Nodes;
using Azure.AI.Inference;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Provider.Tools;

/// <summary>
/// Message conversion (port of <c>chat_request_messages</c>, <c>chat_request_message</c>,
/// <c>chat_content_item</c>, <c>mistral_message_reducer</c> and
/// <c>fold_user_message_into_tool_message</c> in <c>azureai.py</c>).
/// </summary>
public static class AzureMessageConversion
{
    /// <summary>Port of <c>chat_request_messages</c>; applies the Mistral reducer when <paramref name="isMistral"/>.</summary>
    public static List<ChatRequestMessage> ChatRequestMessages(IReadOnlyList<ChatMessage> messages, ChatApiHandler? handler, bool isMistral = false)
    {
        var chatMessages = messages.Select(m => ChatRequestMessage(m, handler)).ToList();
        if (isMistral)
        {
            chatMessages = chatMessages.Aggregate(new List<ChatRequestMessage>(), MistralMessageReducer);
        }

        return chatMessages;
    }

    /// <summary>Port of <c>mistral_message_reducer</c>: folds a user message immediately following a tool message into it.</summary>
    public static List<ChatRequestMessage> MistralMessageReducer(List<ChatRequestMessage> messages, ChatRequestMessage message)
    {
        if (messages.Count > 0 && messages[^1] is ChatRequestToolMessage toolMessage && message is ChatRequestUserMessage userMessage)
        {
            messages[^1] = FoldUserMessageIntoToolMessage(toolMessage, userMessage);
        }
        else
        {
            messages.Add(message);
        }

        return messages;
    }

    /// <summary>Port of <c>fold_user_message_into_tool_message</c>.</summary>
    public static ChatRequestToolMessage FoldUserMessageIntoToolMessage(ChatRequestToolMessage toolMessage, ChatRequestUserMessage userMessage)
    {
        static string ConvertContentItemsToString(IEnumerable<ChatMessageContentItem> items)
        {
            var list = items.ToList();
            if (!list.All(item => item is ChatMessageTextContentItem or ChatMessageImageContentItem))
            {
                throw new InvalidOperationException("Expected all items to be TextContentItem or ImageContentItem");
            }

            return string.Concat(list.Select(item => item switch
            {
                ChatMessageTextContentItem text => text.Text,
                ChatMessageImageContentItem image => $"[Image: {ImageUrlOf(image)}]",
                _ => throw new InvalidOperationException("Unexpected content item type"),
            }));
        }

        var toolContent = toolMessage.Content;
        var userContent = userMessage.MultimodalContentItems is { Count: > 0 } items
            ? ConvertContentItemsToString(items)
            : userMessage.Content;

        return new ChatRequestToolMessage((toolContent ?? "") + (userContent ?? ""), toolMessage.ToolCallId);
    }

    /// <summary>Port of <c>chat_request_message</c>.</summary>
    public static ChatRequestMessage ChatRequestMessage(ChatMessage message, ChatApiHandler? handler)
    {
        switch (message)
        {
            case ChatMessageSystem system:
                return new ChatRequestSystemMessage(system.Text);
            case ChatMessageUser user:
                return user.Content.IsString
                    ? new ChatRequestUserMessage(user.Content.Text!)
                    : new ChatRequestUserMessage(user.Content.Items!.Select(ChatContentItem));
            case ChatMessageTool tool:
                return new ChatRequestToolMessage(
                    tool.Error is not null ? $"Error: {tool.Error.Message}" : tool.Text,
                    tool.ToolCallId ?? "None");
            case ChatMessageAssistant assistant:
                if (assistant.ToolCalls is { Count: > 0 })
                {
                    if (handler is not null)
                    {
                        return new ChatRequestAssistantMessage(handler.AssistantMessage(assistant).Content);
                    }

                    var text = assistant.Text;
                    return new ChatRequestAssistantMessage(
                        assistant.ToolCalls.Select(AzureToolConversion.ChatToolCall),
                        text.Length > 0 ? text : null);
                }

                return new ChatRequestAssistantMessage(assistant.Text);
            default:
                throw new ArgumentException($"Unsupported message type {message.GetType().Name}", nameof(message));
        }
    }

    /// <summary>
    /// Port of <c>chat_content_item</c>: text and (inline data URI) images only; anything else raises
    /// <c>Azure AI models do not support audio or video inputs.</c>
    /// </summary>
    public static ChatMessageContentItem ChatContentItem(Content content)
    {
        switch (content)
        {
            case ContentText text:
                return new ChatMessageTextContentItem(text.Text);
            case ContentImage image:
                var url = InlineMedia.InlineMediaDataUri(image.Image, "image");
                return new ChatMessageImageContentItem(new Uri(url), new ChatMessageImageDetailLevel(image.Detail));
            default:
                throw new InvalidOperationException("Azure AI models do not support audio or video inputs.");
        }
    }

    /// <summary>
    /// The SDK does not expose an image item's URL, so it is read back from the item's own wire
    /// serialisation (<c>image_url.url</c>).
    /// </summary>
    public static string ImageUrlOf(ChatMessageImageContentItem image)
    {
        var json = JsonNode.Parse(ModelReaderWriter.Write(image).ToString())!.AsObject();
        return json["image_url"]?["url"]?.GetValue<string>() ?? "";
    }

    /// <summary>Wire form (<c>as_dict()</c>) of a request message, via the SDK's own serialiser.</summary>
    public static JsonObject AsDict(ChatRequestMessage message) =>
        JsonNode.Parse(ModelReaderWriter.Write(message).ToString())!.AsObject();

    /// <summary>Wire form (<c>as_dict()</c>) of a tool definition, via the SDK's own serialiser.</summary>
    public static JsonObject AsDict(ChatCompletionsToolDefinition tool) =>
        JsonNode.Parse(ModelReaderWriter.Write(tool).ToString())!.AsObject();
}

using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Tools;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Provider.OpenAI;

/// <summary>
/// Converts the conversation to Responses API <c>input</c> items (port of <c>openai_responses_inputs</c>
/// and its helpers in <c>src/inspect_ai/model/_openai_responses.py</c>). System messages travel as
/// <c>developer</c> messages. An assistant turn becomes its reasoning items (replayed through
/// <c>encrypted_content</c>, the only form the server accepts back with <c>store: false</c>), one
/// <c>message</c> item per run of text and a <c>function_call</c> per tool call; a tool result becomes a
/// <c>function_call_output</c>. Message and function-call item ids are not replayed (they are optional with
/// <c>store: false</c>, and Python omits them too when it has none); the reasoning item id rides on
/// <see cref="ContentReasoning.Signature"/>.
/// </summary>
public static class ResponsesInput
{
    /// <summary>The API's limit on <c>function_call.arguments</c> on input (1 MiB); longer strings are middle-truncated.</summary>
    public const int MaxFunctionCallArguments = 1_048_576;

    public const string ReasoningNotReplayableWarning =
        "OpenAI Responses on Azure: a reasoning item without encrypted content cannot be replayed and was left out of the conversation.";

    public const string ServerToolUseSkippedWarning =
        "OpenAI Responses on Azure: server-side tool use content is not replayed on this route (the built-in tools are not ported).";

    public const string AudioVideoUnsupportedError = "OpenAI Responses on Azure does not support audio or video inputs.";

    /// <summary>The <c>input</c> array for a conversation.</summary>
    public static JsonArray InputItems(IReadOnlyList<ChatMessage> input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var items = new JsonArray();
        foreach (var message in input)
        {
            switch (message)
            {
                case ChatMessageSystem system:
                    items.Add(MessageItem("developer", ContentParts(system.ContentList)));
                    break;
                case ChatMessageUser user:
                    items.Add(MessageItem("user", ContentParts(user.ContentList)));
                    break;
                case ChatMessageAssistant assistant:
                    foreach (var item in AssistantItems(assistant))
                    {
                        items.Add(item);
                    }

                    break;
                case ChatMessageTool tool:
                    items.Add(FunctionCallOutputItem(tool));
                    break;
            }
        }

        return items;
    }

    /// <summary>An input message item (<c>developer</c>, <c>user</c> or <c>assistant</c>).</summary>
    public static JsonObject MessageItem(string role, JsonArray content) =>
        new() { ["type"] = "message", ["role"] = role, ["content"] = content };

    /// <summary>
    /// Input content parts: <c>input_text</c>, <c>input_image</c> and <c>input_file</c>. Reasoning inside a
    /// user or tool message has no wire form and is dropped; server-side tool use is dropped with a warning;
    /// audio and video are not supported on this route.
    /// </summary>
    public static JsonArray ContentParts(IReadOnlyList<Content> items)
    {
        var parts = new JsonArray();
        foreach (var item in items)
        {
            switch (item)
            {
                case ContentText text:
                    parts.Add(new JsonObject { ["type"] = "input_text", ["text"] = text.Text });
                    break;
                case ContentImage image:
                    parts.Add(ImagePart(image));
                    break;
                case ContentDocument document:
                    parts.Add(FilePart(document));
                    break;
                case ContentToolUse:
                    ProviderLogger.WarnOnce(ServerToolUseSkippedWarning);
                    break;
                case ContentAudio or ContentVideo:
                    throw new InvalidOperationException(AudioVideoUnsupportedError);
            }
        }

        return parts;
    }

    /// <summary>An <c>input_image</c> part: http(s) URLs travel verbatim, anything else must be an inline data URI.</summary>
    public static JsonObject ImagePart(ContentImage image)
    {
        var url = IsHttpUrl(image.Image) ? image.Image : InlineMedia.InlineMediaDataUri(image.Image, "image");
        return new JsonObject { ["type"] = "input_image", ["image_url"] = url, ["detail"] = image.Detail };
    }

    /// <summary>An <c>input_file</c> part carrying the document inline.</summary>
    public static JsonObject FilePart(ContentDocument document)
    {
        var mimeHint = document.MimeType.Length > 0 ? document.MimeType : null;
        return new JsonObject
        {
            ["type"] = "input_file",
            ["file_data"] = InlineMedia.InlineMediaDataUri(document.Document, "document", mimeHint),
            ["filename"] = document.Filename.Length > 0 ? document.Filename : "document",
        };
    }

    /// <summary>
    /// The items of an assistant turn in content order: reasoning items, <c>message</c> items for each run of
    /// text (refusals as <c>refusal</c> parts), generated images replayed as a user <c>input_image</c> message
    /// (Python does the same: replaying an <c>image_generation_call</c> needs <c>store: true</c>), then the
    /// <c>function_call</c> items. Empty text is dropped when the turn has tool calls.
    /// </summary>
    public static List<JsonObject> AssistantItems(ChatMessageAssistant assistant, IReadOnlyList<string?>? phases = null)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        var items = new List<JsonObject>();
        var pending = new JsonArray();
        var hasToolCalls = assistant.ToolCalls is { Count: > 0 };
        var textIndex = 0;
        string? pendingPhase = null;

        void Flush()
        {
            if (pending.Count > 0)
            {
                var message = new JsonObject { ["type"] = "message", ["role"] = "assistant", ["status"] = "completed", ["content"] = pending };
                if (pendingPhase is not null) message["phase"] = pendingPhase;
                items.Add(message);
                pending = new JsonArray();
            }
        }

        foreach (var content in assistant.ContentList)
        {
            switch (content)
            {
                case ContentReasoning reasoning:
                    Flush();
                    if (ReasoningItem(reasoning) is { } item)
                    {
                        items.Add(item);
                    }

                    break;
                case ContentText text:
                    var phase = phases is not null && textIndex < phases.Count ? phases[textIndex] : null;
                    textIndex++;
                    if (phase != pendingPhase) Flush();
                    pendingPhase = phase;
                    if (text.Text.Length == 0 && hasToolCalls)
                    {
                        break;
                    }

                    pending.Add(text.Refusal == true
                        ? new JsonObject { ["type"] = "refusal", ["refusal"] = text.Text }
                        : new JsonObject { ["type"] = "output_text", ["text"] = text.Text, ["annotations"] = new JsonArray() });
                    break;
                case ContentImage image:
                    Flush();
                    items.Add(MessageItem("user", new JsonArray(ImagePart(image))));
                    break;
                case ContentToolUse:
                    Flush();
                    ProviderLogger.WarnOnce(ServerToolUseSkippedWarning);
                    break;
                default:
                    Flush();
                    break;
            }
        }

        Flush();
        foreach (var call in assistant.ToolCalls ?? [])
        {
            items.Add(FunctionCallItem(call));
        }

        return items;
    }

    /// <summary>
    /// A <c>reasoning</c> input item: the encrypted blob (<see cref="ContentReasoning.Reasoning"/> when
    /// <see cref="ContentReasoning.Redacted"/>), the item id from <see cref="ContentReasoning.Signature"/>
    /// (the key is omitted when unknown) and the summary parts; never a <c>content</c> key, which the API
    /// rejects on input. Null, with a one-time warning, when there is no encrypted blob to replay.
    /// </summary>
    public static JsonObject? ReasoningItem(ContentReasoning reasoning)
    {
        ArgumentNullException.ThrowIfNull(reasoning);
        if (!reasoning.Redacted || reasoning.Reasoning.Length == 0)
        {
            ProviderLogger.WarnOnce(ReasoningNotReplayableWarning);
            return null;
        }

        var item = new JsonObject { ["type"] = "reasoning" };
        if (reasoning.Signature is { Length: > 0 })
        {
            item["id"] = reasoning.Signature;
        }

        item["encrypted_content"] = reasoning.Reasoning;
        item["summary"] = reasoning.Summary is { Length: > 0 }
            ? new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = reasoning.Summary })
            : new JsonArray();
        return item;
    }

    /// <summary>A <c>function_call</c> input item (arguments as Python's <c>json.dumps</c> would print them, capped at 1 MiB).</summary>
    public static JsonObject FunctionCallItem(ToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var arguments = PythonJson.Dumps(call.Arguments);
        arguments = ToolCallParsing.TruncateStringToBytes(arguments, MaxFunctionCallArguments)?.Output ?? arguments;
        return new JsonObject
        {
            ["type"] = "function_call",
            ["call_id"] = call.Id,
            ["name"] = ResponsesTools.Alias(call.Function),
            ["arguments"] = arguments,
        };
    }

    /// <summary>
    /// A <c>function_call_output</c> item: the error message verbatim for a failed call (no <c>Error:</c>
    /// prefix, as Python), the text for text results, or an array of <c>input_text</c>/<c>input_image</c>
    /// parts when the result carries images.
    /// </summary>
    public static JsonObject FunctionCallOutputItem(ChatMessageTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        JsonNode? output;
        if (tool.Error is not null)
        {
            output = JsonValue.Create(tool.Error.Message);
        }
        else if (tool.Content.IsString || tool.ContentList.All(c => c is ContentText))
        {
            output = JsonValue.Create(tool.Text);
        }
        else
        {
            output = ContentParts(tool.ContentList.Where(c => c is ContentText or ContentImage).ToList());
        }

        return new JsonObject
        {
            ["type"] = "function_call_output",
            ["call_id"] = tool.ToolCallId ?? tool.Function ?? "",
            ["output"] = output,
        };
    }

    private static bool IsHttpUrl(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}

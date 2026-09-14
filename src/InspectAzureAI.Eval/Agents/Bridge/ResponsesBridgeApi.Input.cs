using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Tools;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Agents.Bridge;

/// <summary>Responses <c>input</c> items into Inspect messages (port of <c>messages_from_responses_input</c>, <c>responses_impl.py:715-1128</c>).</summary>
public static partial class ResponsesBridgeApi
{
    internal const string WebSearchCallDroppedWarning =
        "A web_search_call input item was dropped: the agent bridge does not replay web search results on the Responses route.";

    internal const string AgentMessageEncryptedWarning =
        "agent_message item carries only encrypted content: the served model sees only a placeholder.";

    internal const string AgentMessageEmptyWarning =
        "agent_message item carries no readable content: rendering a placeholder.";

    /// <summary>
    /// Port of <c>messages_from_responses_input</c>. A string input becomes one user message. Consecutive
    /// assistant-side items (<c>message</c> with <c>output_text</c> or <c>refusal</c>, a typeless assistant message,
    /// <c>function_call</c>, <c>custom_tool_call</c>, <c>reasoning</c>, <c>web_search_call</c>) become one assistant
    /// message. In their text, <c>&lt;content-internal&gt;</c> tags are removed and a <c>&lt;think&gt;</c> tag becomes
    /// reasoning. <c>agent_message</c> becomes an attributed user message whose raw item is kept in
    /// <c>Metadata["agent_message"]</c>. Role messages and tool outputs map directly. <c>additional_tools</c> and
    /// <c>tool_search_output</c> are skipped. Any other item type throws <see cref="BridgeRequestException"/>.
    /// <paramref name="namespaces"/> is the request's inner-tool-to-namespace map: a replayed <c>function_call</c>
    /// whose <c>namespace</c> it does not declare is logged once, because the served model receives the call without
    /// that namespace. <paramref name="modelName"/> is written as the <c>Model</c> of those assistant messages.
    /// </summary>
    public static IReadOnlyList<ChatMessage> MessagesFromResponsesInput(JsonNode input, IReadOnlyDictionary<string, string> namespaces, string? modelName = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(namespaces);
        if (input is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return [new ChatMessageUser(MessageContent.FromItems([new ContentText(text)]))];
        }

        var messages = new List<ChatMessage>();
        var functionCalls = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new List<JsonObject>();

        void CollectPendingAssistant()
        {
            if (pending.Count > 0)
            {
                messages.Add(AssistantFromItems(pending, namespaces, functionCalls, modelName));
                pending.Clear();
            }
        }

        foreach (var node in BridgeJson.RequireArray(input, "input"))
        {
            var item = node as JsonObject ?? throw new BridgeRequestException($"invalid request field in bridged request (input[]: expected an object, got {BridgeJson.Describe(node)})");
            if (IsAssistantItem(item))
            {
                pending.Add(item);
                continue;
            }

            CollectPendingAssistant();
            var type = BridgeJson.GetString(item, "type");
            if (type == "agent_message")
            {
                messages.Add(AgentMessage(item));
            }
            else if (item.ContainsKey("role") && item.ContainsKey("content"))
            {
                messages.Add(RoleMessage(item));
            }
            else
            {
                switch (type)
                {
                    case "function_call_output" or "custom_tool_call_output":
                        var callId = BridgeJson.GetString(item, "call_id");
                        messages.Add(new ChatMessageTool(
                            ToolOutputContent(item["output"]),
                            callId,
                            callId is null ? null : functionCalls.GetValueOrDefault(callId)));
                        break;
                    case "additional_tools" or "tool_search_output":
                        // additional_tools are merged into the request's tools; tool_search_output is harvested for namespaces
                        break;
                    default:
                        throw new BridgeRequestException($"Type {type ?? "None"} is not supported by the agent bridge");
                }
            }
        }

        CollectPendingAssistant();
        return messages;
    }

    /// <summary>
    /// A replayed <c>reasoning</c> item as <see cref="ContentReasoning"/> (port of <c>reasoning_from_responses_reasoning</c>,
    /// <c>model/_openai_responses.py:1117-1164</c>). The readable <c>content</c> texts are joined by newlines and the
    /// <c>summary</c> texts by blank lines.
    /// <list type="bullet">
    /// <item>With both readable text and <c>encrypted_content</c>, the encrypted blob is the redacted reasoning, and the summary (or else the readable text) is the summary.</item>
    /// <item>Otherwise the reasoning is the readable text, else the blob, else empty; it is redacted only when it is the blob.</item>
    /// </list>
    /// The item id is the signature. This differs from the provider's <see cref="InspectAzureAI.Provider.OpenAI.ResponsesOutput.ReasoningFromItem"/>,
    /// which keeps readable text unredacted and so would break encrypted-reasoning replay.
    /// </summary>
    public static ContentReasoning ReplayReasoningFromItem(JsonObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var readable = JoinTexts(item["content"], "\n");
        var summary = JoinTexts(item["summary"], "\n\n");
        var encrypted = BridgeJson.GetString(item, "encrypted_content");
        var id = BridgeJson.GetString(item, "id");
        if (readable is not null && encrypted is not null)
        {
            return new ContentReasoning(encrypted, id, Redacted: true) { Summary = summary ?? readable };
        }

        return new ContentReasoning(readable ?? encrypted ?? "", id, readable is null && encrypted is not null) { Summary = summary };
    }

    /// <summary>Port of <c>is_assistant_message_param</c>, less the item types this port rejects (computer, MCP, tool search).</summary>
    private static bool IsAssistantItem(JsonObject item)
    {
        if (!item.ContainsKey("type"))
        {
            return BridgeJson.GetString(item, "role") == "assistant" && item.ContainsKey("content");
        }

        return BridgeJson.GetString(item, "type") switch
        {
            "message" => BridgeJson.GetString(item, "role") == "assistant"
                && item["content"] is JsonArray parts
                && parts.OfType<JsonObject>().Any(part => BridgeJson.GetString(part, "type") is "output_text" or "refusal"),
            "function_call" or "custom_tool_call" or "reasoning" or "web_search_call" => true,
            _ => false,
        };
    }

    private static ChatMessageAssistant AssistantFromItems(List<JsonObject> items, IReadOnlyDictionary<string, string> namespaces, Dictionary<string, string> functionCalls, string? modelName)
    {
        var content = new List<Content>();
        var toolCalls = new List<ToolCall>();
        foreach (var item in items)
        {
            if (!item.ContainsKey("type"))
            {
                AddSimpleAssistantContent(content, item["content"]);
                continue;
            }

            switch (BridgeJson.GetString(item, "type"))
            {
                case "message":
                    foreach (var part in (item["content"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        switch (BridgeJson.GetString(part, "type"))
                        {
                            case "output_text":
                                AddAssistantText(content, BridgeJson.GetString(part, "text") ?? "");
                                break;
                            case "refusal":
                                content.Add(new ContentText(BridgeJson.GetString(part, "refusal") ?? "") { Refusal = true });
                                break;
                        }
                    }

                    break;

                case "function_call":
                {
                    var callId = BridgeJson.GetString(item, "call_id") ?? "";
                    var name = BridgeJson.GetString(item, "name") ?? "";
                    functionCalls[callId] = name;
                    if (BridgeJson.GetString(item, "namespace") is { Length: > 0 } ns && namespaces.GetValueOrDefault(name) != ns)
                    {
                        ProviderLogger.WarnOnce($"A replayed function_call '{name}' carries namespace '{ns}', which this request does not declare; the served model receives the call without it.");
                    }

                    var arguments = item["arguments"] is JsonValue argumentsValue && argumentsValue.TryGetValue<string>(out var argumentsText)
                        ? argumentsText
                        : PythonJson.Dumps(item["arguments"]);
                    toolCalls.Add(ToolCallParsing.ParseToolCall(callId, name, arguments));
                    break;
                }

                case "custom_tool_call":
                {
                    var callId = BridgeJson.GetString(item, "call_id") ?? "";
                    var name = BridgeJson.GetString(item, "name") ?? "";
                    functionCalls[callId] = name;
                    toolCalls.Add(new ToolCall(callId, name, new JsonObject { ["input"] = item["input"]?.DeepClone() }) { Type = "custom" });
                    break;
                }

                case "reasoning":
                    content.Add(ReplayReasoningFromItem(item));
                    break;

                case "web_search_call":
                    ProviderLogger.WarnOnce(WebSearchCallDroppedWarning);
                    break;
            }
        }

        return new ChatMessageAssistant(MessageContent.FromItems(content), toolCalls.Count > 0 ? toolCalls : null, modelName);
    }

    /// <summary>A typeless <c>{role: assistant, content}</c> item: a string, or <c>input_text</c> / <c>input_image</c> / <c>input_file</c> parts.</summary>
    private static void AddSimpleAssistantContent(List<Content> content, JsonNode? raw)
    {
        if (raw is JsonValue value && value.TryGetValue<string>(out var text))
        {
            AddAssistantText(content, text);
            return;
        }

        foreach (var part in BridgeJson.RequireArray(raw, "input[].content").OfType<JsonObject>())
        {
            switch (BridgeJson.GetString(part, "type"))
            {
                case "input_text":
                    AddAssistantText(content, BridgeJson.GetString(part, "text") ?? "");
                    break;
                case "input_image" when BridgeJson.GetString(part, "image_url") is { } url:
                    content.Add(new ContentImage(url, BridgeJson.GetString(part, "detail") ?? "auto"));
                    break;
                case "input_file":
                    content.Add(new ContentDocument(BridgeJson.GetString(part, "file_data") ?? "", BridgeJson.GetString(part, "filename") ?? ""));
                    break;
            }
        }
    }

    /// <summary>Assistant text: <c>&lt;content-internal&gt;</c> removed (D-R8), a think tag restored as reasoning, remaining text kept when non-empty.</summary>
    private static void AddAssistantText(List<Content> content, string text)
    {
        if (ContentInternalRegex().IsMatch(text))
        {
            text = ContentInternalRegex().Replace(text, "").Trim();
        }

        var (remaining, reasoning) = ThinkTags.Parse(text);
        content.AddRange(reasoning);
        if (remaining.Length > 0)
        {
            content.Add(new ContentText(remaining));
        }
    }

    /// <summary>Codex Multi-Agent V2 <c>agent_message</c> (<c>responses_impl.py:964-1014</c>): an author-attributed user message.</summary>
    private static ChatMessageUser AgentMessage(JsonObject item)
    {
        var author = BridgeJson.GetString(item, "author") is { Length: > 0 } given ? given : "agent";
        var parts = (item["content"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var texts = parts
            .Where(part => BridgeJson.GetString(part, "type") == "input_text")
            .Select(part => BridgeJson.GetString(part, "text"))
            .OfType<string>()
            .ToList();
        if (texts.Count == 0)
        {
            if (parts.Any(part => BridgeJson.GetString(part, "type") == "encrypted_content"))
            {
                ProviderLogger.WarnOnce(AgentMessageEncryptedWarning);
                texts.Add("[encrypted content: readable only by OpenAI]");
            }
            else
            {
                ProviderLogger.WarnOnce(AgentMessageEmptyWarning);
                texts.Add("[no readable content]");
            }
        }

        return new ChatMessageUser(MessageContent.FromItems([new ContentText($"Agent message from {author}:\n" + string.Join("\n", texts))]))
        {
            Metadata = new Dictionary<string, object?> { ["agent_message"] = item.DeepClone() },
        };
    }

    /// <summary>A <c>{role, content}</c> input message: <c>user</c>, <c>assistant</c>, or anything else (developer, system) as a system message.</summary>
    private static ChatMessage RoleMessage(JsonObject item)
    {
        List<JsonObject> parts = item["content"] switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => [new JsonObject { ["type"] = "input_text", ["text"] = text }],
            JsonArray array => array.Select(part => part as JsonObject
                ?? throw new BridgeRequestException($"invalid request field in bridged request (input[].content[]: expected an object, got {BridgeJson.Describe(part)})")).ToList(),
            JsonObject single => [single],
            var other => throw new BridgeRequestException($"invalid request field in bridged request (input[].content: expected a string or a list, got {BridgeJson.Describe(other)})"),
        };
        var content = MessageContent.FromItems(parts.Select(ContentFromInputPart).ToList());
        return BridgeJson.GetString(item, "role") switch
        {
            "user" => new ChatMessageUser(content),
            "assistant" => new ChatMessageAssistant(content),
            _ => new ChatMessageSystem(content),
        };
    }

    /// <summary>Port of <c>content_from_response_input_content_param</c>.</summary>
    private static Content ContentFromInputPart(JsonObject part) => BridgeJson.GetString(part, "type") switch
    {
        "input_text" => new ContentText(BridgeJson.GetString(part, "text") ?? ""),
        "input_image" => new ContentImage(BridgeJson.GetString(part, "image_url") ?? "", BridgeJson.GetString(part, "detail") ?? "auto"),
        "input_file" => new ContentDocument(BridgeJson.GetString(part, "file_data") ?? "", BridgeJson.GetString(part, "filename") ?? ""),
        _ => throw new BridgeRequestException($"Unexpected input from responses API: {PythonJson.Dumps(part)}"),
    };

    /// <summary>Port of <c>_tool_content_from_openai_tool_output</c>: a string verbatim, or the <c>input_text</c> / <c>input_image</c> parts of a list.</summary>
    private static MessageContent ToolOutputContent(JsonNode? output)
    {
        if (output is null)
        {
            return "";
        }

        if (output is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        var content = new List<Content>();
        foreach (var part in (output as JsonArray ?? []).OfType<JsonObject>())
        {
            switch (BridgeJson.GetString(part, "type"))
            {
                case "input_text":
                    content.Add(new ContentText(BridgeJson.GetString(part, "text") ?? ""));
                    break;
                case "input_image" when part.ContainsKey("image_url"):
                    content.Add(new ContentImage(
                        BridgeJson.GetString(part, "image_url") ?? "",
                        BridgeJson.GetString(part, "detail") is { Length: > 0 } detail ? detail : "auto"));
                    break;
            }
        }

        return MessageContent.FromItems(content);
    }

    private static string? JoinTexts(JsonNode? parts, string separator) =>
        parts is JsonArray { Count: > 0 } array
            ? string.Join(separator, array.Select(part => part is JsonObject obj ? BridgeJson.GetString(obj, "text") ?? "" : ""))
            : null;

    [GeneratedRegex("<content-internal>(.*?)</content-internal>", RegexOptions.Singleline)]
    private static partial Regex ContentInternalRegex();
}

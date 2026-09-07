using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Agents.Bridge;

/// <summary>
/// Port of <c>agent/_bridge/anthropic_api_impl.py</c>: an Anthropic Messages request into Inspect messages,
/// tools, tool choice and config; a <see cref="ModelOutput"/> back into a Messages response; and the
/// synthesized event stream of <c>inspect_sandbox_tools/_agent_bridge/proxy.py</c> for <c>stream: true</c>.
/// </summary>
public static class AnthropicBridgeApi
{
    /// <summary>Port of <c>_util/constants.py</c> <c>NO_CONTENT</c>: the Messages API rejects empty text blocks.</summary>
    public const string NoContent = "(no content)";

    private static readonly IReadOnlyDictionary<int, string> ErrorTypes = new Dictionary<int, string>
    {
        [400] = "invalid_request_error",
        [401] = "authentication_error",
        [403] = "permission_error",
        [404] = "not_found_error",
        [413] = "request_too_large",
        [429] = "rate_limit_error",
        [500] = "api_error",
        [529] = "overloaded_error",
    };

    /// <summary>Port of <c>inspect_anthropic_api_request_impl</c>: parse, generate through the bridge (which tracks state) and answer with a Messages response.</summary>
    public static async Task<JsonObject> HandleAsync(AgentBridge bridge, JsonObject request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(request);
        var parsed = ParseRequest(request);
        var output = await bridge.GenerateAsync(parsed.Model, parsed.Messages, parsed.Tools, parsed.ToolChoice, parsed.Config, cancellationToken).ConfigureAwait(false);
        return ResponseFromOutput(output, parsed.Model);
    }

    /// <summary>The request half of <c>inspect_anthropic_api_request_impl</c>: model, messages (with <c>system</c> hoisted into leading system messages, one per block), tools, tool choice and config.</summary>
    public static BridgeRequest ParseRequest(JsonObject request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = BridgeJson.GetString(request, "model");
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new BridgeRequestException("Missing required parameter: 'model'.");
        }

        if (request["messages"] is null)
        {
            throw new BridgeRequestException("Missing required parameter: 'messages'.");
        }

        var tools = ToolsFromAnthropicTools(request["tools"]);
        var toolChoice = ToolChoiceFromAnthropicToolChoice(request["tool_choice"]) ?? ToolChoice.Auto;
        var messages = MessagesFromAnthropicInput(BridgeJson.RequireArray(request["messages"], "messages"), tools).ToList();
        var config = GenerateConfigFromAnthropic(request);

        // One system message per Anthropic block: the API treats a block starting with an x-anthropic-*-header
        // line as request metadata and drops it, so concatenating blocks can silently discard instructions.
        var offset = 0;
        foreach (var text in SystemToTexts(request["system"]))
        {
            messages.Insert(offset++, new ChatMessageSystem(text));
        }

        return new BridgeRequest(model, messages, tools, toolChoice, config, BridgeJson.GetBool(request, "stream") ?? false);
    }

    /// <summary>Port of <c>anthropic_system_to_texts</c>: a string or text blocks into one text per non-empty block.</summary>
    public static IReadOnlyList<string> SystemToTexts(JsonNode? system)
    {
        switch (system)
        {
            case null:
                return [];
            case JsonValue value when value.TryGetValue<string>(out var systemText):
                return systemText.Length > 0 ? [systemText] : [];
            case JsonArray blocks:
                var texts = new List<string>();
                foreach (var block in blocks)
                {
                    if (block is JsonObject obj && BridgeJson.GetString(obj, "type") == "text")
                    {
                        var blockText = BridgeJson.GetString(obj, "text") ?? "";
                        if (blockText.Length > 0)
                        {
                            texts.Add(blockText);
                        }
                    }
                }

                return texts;
            default:
                throw new BridgeRequestException("invalid request field in bridged request (system: expected a string or a list of text blocks)");
        }
    }

    /// <summary>Port of <c>generate_config_from_anthropic</c> (the fields this port's <see cref="GenerateConfig"/> models).</summary>
    public static GenerateConfig GenerateConfigFromAnthropic(JsonObject request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var config = new GenerateConfig
        {
            MaxTokens = BridgeJson.GetInt(request, "max_tokens"),
            StopSeqs = BridgeJson.GetStringList(request, "stop_sequences"),
            Temperature = BridgeJson.GetDouble(request, "temperature"),
            TopK = BridgeJson.GetInt(request, "top_k"),
            TopP = BridgeJson.GetDouble(request, "top_p"),
        };

        var thinking = BridgeJson.RequestObject(request["thinking"], "thinking");
        if (thinking is not null && BridgeJson.GetString(thinking, "type") == "enabled")
        {
            config = config with { ReasoningTokens = BridgeJson.GetInt(thinking, "budget_tokens") };
        }

        var outputConfig = BridgeJson.RequestObject(request["output_config"], "output_config");
        if (outputConfig is not null && BridgeJson.GetString(outputConfig, "effort") is { } effort)
        {
            config = config with { ReasoningEffort = effort };
        }

        var toolChoice = BridgeJson.RequestObject(request["tool_choice"], "tool_choice");
        if (toolChoice is not null && BridgeJson.GetBool(toolChoice, "disable_parallel_tool_use") == true)
        {
            config = config with { ParallelToolCalls = false };
        }

        return config;
    }

    /// <summary>
    /// Port of <c>tools_from_anthropic_tools</c> for client tools (<c>{name, description, input_schema}</c>).
    /// Server and built-in tools (a <c>type</c> such as <c>web_search_20250305</c> or <c>bash_20250124</c>) are
    /// dropped: this port has no host tools to substitute for them and never forwards server tools.
    /// </summary>
    public static IReadOnlyList<ToolInfo> ToolsFromAnthropicTools(JsonNode? tools)
    {
        if (tools is null)
        {
            return [];
        }

        var result = new List<ToolInfo>();
        foreach (var node in BridgeJson.RequireArray(tools, "tools"))
        {
            if (node is not JsonObject tool || !tool.ContainsKey("input_schema"))
            {
                continue;
            }

            var name = BridgeJson.GetString(tool, "name") ?? throw new BridgeRequestException("invalid request field in bridged request (tools[].name: expected a string)");
            result.Add(new ToolInfo(name, BridgeJson.GetString(tool, "description") ?? "")
            {
                Parameters = BridgeJson.ToolParamsFromSchema(tool["input_schema"] as JsonObject),
            });
        }

        return result;
    }

    /// <summary>Port of <c>tool_choice_from_anthropic_tool_choice</c>: <c>auto</c> | <c>any</c> | <c>none</c> | <c>tool(name)</c>; null when absent.</summary>
    public static ToolChoice? ToolChoiceFromAnthropicToolChoice(JsonNode? toolChoice)
    {
        var obj = BridgeJson.RequestObject(toolChoice, "tool_choice");
        if (obj is null)
        {
            return null;
        }

        var type = BridgeJson.GetString(obj, "type");
        switch (type)
        {
            case "any":
                return ToolChoice.Any;
            case "auto":
                return ToolChoice.Auto;
            case "none":
                return ToolChoice.None;
            case "tool":
                var name = BridgeJson.GetString(obj, "name")
                    ?? throw new BridgeRequestException("invalid request field in bridged request (tool_choice.name: input should be a string)");
                return new ToolFunction(name);
            default:
                throw new BridgeRequestException($"invalid request field in bridged request (tool_choice.type: expected one of 'any', 'auto', 'none', 'tool', got {(type is null ? "None" : $"'{type}'")})");
        }
    }

    /// <summary>
    /// Port of <c>messages_from_anthropic_input</c>: assistant blocks become one assistant message (text,
    /// thinking, tool_use → tool calls); a user message's <c>tool_result</c> blocks each become a
    /// <see cref="ChatMessageTool"/> (named from the preceding tool_use ids) and its remaining blocks a
    /// <see cref="ChatMessageUser"/>, in wire order.
    /// </summary>
    public static IReadOnlyList<ChatMessage> MessagesFromAnthropicInput(JsonArray input, IReadOnlyList<ToolInfo> tools)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(tools);
        var messages = new List<ChatMessage>();
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var node in input)
        {
            var param = node as JsonObject ?? throw new BridgeRequestException("invalid request field in bridged request (messages[]: expected an object)");
            var role = BridgeJson.GetString(param, "role");
            var content = param["content"];
            switch (role)
            {
                case "assistant":
                    var blocks = content is JsonValue text && text.TryGetValue<string>(out var s)
                        ? new JsonArray(new JsonObject { ["type"] = "text", ["text"] = s })
                        : BridgeJson.RequireArray(content, "messages[].content");
                    var (assistantContent, toolCalls) = ContentAndToolCallsFromAssistantBlocks(blocks, tools);
                    messages.Add(new ChatMessageAssistant(MessageContent.FromItems(assistantContent), toolCalls));
                    foreach (var call in toolCalls ?? [])
                    {
                        toolNames[call.Id] = call.Function;
                    }

                    break;

                case "user":
                    if (content is JsonValue userText && userText.TryGetValue<string>(out var userString))
                    {
                        messages.Add(new ChatMessageUser(userString));
                        break;
                    }

                    var pending = new List<Content>();
                    foreach (var block in BridgeJson.RequireArray(content, "messages[].content"))
                    {
                        if (block is not JsonObject c)
                        {
                            continue;
                        }

                        var type = BridgeJson.GetString(c, "type");
                        if (type == "tool_result")
                        {
                            FlushPending(messages, pending);
                            var toolUseId = BridgeJson.GetString(c, "tool_use_id") ?? "";
                            var resultContent = c["content"];
                            MessageContent toolContent;
                            string errorText;
                            switch (resultContent)
                            {
                                case null:
                                    toolContent = "";
                                    errorText = "";
                                    break;
                                case JsonValue value when value.TryGetValue<string>(out var resultText):
                                    toolContent = resultText;
                                    errorText = resultText;
                                    break;
                                default:
                                    var resultBlocks = BridgeJson.RequireArray(resultContent, "tool_result.content");
                                    var items = resultBlocks.OfType<JsonObject>().Select(ContentBlockToContent).ToList();
                                    toolContent = MessageContent.FromItems(items);
                                    // Python's message is str(list) (the repr of the blocks); the text blocks read better, and
                                    // a list without any falls back to the blocks' JSON so the message is never empty.
                                    errorText = string.Join("\n", items.OfType<ContentText>().Select(t => t.Text));
                                    if (errorText.Length == 0 && resultBlocks.Count > 0)
                                    {
                                        errorText = PythonJson.Dumps(resultBlocks);
                                    }

                                    break;
                            }

                            var isError = BridgeJson.GetBool(c, "is_error") == true;
                            messages.Add(new ChatMessageTool(toolContent, toolUseId, toolNames.GetValueOrDefault(toolUseId), isError ? new ToolCallError("unknown", errorText) : null));
                        }
                        else if (type is "text" or "image" or "document")
                        {
                            pending.Add(ContentBlockToContent(c));
                        }
                        else
                        {
                            throw new BridgeRequestException($"Unexpected input parameter: {PythonJson.Dumps(c)}");
                        }
                    }

                    FlushPending(messages, pending);
                    break;

                case "system":
                    messages.AddRange(SystemToTexts(content).Select(t => new ChatMessageSystem(t)));
                    break;

                default:
                    throw new BridgeRequestException($"Unexpected message role: {role}");
            }
        }

        return messages;
    }

    /// <summary>Port of <c>content_block_to_content</c> for <c>text</c>, <c>image</c> (base64 → data URI, url as-is) and text-bearing <c>document</c> blocks.</summary>
    public static Content ContentBlockToContent(JsonObject block)
    {
        ArgumentNullException.ThrowIfNull(block);
        switch (BridgeJson.GetString(block, "type"))
        {
            case "text":
                return new ContentText(BridgeJson.GetString(block, "text") ?? "");
            case "image":
                var source = BridgeJson.RequestObject(block["source"], "image.source") ?? throw new BridgeRequestException("invalid request field in bridged request (image.source: expected an object)");
                return BridgeJson.GetString(source, "type") switch
                {
                    "base64" => new ContentImage(InlineMedia.AsDataUri(BridgeJson.GetString(source, "media_type") ?? "image/png", BridgeJson.GetString(source, "data") ?? "")),
                    "url" => new ContentImage(BridgeJson.GetString(source, "url") ?? ""),
                    var imageType => throw new BridgeRequestException($"Unsupported image source type: {imageType}"),
                };
            case "document":
                var docSource = BridgeJson.RequestObject(block["source"], "document.source") ?? throw new BridgeRequestException("invalid request field in bridged request (document.source: expected an object)");
                switch (BridgeJson.GetString(docSource, "type"))
                {
                    case "text":
                        return new ContentText(BridgeJson.GetString(docSource, "data") ?? "");
                    case "content":
                        var inner = docSource["content"];
                        if (inner is JsonValue innerText && innerText.TryGetValue<string>(out var innerString))
                        {
                            return new ContentText(innerString);
                        }

                        var first = BridgeJson.RequireArray(inner, "document.source.content").OfType<JsonObject>().FirstOrDefault()
                            ?? throw new BridgeRequestException("Unsupported document source: empty content");
                        return ContentBlockToContent(first);
                    case var documentType:
                        // ContentDocument has no counterpart in this port's Content union.
                        throw new BridgeRequestException($"Unsupported document source type: {documentType}");
                }

            case var other:
                throw new BridgeRequestException($"Unsupported content block type: {other}");
        }
    }

    /// <summary>Port of <c>anthropic_stop_reason</c>.</summary>
    public static string AnthropicStopReason(StopReason stopReason) => stopReason switch
    {
        StopReason.Stop => "end_turn",
        StopReason.MaxTokens => "max_tokens",
        StopReason.ModelLength => "max_tokens",
        StopReason.ToolCalls => "tool_use",
        StopReason.ContentFilter => "refusal",
        _ => "end_turn",
    };

    /// <summary>Port of <c>anthropic_usage</c>.</summary>
    public static JsonObject AnthropicUsage(ModelUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var obj = new JsonObject
        {
            ["input_tokens"] = usage.InputTokens,
            ["output_tokens"] = usage.OutputTokens,
            ["cache_creation_input_tokens"] = usage.InputTokensCacheWrite,
            ["cache_read_input_tokens"] = usage.InputTokensCacheRead,
        };
        if (usage.ReasoningTokens is { } thinking)
        {
            obj["output_tokens_details"] = new JsonObject { ["thinking_tokens"] = thinking };
        }

        return obj;
    }

    /// <summary>
    /// Port of <c>assistant_message_blocks</c> / the Anthropic provider's <c>message_param_content</c>: text and
    /// reasoning content in order, then a <c>tool_use</c> block per tool call. Only signed reasoning becomes a
    /// <c>thinking</c> block and only signed redacted reasoning a <c>redacted_thinking</c> block; reasoning that
    /// did not come from Anthropic (no signature) is degraded to a text block carrying <c>ContentReasoning.text</c>
    /// (<c>&lt;think&gt;…&lt;/think&gt;</c>), because the Messages API rejects a thinking block whose signature is
    /// not its own and a served non-Anthropic model produces none.
    /// </summary>
    public static JsonArray AssistantMessageBlocks(ChatMessageAssistant message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var blocks = new JsonArray();
        if (message.Content.IsString)
        {
            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = message.Content.Text is { Length: > 0 } text ? text : NoContent });
        }
        else
        {
            foreach (var content in message.ContentList)
            {
                switch (content)
                {
                    case ContentText text:
                        blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text.Length > 0 ? text.Text : NoContent });
                        break;
                    case ContentReasoning { Redacted: true, Signature: not null } redacted:
                        blocks.Add(new JsonObject { ["type"] = "redacted_thinking", ["data"] = redacted.Signature });
                        break;
                    case ContentReasoning { Redacted: false, Signature: { Length: > 0 } } reasoning:
                        blocks.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = reasoning.Reasoning, ["signature"] = reasoning.Signature });
                        break;
                    case ContentReasoning unsigned:
                        blocks.Add(new JsonObject { ["type"] = "text", ["text"] = ReasoningText(unsigned) });
                        break;
                }
            }
        }

        foreach (var call in message.ToolCalls ?? [])
        {
            blocks.Add(new JsonObject { ["type"] = "tool_use", ["id"] = call.Id, ["name"] = call.Function, ["input"] = call.Arguments.DeepClone() });
        }

        return blocks;
    }

    /// <summary>Port of <c>ContentReasoning.text</c>: the reasoning (nothing when redacted) wrapped in think tags.</summary>
    public static string ReasoningText(ContentReasoning reasoning)
    {
        ArgumentNullException.ThrowIfNull(reasoning);
        return $"<think>{(reasoning.Redacted ? "" : reasoning.Reasoning)}</think>";
    }

    /// <summary>The response half of <c>inspect_anthropic_api_request_impl</c>: the Messages API <c>message</c> object for an output, presented as <paramref name="model"/>.</summary>
    public static JsonObject ResponseFromOutput(ModelOutput output, string model)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(model);
        return new JsonObject
        {
            ["id"] = $"msg_{ShortUuid.Generate()}",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = model,
            ["content"] = AssistantMessageBlocks(output.Message),
            ["stop_reason"] = AnthropicStopReason(output.StopReason),
            ["stop_sequence"] = null,
            ["usage"] = AnthropicUsage(output.Usage ?? new ModelUsage()),
        };
    }

    /// <summary>
    /// Port of the proxy's <c>stream_response</c>: <c>message_start</c> (empty content, input usage), per block
    /// <c>content_block_start</c> / <c>content_block_delta</c> / <c>content_block_stop</c> (a text or thinking
    /// block streams as one delta; a tool_use input as one <c>input_json_delta</c>), then <c>message_delta</c>
    /// with the stop reason and output usage, and <c>message_stop</c>.
    /// </summary>
    public static IReadOnlyList<SseEvent> StreamEvents(JsonObject message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var events = new List<SseEvent>();
        var usage = message["usage"] as JsonObject ?? new JsonObject();

        var startUsage = new JsonObject
        {
            ["input_tokens"] = usage["input_tokens"]?.DeepClone() ?? 0,
            ["output_tokens"] = 0,
            ["cache_creation_input_tokens"] = usage["cache_creation_input_tokens"]?.DeepClone(),
            ["cache_read_input_tokens"] = usage["cache_read_input_tokens"]?.DeepClone(),
        };
        events.Add(new SseEvent("message_start", new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = message["id"]?.DeepClone(),
                ["type"] = "message",
                ["role"] = "assistant",
                ["content"] = new JsonArray(),
                ["model"] = message["model"]?.DeepClone(),
                ["stop_reason"] = null,
                ["stop_sequence"] = null,
                ["usage"] = startUsage,
            },
        }));

        var index = 0;
        foreach (var node in message["content"] as JsonArray ?? [])
        {
            if (node is not JsonObject block)
            {
                continue;
            }

            switch (BridgeJson.GetString(block, "type"))
            {
                case "text":
                    events.Add(BlockStart(index, new JsonObject { ["type"] = "text", ["text"] = "" }));
                    events.Add(BlockDelta(index, new JsonObject { ["type"] = "text_delta", ["text"] = block["text"]?.DeepClone() ?? "" }));
                    events.Add(BlockStop(index));
                    break;
                case "thinking":
                    events.Add(BlockStart(index, new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "" }));
                    events.Add(BlockDelta(index, new JsonObject { ["type"] = "thinking_delta", ["thinking"] = block["thinking"]?.DeepClone() ?? "" }));
                    if (BridgeJson.GetString(block, "signature") is { Length: > 0 } signature)
                    {
                        events.Add(BlockDelta(index, new JsonObject { ["type"] = "signature_delta", ["signature"] = signature }));
                    }

                    events.Add(BlockStop(index));
                    break;
                case "redacted_thinking":
                    events.Add(BlockStart(index, new JsonObject { ["type"] = "redacted_thinking", ["data"] = block["data"]?.DeepClone() ?? "" }));
                    events.Add(BlockStop(index));
                    break;
                case "tool_use":
                    events.Add(BlockStart(index, new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = block["id"]?.DeepClone(),
                        ["name"] = block["name"]?.DeepClone(),
                        ["input"] = new JsonObject(),
                    }));
                    events.Add(BlockDelta(index, new JsonObject
                    {
                        ["type"] = "input_json_delta",
                        ["partial_json"] = PythonJson.Dumps(block["input"] ?? new JsonObject()),
                    }));
                    events.Add(BlockStop(index));
                    break;
                default:
                    continue;
            }

            index++;
        }

        var deltaUsage = new JsonObject { ["output_tokens"] = usage["output_tokens"]?.DeepClone() ?? 0 };
        foreach (var key in new[] { "input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens" })
        {
            if (usage.ContainsKey(key))
            {
                deltaUsage[key] = usage[key]?.DeepClone();
            }
        }

        events.Add(new SseEvent("message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject
            {
                ["stop_reason"] = message["stop_reason"]?.DeepClone(),
                ["stop_sequence"] = message["stop_sequence"]?.DeepClone(),
            },
            ["usage"] = deltaUsage,
        }));
        events.Add(new SseEvent("message_stop", new JsonObject { ["type"] = "message_stop" }));
        return events;
    }

    /// <summary>Port of the proxy's <c>_anthropic_error_body</c>.</summary>
    public static JsonObject ErrorBody(int status, string message) => new()
    {
        ["type"] = "error",
        ["error"] = new JsonObject
        {
            ["type"] = ErrorTypes.GetValueOrDefault(status, "api_error"),
            ["message"] = message,
        },
    };

    /// <summary>
    /// The <c>/v1/messages/count_tokens</c> answer: an estimate of one token per four characters of the
    /// request's <c>system</c> and <c>messages</c> (this port has no tokenizer for the served model).
    /// </summary>
    public static JsonObject CountTokens(JsonObject request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var chars = 0L;
        if (request["system"] is { } system)
        {
            chars += PythonJson.Dumps(system).Length;
        }

        if (request["messages"] is { } messages)
        {
            chars += PythonJson.Dumps(messages).Length;
        }

        return new JsonObject { ["input_tokens"] = (long)Math.Ceiling(chars / 4.0) };
    }

    private static (List<Content> Content, List<ToolCall>? ToolCalls) ContentAndToolCallsFromAssistantBlocks(JsonArray blocks, IReadOnlyList<ToolInfo> tools)
    {
        var content = new List<Content>();
        List<ToolCall>? toolCalls = null;
        foreach (var node in blocks)
        {
            if (node is not JsonObject block)
            {
                continue;
            }

            switch (BridgeJson.GetString(block, "type"))
            {
                case "text":
                    var text = BridgeJson.GetString(block, "text");
                    if (text is null)
                    {
                        continue;
                    }

                    if (tools.Count > 0)
                    {
                        // claude sometimes wraps tool-call turns in <result> tags; strip them as the provider does
                        text = text.Replace("<result>", "", StringComparison.Ordinal).Replace("</result>", "", StringComparison.Ordinal);
                    }

                    content.Add(new ContentText(text));
                    break;
                case "thinking":
                    content.Add(new ContentReasoning(BridgeJson.GetString(block, "thinking") ?? "", BridgeJson.GetString(block, "signature")));
                    break;
                case "redacted_thinking":
                    content.Add(new ContentReasoning("", BridgeJson.GetString(block, "data"), Redacted: true));
                    break;
                case "tool_use":
                    toolCalls ??= [];
                    var arguments = block["input"] is JsonObject input ? input.DeepClone().AsObject() : new JsonObject();
                    toolCalls.Add(new ToolCall(BridgeJson.GetString(block, "id") ?? "", BridgeJson.GetString(block, "name") ?? "", arguments));
                    break;
                default:
                    // server tool blocks (web_search_tool_result, server_tool_use, ...) are not modelled here
                    break;
            }
        }

        return (content, toolCalls);
    }

    private static void FlushPending(List<ChatMessage> messages, List<Content> pending)
    {
        if (pending.Count == 0)
        {
            return;
        }

        messages.Add(new ChatMessageUser(MessageContent.FromItems(pending.ToArray())));
        pending.Clear();
    }

    private static SseEvent BlockStart(int index, JsonObject contentBlock) =>
        new("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = index, ["content_block"] = contentBlock });

    private static SseEvent BlockDelta(int index, JsonObject delta) =>
        new("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = index, ["delta"] = delta });

    private static SseEvent BlockStop(int index) =>
        new("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = index });
}

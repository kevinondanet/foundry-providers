using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Tools;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Agents.Bridge;

/// <summary>
/// Port of <c>agent/_bridge/completions.py</c> (with the <c>messages_from_openai</c> / <c>openai_chat_choices</c>
/// conversions of <c>model/_openai.py</c> it relies on): an OpenAI chat completions request into Inspect
/// messages, tools, tool choice and config; a <see cref="ModelOutput"/> back into a chat completion; and the
/// synthesized chunk stream of <c>inspect_sandbox_tools/_agent_bridge/proxy.py</c> for <c>stream: true</c>.
/// </summary>
public static partial class CompletionsBridgeApi
{
    /// <summary>Port of <c>inspect_completions_api_request</c>: parse, generate through the bridge (which tracks state) and answer with a chat completion.</summary>
    public static async Task<JsonObject> HandleAsync(AgentBridge bridge, JsonObject request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(request);
        var parsed = ParseRequest(request);
        var output = await bridge.GenerateAsync(parsed.Model, parsed.Messages, parsed.Tools, parsed.ToolChoice, parsed.Config, cancellationToken).ConfigureAwait(false);
        return ResponseFromOutput(output, bridge.ResolveModel(parsed.Model).Name);
    }

    /// <summary>The request half of <c>inspect_completions_api_request</c>.</summary>
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

        var messages = MessagesFromOpenAI(BridgeJson.RequireArray(request["messages"], "messages"), model);
        var tools = ToolsFromOpenAITools(request["tools"]);
        var toolChoice = ToolChoiceFromOpenAIToolChoice(request["tool_choice"]) ?? ToolChoice.Auto;
        var config = GenerateConfigFromOpenAICompletions(request);
        return new BridgeRequest(model, messages, tools, toolChoice, config, BridgeJson.GetBool(request, "stream") ?? false);
    }

    /// <summary>
    /// Port of <c>messages_from_openai</c>: system/developer, user, assistant (string content may smuggle
    /// reasoning in a <c>&lt;think&gt;</c> tag; <c>tool_calls</c> become tool calls) and tool messages, after the
    /// <c>openai_assistant_message_reducer</c> fix-up for scaffolds that split an assistant turn around its tool results.
    /// </summary>
    public static IReadOnlyList<ChatMessage> MessagesFromOpenAI(JsonArray messages, string? model = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var reduced = new List<JsonObject>();
        foreach (var node in messages)
        {
            var message = node as JsonObject ?? throw new BridgeRequestException("invalid request field in bridged request (messages[]: expected an object)");
            AssistantMessageReducer(reduced, message);
        }

        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new List<ChatMessage>();
        foreach (var message in reduced)
        {
            var role = BridgeJson.GetString(message, "role");
            var content = message["content"];
            switch (role)
            {
                case "system":
                case "developer":
                    result.Add(new ChatMessageSystem(ContentFromOpenAIContent(content)));
                    break;
                case "user":
                    result.Add(new ChatMessageUser(ContentFromOpenAIContent(content)));
                    break;
                case "assistant":
                    result.Add(AssistantFromOpenAI(message, model, toolNames));
                    break;
                case "tool":
                    var toolCallId = BridgeJson.GetString(message, "tool_call_id") ?? "";
                    MessageContent toolContent = content is JsonValue value && value.TryGetValue<string>(out var text)
                        ? StripThinkTag(text)
                        : ContentFromOpenAIContent(content);
                    result.Add(new ChatMessageTool(toolContent, toolCallId, toolNames.GetValueOrDefault(toolCallId, "")));
                    break;
                default:
                    throw new BridgeRequestException($"Unexpected message param type: {role}");
            }
        }

        return result;
    }

    /// <summary>Port of <c>content_from_openai</c> for one content part (<c>text</c>, <c>image_url</c>, <c>refusal</c>, <c>reasoning</c>).</summary>
    public static IReadOnlyList<Content> ContentFromOpenAI(JsonObject part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var type = BridgeJson.GetString(part, "type");
        if (type is null && part.Count == 1)
        {
            // some providers omit the type tag and use "object-with-a-single-field" encoding
            type = part.First().Key;
        }

        switch (type)
        {
            case "text":
                return [new ContentText(BridgeJson.GetString(part, "text") ?? "")];
            case "reasoning":
                return [new ContentReasoning(BridgeJson.GetString(part, "reasoning") ?? "")];
            case "image_url":
                var image = BridgeJson.RequestObject(part["image_url"], "image_url") ?? throw new BridgeRequestException("invalid request field in bridged request (image_url: expected an object)");
                return [new ContentImage(BridgeJson.GetString(image, "url") ?? "", BridgeJson.GetString(image, "detail") ?? "auto")];
            case "refusal":
                return [new ContentText(BridgeJson.GetString(part, "refusal") ?? "") { Refusal = true }];
            default:
                throw new BridgeRequestException($"Unexpected content type '{type}' in message.");
        }
    }

    /// <summary>Port of <c>tools_from_openai_tools</c>: only <c>function</c> tools are supported.</summary>
    public static IReadOnlyList<ToolInfo> ToolsFromOpenAITools(JsonNode? tools)
    {
        if (tools is null)
        {
            return [];
        }

        var result = new List<ToolInfo>();
        foreach (var node in BridgeJson.RequireArray(tools, "tools"))
        {
            if (node is not JsonObject tool)
            {
                continue;
            }

            if (BridgeJson.GetString(tool, "type") != "function")
            {
                throw new BridgeRequestException("\"custom\" tool calls are not supported");
            }

            var function = BridgeJson.RequestObject(tool["function"], "tools[].function") ?? throw new BridgeRequestException("invalid request field in bridged request (tools[].function: expected an object)");
            var name = BridgeJson.GetString(function, "name") ?? throw new BridgeRequestException("invalid request field in bridged request (tools[].function.name: expected a string)");
            result.Add(new ToolInfo(name, BridgeJson.GetString(function, "description") ?? "")
            {
                Parameters = BridgeJson.ToolParamsFromSchema(function["parameters"] as JsonObject),
            });
        }

        return result;
    }

    /// <summary>Port of <c>tool_choice_from_openai_tool_choice</c>: <c>auto</c> | <c>none</c> | <c>required</c> (→ any) | <c>{type: function, function: {name}}</c>.</summary>
    public static ToolChoice? ToolChoiceFromOpenAIToolChoice(JsonNode? toolChoice)
    {
        switch (toolChoice)
        {
            case null:
                return null;
            case JsonValue value when value.TryGetValue<string>(out var preset):
                return preset switch
                {
                    "auto" => ToolChoice.Auto,
                    "none" => ToolChoice.None,
                    "required" => ToolChoice.Any,
                    _ => throw new BridgeRequestException($"invalid request field in bridged request (tool_choice: expected one of 'auto', 'none', 'required', got '{preset}')"),
                };
            case JsonObject obj:
                if (BridgeJson.GetString(obj, "type") != "function")
                {
                    throw new BridgeRequestException("\"custom\" tool calls are not supported");
                }

                var function = BridgeJson.RequestObject(obj["function"], "tool_choice.function");
                var name = function is null ? null : BridgeJson.GetString(function, "name");
                return new ToolFunction(name ?? throw new BridgeRequestException("invalid request field in bridged request (tool_choice.function.name: expected a string)"));
            default:
                throw new BridgeRequestException("invalid request field in bridged request (tool_choice: expected a string or an object)");
        }
    }

    /// <summary>Port of <c>generate_config_from_openai_completions</c> (the fields this port's <see cref="GenerateConfig"/> models).</summary>
    public static GenerateConfig GenerateConfigFromOpenAICompletions(JsonObject request)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<string>? stop = request["stop"] switch
        {
            JsonValue value when value.TryGetValue<string>(out var single) && single.Length > 0 => [single],
            JsonArray => BridgeJson.GetStringList(request, "stop"),
            _ => null,
        };
        return new GenerateConfig
        {
            MaxTokens = BridgeJson.GetInt(request, "max_completion_tokens") ?? BridgeJson.GetInt(request, "max_tokens"),
            TopP = BridgeJson.GetDouble(request, "top_p"),
            Temperature = BridgeJson.GetDouble(request, "temperature"),
            StopSeqs = stop,
            FrequencyPenalty = BridgeJson.GetDouble(request, "frequency_penalty"),
            PresencePenalty = BridgeJson.GetDouble(request, "presence_penalty"),
            Seed = BridgeJson.GetInt(request, "seed"),
            NumChoices = BridgeJson.GetInt(request, "n"),
            Logprobs = BridgeJson.GetBool(request, "logprobs"),
            TopLogprobs = BridgeJson.GetInt(request, "top_logprobs"),
            ParallelToolCalls = BridgeJson.GetBool(request, "parallel_tool_calls"),
            ReasoningEffort = BridgeJson.GetString(request, "reasoning_effort"),
        };
    }

    /// <summary>Port of <c>openai_finish_reason</c>.</summary>
    public static string OpenAIFinishReason(StopReason stopReason) => stopReason switch
    {
        StopReason.Stop => "stop",
        StopReason.ToolCalls => "tool_calls",
        StopReason.ContentFilter => "content_filter",
        StopReason.ModelLength => "length",
        _ => "stop",
    };

    /// <summary>Port of <c>openai_completion_usage</c>: cached and cache-write tokens are folded into <c>prompt_tokens</c>.</summary>
    public static JsonObject OpenAICompletionUsage(ModelUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var cacheRead = usage.InputTokensCacheRead ?? 0;
        var cacheWrite = usage.InputTokensCacheWrite ?? 0;
        var obj = new JsonObject
        {
            ["completion_tokens"] = usage.OutputTokens,
            ["prompt_tokens"] = usage.InputTokens + cacheRead + cacheWrite,
            ["total_tokens"] = usage.TotalTokens,
        };
        if (usage.InputTokensCacheRead is not null || usage.InputTokensCacheWrite is not null)
        {
            obj["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = cacheRead, ["cache_write_tokens"] = cacheWrite };
        }

        return obj;
    }

    /// <summary>Port of <c>openai_assistant_content</c>: reasoning items are smuggled into the text as <c>&lt;think&gt;</c> tags so they survive round trips.</summary>
    public static string OpenAIAssistantContent(ChatMessageAssistant message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Content.IsString)
        {
            return message.Content.Text ?? "";
        }

        var content = "";
        foreach (var item in message.ContentList)
        {
            switch (item)
            {
                case ContentReasoning reasoning:
                    content = $"{content}\n{ReasoningToThinkTag(reasoning)}\n";
                    break;
                case ContentText text:
                    content = $"{content}\n{text.Text}";
                    break;
            }
        }

        return content;
    }

    /// <summary>Port of <c>reasoning_to_think_tag</c>.</summary>
    public static string ReasoningToThinkTag(ContentReasoning reasoning)
    {
        ArgumentNullException.ThrowIfNull(reasoning);
        var attribs = "";
        if (reasoning.Signature is not null)
        {
            attribs = $"{attribs} signature=\"{WebUtility.HtmlEncode(reasoning.Signature)}\"";
        }

        if (reasoning.Redacted)
        {
            attribs = $"{attribs} redacted=\"true\"";
        }

        return $"<think{attribs}>\n{reasoning.Reasoning}\n</think>";
    }

    /// <summary>The response half of <c>inspect_completions_api_request</c>: a <c>chat.completion</c> object (choices via <c>openai_chat_choices</c>).</summary>
    public static JsonObject ResponseFromOutput(ModelOutput output, string model)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(model);
        var choices = new JsonArray();
        for (var index = 0; index < output.Choices.Count; index++)
        {
            var choice = output.Choices[index];
            var message = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = OpenAIAssistantContent(choice.Message),
            };
            if (choice.Message.ToolCalls is { Count: > 0 } calls)
            {
                message["tool_calls"] = new JsonArray(calls.Select(call => (JsonNode?)new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Function, ["arguments"] = PythonJson.Dumps(call.Arguments) },
                }).ToArray());
            }
            else
            {
                message["tool_calls"] = null;
            }

            choices.Add(new JsonObject
            {
                ["finish_reason"] = OpenAIFinishReason(choice.StopReason),
                ["index"] = index,
                ["message"] = message,
                ["logprobs"] = null,
            });
        }

        return new JsonObject
        {
            ["id"] = ShortUuid.Generate(),
            ["choices"] = choices,
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["object"] = "chat.completion",
            ["usage"] = output.Usage is { } usage ? OpenAICompletionUsage(usage) : null,
        };
    }

    /// <summary>
    /// Port of the proxy's <c>stream_response</c> for chat completions: per choice a role chunk, a content
    /// chunk, per tool call a name chunk and an arguments chunk (index, id and type repeated on every delta,
    /// as some clients require), a final empty delta carrying <c>finish_reason</c>; then a usage-only chunk when
    /// <paramref name="includeUsage"/>. The caller appends <see cref="SseWriter.Done"/>.
    /// </summary>
    public static IReadOnlyList<JsonObject> StreamChunks(JsonObject completion, bool includeUsage)
    {
        ArgumentNullException.ThrowIfNull(completion);
        var chunks = new List<JsonObject>();
        JsonObject BaseChunk() => new()
        {
            ["id"] = completion["id"]?.DeepClone(),
            ["object"] = "chat.completion.chunk",
            ["created"] = completion["created"]?.DeepClone(),
            ["model"] = completion["model"]?.DeepClone(),
            ["choices"] = new JsonArray(),
        };

        JsonObject ChoiceChunk(int index, JsonObject delta, JsonNode? finishReason)
        {
            var chunk = BaseChunk();
            chunk["choices"] = new JsonArray(new JsonObject { ["index"] = index, ["delta"] = delta, ["finish_reason"] = finishReason });
            return chunk;
        }

        var choiceIndex = 0;
        foreach (var node in completion["choices"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject choice)
            {
                continue;
            }

            var message = choice["message"] as JsonObject;
            chunks.Add(ChoiceChunk(choiceIndex, new JsonObject { ["role"] = message?["role"]?.DeepClone() ?? "assistant" }, null));

            if (message is not null && BridgeJson.GetString(message, "content") is { Length: > 0 } content)
            {
                chunks.Add(ChoiceChunk(choiceIndex, new JsonObject { ["content"] = content }, null));
            }

            var toolCalls = message?["tool_calls"] as JsonArray;
            for (var callIndex = 0; callIndex < (toolCalls?.Count ?? 0); callIndex++)
            {
                if (toolCalls![callIndex] is not JsonObject call)
                {
                    continue;
                }

                var function = call["function"] as JsonObject;
                chunks.Add(ChoiceChunk(choiceIndex, new JsonObject
                {
                    ["tool_calls"] = new JsonArray(new JsonObject
                    {
                        ["index"] = callIndex,
                        ["id"] = call["id"]?.DeepClone(),
                        ["type"] = call["type"]?.DeepClone(),
                        ["function"] = new JsonObject { ["name"] = function?["name"]?.DeepClone() ?? "" },
                    }),
                }, null));
                chunks.Add(ChoiceChunk(choiceIndex, new JsonObject
                {
                    ["tool_calls"] = new JsonArray(new JsonObject
                    {
                        ["index"] = callIndex,
                        ["id"] = call["id"]?.DeepClone(),
                        ["type"] = call["type"]?.DeepClone(),
                        ["function"] = new JsonObject { ["arguments"] = function?["arguments"]?.DeepClone() ?? "" },
                    }),
                }, null));
            }

            chunks.Add(ChoiceChunk(choiceIndex, new JsonObject(), choice["finish_reason"]?.DeepClone()));
            choiceIndex++;
        }

        if (includeUsage && completion["usage"] is JsonObject usage)
        {
            var chunk = BaseChunk();
            chunk["usage"] = usage.DeepClone();
            chunks.Add(chunk);
        }

        return chunks;
    }

    /// <summary>Port of the proxy's <c>_openai_error_body</c>.</summary>
    public static JsonObject ErrorBody(int status, string message) => new()
    {
        ["error"] = new JsonObject
        {
            ["message"] = message,
            ["type"] = status is >= 400 and < 500 ? "invalid_request_error" : "api_error",
            ["param"] = null,
            ["code"] = null,
        },
    };

    [GeneratedRegex("<think([^>]*)>(.*?)</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkTagRegex();

    [GeneratedRegex("signature=\"([^\"]*)\"")]
    private static partial Regex SignatureAttrRegex();

    [GeneratedRegex("redacted=\"([^\"]*)\"")]
    private static partial Regex RedactedAttrRegex();

    /// <summary>Port of <c>openai_assistant_message_reducer</c>: <c>assistant[tool_calls] → tool → assistant[content]</c> is folded back into the first assistant message.</summary>
    private static void AssistantMessageReducer(List<JsonObject> messages, JsonObject message)
    {
        if (messages.Count >= 2
            && BridgeJson.GetString(message, "role") == "assistant"
            && !message.ContainsKey("tool_calls")
            && message.ContainsKey("content")
            && BridgeJson.GetString(messages[^1], "role") == "tool"
            && BridgeJson.GetString(messages[^2], "role") == "assistant"
            && messages[^2].ContainsKey("tool_calls")
            && messages[^2]["content"] is null)
        {
            messages[^2]["content"] = message["content"]?.DeepClone();
        }
        else
        {
            messages.Add(message);
        }
    }

    private static ChatMessageAssistant AssistantFromOpenAI(JsonObject message, string? model, Dictionary<string, string> toolNames)
    {
        MessageContent content;
        var rawContent = message["content"];
        if (rawContent is JsonValue value && value.TryGetValue<string>(out var text))
        {
            var (remaining, reasoning) = ParseContentWithReasoning(text);
            if (reasoning is not null)
            {
                var items = new List<Content> { reasoning };
                if (remaining.Length > 0)
                {
                    items.Add(new ContentText(remaining));
                }

                content = MessageContent.FromItems(items);
            }
            else
            {
                content = remaining;
            }
        }
        else if (rawContent is null)
        {
            var refusal = BridgeJson.GetString(message, "refusal") ?? "";
            content = refusal.Length > 0 ? MessageContent.FromItems([new ContentText(refusal) { Refusal = true }]) : "";
        }
        else
        {
            var items = new List<Content>();
            foreach (var part in BridgeJson.RequireArray(rawContent, "messages[].content").OfType<JsonObject>())
            {
                items.AddRange(ContentFromOpenAI(part));
            }

            content = MessageContent.FromItems(items);
        }

        List<ToolCall>? toolCalls = null;
        if (message["tool_calls"] is JsonArray calls)
        {
            toolCalls = [];
            foreach (var call in calls.OfType<JsonObject>())
            {
                if (BridgeJson.GetString(call, "type") != "function")
                {
                    continue;
                }

                var function = call["function"] as JsonObject;
                var id = BridgeJson.GetString(call, "id") ?? "";
                var name = function is null ? "" : BridgeJson.GetString(function, "name") ?? "";
                var arguments = function is null ? null : function["arguments"];
                var argumentsText = arguments is JsonValue argValue && argValue.TryGetValue<string>(out var s) ? s : PythonJson.Dumps(arguments);
                toolCalls.Add(ToolCallParsing.ParseToolCall(id, name, argumentsText));
                toolNames[id] = name;
            }
        }

        return new ChatMessageAssistant(content, toolCalls is { Count: > 0 } ? toolCalls : null, model, "generate");
    }

    private static MessageContent ContentFromOpenAIContent(JsonNode? content)
    {
        if (content is null)
        {
            return "";
        }

        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        var items = new List<Content>();
        foreach (var part in BridgeJson.RequireArray(content, "messages[].content").OfType<JsonObject>())
        {
            items.AddRange(ContentFromOpenAI(part));
        }

        return MessageContent.FromItems(items);
    }

    /// <summary>Port of <c>parse_content_with_reasoning</c> (signature and redacted attributes; no nested summaries).</summary>
    private static (string Remaining, ContentReasoning? Reasoning) ParseContentWithReasoning(string content)
    {
        var match = ThinkTagRegex().Match(content);
        if (!match.Success)
        {
            return (content, null);
        }

        var attrs = match.Groups[1].Value;
        var signatureMatch = SignatureAttrRegex().Match(attrs);
        var redactedMatch = RedactedAttrRegex().Match(attrs);
        var reasoning = new ContentReasoning(
            match.Groups[2].Value.Trim(),
            signatureMatch.Success ? WebUtility.HtmlDecode(signatureMatch.Groups[1].Value) : null,
            redactedMatch.Success && redactedMatch.Groups[1].Value == "true");
        var remaining = content[..match.Index] + content[(match.Index + match.Length)..];
        return (remaining.Trim(), reasoning);
    }

    private static string StripThinkTag(string content) => ParseContentWithReasoning(content).Remaining;
}

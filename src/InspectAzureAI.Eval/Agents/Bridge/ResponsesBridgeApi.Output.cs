using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Agents.Bridge;

/// <summary>A <see cref="ModelOutput"/> as a Responses API response object (port of <c>responses_impl.py:327-340, 1153-1289</c>).</summary>
public static partial class ResponsesBridgeApi
{
    /// <summary>
    /// The response body. <c>status</c> is <c>incomplete</c> exactly when <c>incomplete_details</c> is set.
    /// <c>tools</c> and <c>tool_choice</c> echo the request raw (D-R9). <c>parallel_tool_calls</c> echoes the request
    /// value, true when absent. <paramref name="modelName"/> is the served model's API name.
    /// </summary>
    public static JsonObject ResponseFromOutput(ModelOutput output, string modelName, ResponsesBridgeRequest parsed)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(modelName);
        ArgumentNullException.ThrowIfNull(parsed);
        var message = output.Message;
        var incompleteDetails = IncompleteDetails(output.StopReason);
        return new JsonObject
        {
            ["id"] = message.Id ?? ShortUuid.Generate(),
            ["object"] = "response",
            ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["status"] = incompleteDetails is null ? "completed" : "incomplete",
            ["error"] = null,
            ["incomplete_details"] = incompleteDetails,
            ["instructions"] = null,
            ["model"] = modelName,
            ["output"] = OutputItems(message, parsed.ToolNamespaces, parsed.CustomToolNames),
            ["parallel_tool_calls"] = parsed.ParallelToolCalls,
            ["tool_choice"] = parsed.ToolChoiceEcho?.DeepClone() ?? JsonValue.Create("auto"),
            ["tools"] = parsed.ToolsEcho.DeepClone(),
            ["usage"] = ResponsesUsage(output.Usage),
        };
    }

    /// <summary>
    /// Port of <c>responses_output_items_from_assistant_message</c>: content items in content order, then tool calls.
    /// <list type="bullet">
    /// <item>Text becomes a <c>message</c> with one <c>output_text</c> part; refusals too (D-R4). Empty text next to tool calls is skipped (D-R5).</item>
    /// <item>Reasoning becomes a <c>message</c> holding its think tag; native <c>reasoning</c> items are never emitted.</item>
    /// <item>A web search tool use becomes a <c>web_search_call</c>, <c>failed</c> when it carries an error.</item>
    /// <item>A call of type <c>custom</c>, or to a name in <paramref name="customToolNames"/>, becomes a <c>custom_tool_call</c>. Any other call becomes a <c>function_call</c>.</item>
    /// </list>
    /// Both call kinds carry <c>namespace</c> when <paramref name="namespaces"/> maps their name. Every item has an id and <c>status: completed</c> (D-R5).
    /// </summary>
    public static JsonArray OutputItems(ChatMessageAssistant message, IReadOnlyDictionary<string, string> namespaces, IReadOnlySet<string> customToolNames)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(namespaces);
        ArgumentNullException.ThrowIfNull(customToolNames);
        var items = new JsonArray();
        var hasToolCalls = message.ToolCalls is { Count: > 0 };
        foreach (var content in message.ContentList)
        {
            switch (content)
            {
                case ContentText text:
                    if (text.Text.Length > 0 || !hasToolCalls)
                    {
                        items.Add(OutputMessage(text.Text));
                    }

                    break;
                case ContentReasoning reasoning:
                    items.Add(OutputMessage(ThinkTags.ToThinkTag(reasoning)));
                    break;
                case ContentToolUse { ToolType: "web_search" } toolUse:
                    items.Add(new JsonObject
                    {
                        ["id"] = toolUse.Id,
                        ["type"] = "web_search_call",
                        ["status"] = toolUse.Error is { Length: > 0 } ? "failed" : "completed",
                        ["action"] = WebSearchAction(toolUse.Arguments),
                    });
                    break;
                default:
                    ProviderLogger.WarnOnce($"Assistant content of type '{content.Type}' cannot be returned on the agent bridge Responses route; it was left out of the response.");
                    break;
            }
        }

        foreach (var call in message.ToolCalls ?? [])
        {
            JsonObject item;
            if (call.Type == "custom" || customToolNames.Contains(call.Function))
            {
                item = new JsonObject
                {
                    ["id"] = "ctc_" + ShortUuid.Generate(),
                    ["type"] = "custom_tool_call",
                    ["status"] = "completed",
                    ["call_id"] = call.Id,
                    ["name"] = call.Function,
                    ["input"] = CustomToolInput(call.Arguments),
                };
            }
            else
            {
                item = new JsonObject
                {
                    ["id"] = "fc_" + ShortUuid.Generate(),
                    ["type"] = "function_call",
                    ["status"] = "completed",
                    ["call_id"] = call.Id,
                    ["name"] = call.Function,
                    ["arguments"] = PythonJson.Dumps(call.Arguments),
                };
            }

            if (namespaces.GetValueOrDefault(call.Function) is { } ns)
            {
                item["namespace"] = ns;
            }

            items.Add(item);
        }

        return items;
    }

    /// <summary>
    /// Port of <c>parse_web_search_action</c> and <c>_is_valid_openai_web_search_action</c>
    /// (<c>model/_openai_responses.py:1341-1404</c>) over the JSON text in <see cref="ContentToolUse.Arguments"/>.
    /// Null-valued keys are dropped first. A valid OpenAI action is returned as-is, except that a <c>search</c> without
    /// <c>query</c> gets <c>queries[0]</c> (or <c>""</c>) and the legacy <c>find</c> type becomes <c>find_in_page</c>.
    /// Valid actions are a <c>search</c> with <c>query</c> or <c>queries</c>, an <c>open_page</c> with <c>url</c>, and a
    /// <c>find</c> or <c>find_in_page</c> with <c>pattern</c> and <c>url</c>. Anything else becomes
    /// <c>{type: search, query}</c>, taking the object's <c>query</c> or else the raw arguments. That covers, for
    /// example, an Anthropic search, non-JSON text and a non-object.
    /// </summary>
    public static JsonObject WebSearchAction(string arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(arguments);
        }
        catch (JsonException)
        {
            parsed = null;
        }

        if (parsed is not JsonObject action)
        {
            return new JsonObject { ["type"] = "search", ["query"] = arguments };
        }

        var filtered = new JsonObject();
        foreach (var (key, value) in action)
        {
            if (value is not null && value.GetValueKind() != JsonValueKind.Null)
            {
                filtered[key] = value.DeepClone();
            }
        }

        var type = BridgeJson.GetString(filtered, "type");
        var valid = type switch
        {
            "search" => filtered.ContainsKey("query") || filtered.ContainsKey("queries"),
            "open_page" => filtered.ContainsKey("url"),
            "find" or "find_in_page" => filtered.ContainsKey("pattern") && filtered.ContainsKey("url"),
            _ => false,
        };
        if (!valid)
        {
            return new JsonObject
            {
                ["type"] = "search",
                ["query"] = filtered.TryGetPropertyValue("query", out var query) ? query!.DeepClone() : JsonValue.Create(arguments),
            };
        }

        if (type == "search" && !filtered.ContainsKey("query"))
        {
            filtered["query"] = filtered["queries"] is JsonArray { Count: > 0 } queries ? queries[0]?.DeepClone() : JsonValue.Create("");
        }

        if (type == "find")
        {
            filtered["type"] = "find_in_page";
        }

        return filtered;
    }

    /// <summary>
    /// Port of <c>responses_model_usage</c>. It is null when there is no usage. <c>input_tokens</c> includes cache reads
    /// and writes (OpenAI semantics, D-R7), with each reported under <c>input_tokens_details</c>.
    /// </summary>
    public static JsonObject? ResponsesUsage(ModelUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        var cacheRead = usage.InputTokensCacheRead ?? 0;
        var cacheWrite = usage.InputTokensCacheWrite ?? 0;
        return new JsonObject
        {
            ["input_tokens"] = usage.InputTokens + cacheRead + cacheWrite,
            ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = cacheRead, ["cache_write_tokens"] = cacheWrite },
            ["output_tokens"] = usage.OutputTokens,
            ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = usage.ReasoningTokens ?? 0 },
            ["total_tokens"] = usage.TotalTokens,
        };
    }

    /// <summary>Port of <c>responses_incomplete_details</c>: <c>content_filter</c> and <c>max_output_tokens</c>; null for every other stop reason.</summary>
    public static JsonObject? IncompleteDetails(StopReason stopReason) => stopReason switch
    {
        StopReason.ContentFilter => new JsonObject { ["reason"] = "content_filter" },
        StopReason.MaxTokens => new JsonObject { ["reason"] = "max_output_tokens" },
        _ => null,
    };

    private static JsonObject OutputMessage(string text) => new()
    {
        ["id"] = "msg_" + ShortUuid.Generate(),
        ["type"] = "message",
        ["role"] = "assistant",
        ["status"] = "completed",
        ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = "output_text",
            ["text"] = text,
            ["annotations"] = new JsonArray(),
            ["logprobs"] = new JsonArray(),
        }),
    };

    /// <summary>The raw input of a custom tool call: the <c>input</c> argument, else the first argument; a string as-is, anything else as JSON.</summary>
    private static string CustomToolInput(JsonObject arguments)
    {
        var value = arguments.TryGetPropertyValue("input", out var input)
            ? input
            : arguments.Count > 0 ? arguments.First().Value : null;
        return value switch
        {
            null => "",
            JsonValue scalar when scalar.TryGetValue<string>(out var text) => text,
            _ => PythonJson.Dumps(value),
        };
    }
}

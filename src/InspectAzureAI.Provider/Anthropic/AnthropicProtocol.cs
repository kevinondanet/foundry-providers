using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Tools;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Provider.Anthropic;

internal sealed class AnthropicProtocol(string DeploymentName, IReadOnlyDictionary<string, object?> ModelArgs, string? AnthropicBeta = null)
{
    private string ModelName => DeploymentName;
    public int? MaxTokens() => DeploymentName.Contains("claude-3") && !DeploymentName.Contains("claude-3-7") ? 4096 : 32000;
    public JsonObject BuildRequest(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, ToolChoice toolChoice, GenerateConfig config, bool streaming)
    {
        var maxTokens = config.MaxTokens ?? MaxTokens() ?? 4096;
        var request = new JsonObject
        {
            ["model"] = DeploymentName,
            ["max_tokens"] = maxTokens,
        };

        var system = string.Join("\n\n", input.OfType<ChatMessageSystem>().Select(m => m.Text).Where(s => s.Length > 0));
        if (system.Length > 0)
        {
            request["system"] = system;
        }

        request["messages"] = Messages(input);

        var choiceKind = toolChoice is ToolFunction ? "tool" : toolChoice.ToString();   // auto | any | none | tool
        // Port of partition_tools: remote MCP server markers leave `tools` and become `mcp_servers`.
        var (functionTools, mcpServers) = AnthropicRemoteMcp.PartitionTools(tools);
        if (functionTools.Count > 0 && choiceKind != "none")
        {
            request["tools"] = new JsonArray(functionTools.Select(t => (JsonNode?)(AnthropicWebSearch.ServerToolParam(t, DeploymentName) ?? new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = JsonSchemaDump.Dump(t.Parameters.ToJson(), JsonSchemaDump.JsonSchemaExtendedFields),
            })).ToArray());
            var choice = toolChoice switch
            {
                ToolFunction fn => new JsonObject { ["type"] = "tool", ["name"] = fn.Name },
                _ when choiceKind == "any" => new JsonObject { ["type"] = "any" },
                _ => new JsonObject { ["type"] = "auto" },
            };
            if (config.ParallelToolCalls == false)
            {
                choice["disable_parallel_tool_use"] = true;
            }

            request["tool_choice"] = choice;
        }

        if (mcpServers.Count > 0 && choiceKind != "none")
        {
            request["mcp_servers"] = new JsonArray(mcpServers.Select(s => (JsonNode?)AnthropicRemoteMcp.McpServerParam(s)).ToArray());
        }

        if (config.Temperature is not null) request["temperature"] = config.Temperature;
        if (config.TopP is not null) request["top_p"] = config.TopP;
        if (config.StopSeqs is not null) request["stop_sequences"] = new JsonArray(config.StopSeqs.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        foreach (var (key, value) in ThinkingParams(config, maxTokens))
        {
            request[key] = value?.DeepClone();
        }

        if (config.ResponseSchema is { } responseSchema)
        {
            request["output_format"] = ResponseFormat.AnthropicOutputFormat(responseSchema);
        }

        if (config.FallbackModels is { Count: > 0 })
        {
            ProviderLogger.WarnOnce(FallbackModelsIgnoredWarning);
        }

        if (streaming) request["stream"] = true;
        foreach (var (key, value) in ModelArgs)
        {
            request[key] = value is JsonNode node ? node.DeepClone() : JsonSerializer.SerializeToNode(value);
        }

        return request;
    }

    /// <summary>Port of the Python warning: <c>fallback_models</c> is a first-party Claude API feature and is ignored on Azure.</summary>
    public const string FallbackModelsIgnoredWarning =
        "fallback_models is only supported on the first-party Anthropic API (not bedrock/vertex/azure) and will be ignored.";

    /// <summary>
    /// The <c>anthropic-beta</c> header value for a request: the <c>anthropic_beta</c> model arg (comma separated)
    /// plus <see cref="ResponseFormat.AnthropicStructuredOutputsBeta"/> when the config carries a response schema
    /// (Python appends the beta alongside <c>output_format</c>) and <see cref="AnthropicRemoteMcp.Beta"/> when
    /// <paramref name="remoteMcp"/> (the request carries <c>mcp_servers</c>); null when there is nothing to send.
    /// </summary>
    public string? BetaHeader(GenerateConfig config, bool remoteMcp = false)
    {
        var betas = new List<string>();
        if (AnthropicBeta is { Length: > 0 })
        {
            betas.AddRange(AnthropicBeta.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        if (config.ResponseSchema is not null && !betas.Contains(ResponseFormat.AnthropicStructuredOutputsBeta))
        {
            betas.Add(ResponseFormat.AnthropicStructuredOutputsBeta);
        }

        if (remoteMcp && !betas.Contains(AnthropicRemoteMcp.Beta))
        {
            betas.Add(AnthropicRemoteMcp.Beta);
        }

        return betas.Count > 0 ? string.Join(",", betas) : null;
    }

    /// <summary>
    /// The thinking fields for the config (Claude on Foundry): nothing when neither reasoning setting is
    /// given or the effort is <c>none</c> (thinking stays off on 4.6; <c>{type: disabled}</c> is never sent
    /// because newer models reject it); <c>thinking: {type: enabled, budget_tokens}</c> for a
    /// <c>ReasoningTokens</c> budget (the deprecated 4.6 form; <c>max_tokens</c> is raised above the budget
    /// as Inspect does), otherwise <c>thinking: {type: adaptive}</c>; and <c>output_config: {effort}</c> for
    /// any effort other than <c>none</c> (<c>minimal</c> becomes <c>low</c>, the rest verbatim).
    /// </summary>
    public static JsonObject ThinkingParams(GenerateConfig config, int maxTokens)
    {
        var fields = new JsonObject();
        var effort = config.ReasoningEffort?.Trim().ToLowerInvariant();
        var budget = config.ReasoningTokens;
        if ((string.IsNullOrEmpty(effort) && budget is null) || effort == "none")
        {
            return fields;
        }

        if (budget is > 0)
        {
            fields["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = budget };
            if (maxTokens <= budget)
            {
                fields["max_tokens"] = budget + AzureAIModelApi.DefaultMaxTokens;
            }
        }
        else
        {
            fields["thinking"] = new JsonObject { ["type"] = "adaptive" };
        }

        if (!string.IsNullOrEmpty(effort))
        {
            fields["output_config"] = new JsonObject { ["effort"] = effort == "minimal" ? "low" : effort };
        }

        return fields;
    }

    /// <summary>Converts the conversation to Messages-API turns, merging consecutive same-role turns (the API requires alternation).</summary>
    public static JsonArray Messages(IReadOnlyList<ChatMessage> input)
    {
        var messages = new JsonArray();
        foreach (var message in input)
        {
            var (role, blocks) = message switch
            {
                ChatMessageSystem => ("", new JsonArray()),
                ChatMessageUser user => ("user", ContentBlocks(user.ContentList)),
                ChatMessageAssistant assistant => ("assistant", AssistantBlocks(assistant)),
                ChatMessageTool tool => ("user", new JsonArray(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = tool.ToolCallId ?? "",
                    ["content"] = tool.Error is not null
                        ? JsonValue.Create($"Error: {tool.Error.Message}")
                        : tool.Content.IsString ? JsonValue.Create(tool.Text) : ContentBlocks(tool.ContentList, includeCitations: false),
                    ["is_error"] = tool.Error is not null,
                })),
                _ => ("", new JsonArray()),
            };
            if (role.Length == 0 || blocks.Count == 0)
            {
                continue;
            }

            if (messages.Count > 0 && messages[^1]!["role"]!.ToString() == role)
            {
                var existing = messages[^1]!["content"]!.AsArray();
                foreach (var block in blocks.ToList())
                {
                    blocks.Remove(block);
                    existing.Add(block);
                }
            }
            else
            {
                messages.Add(new JsonObject { ["role"] = role, ["content"] = blocks });
            }
        }

        return messages;
    }

    /// <summary>
    /// The assistant turn on the wire: thinking blocks first (replayed unchanged with their signature, as
    /// the Messages API requires on later turns), then text and images, then <c>tool_use</c>. A reasoning
    /// item without a signature cannot be replayed and is dropped with a one-time warning.
    /// </summary>
    internal static string BlockOrderKey(JsonNode? block) => $"{block?["type"]}:{block?["id"] ?? block?["signature"] ?? block?["data"] ?? block?["text"]}";

    private static JsonArray AssistantBlocks(ChatMessageAssistant assistant)
    {
        var blocks = ContentBlocks(assistant.ContentList);
        var position = 0;
        foreach (var reasoning in assistant.ContentList.OfType<ContentReasoning>())
        {
            if (reasoning.Redacted)
            {
                blocks.Insert(position++, new JsonObject { ["type"] = "redacted_thinking", ["data"] = reasoning.Signature ?? "" });
            }
            else if (reasoning.Signature is { Length: > 0 })
            {
                blocks.Insert(position++, new JsonObject { ["type"] = "thinking", ["thinking"] = reasoning.Reasoning, ["signature"] = reasoning.Signature });
            }
            else
            {
                ProviderLogger.WarnOnce("Anthropic on Azure: a reasoning block without a signature cannot be replayed and was left out of the conversation.");
            }
        }

        foreach (var call in assistant.ToolCalls ?? [])
        {
            blocks.Add(new JsonObject { ["type"] = "tool_use", ["id"] = call.Id, ["name"] = call.Function, ["input"] = call.Arguments.DeepClone() });
        }

        if (assistant.Metadata?.TryGetValue("anthropic_block_order", out var recorded) == true && JsonSerializer.SerializeToNode(recorded) is JsonArray order)
        {
            var keys = order.Select(n => n!.ToString()).ToList();
            return new JsonArray(blocks.OrderBy(n => { var i = keys.IndexOf(BlockOrderKey(n)); return i < 0 ? int.MaxValue : i; }).Select(n => n!.DeepClone()).ToArray());
        }
        return blocks;
    }

    private static JsonArray ContentBlocks(IReadOnlyList<Content> items, bool includeCitations = true)
    {
        var blocks = new JsonArray();
        foreach (var item in items)
        {
            switch (item)
            {
                case ContentText text when text.Text.Length > 0:
                    var textBlock = new JsonObject { ["type"] = "text", ["text"] = text.Text };
                    // The Messages API accepts citations on conversation text, but not inside tool results.
                    if (includeCitations)
                    {
                        AnthropicWebSearch.AddCitations(textBlock, text.Citations);
                    }

                    blocks.Add(textBlock);
                    break;
                case ContentToolUse toolUse:
                    var replayBlocks = toolUse.ToolType == AnthropicRemoteMcp.ToolType ? AnthropicRemoteMcp.ReplayBlocks(toolUse) : AnthropicWebSearch.ReplayBlocks(toolUse);
                    foreach (var replayed in replayBlocks)
                    {
                        blocks.Add(replayed);
                    }

                    break;
                case ContentImage image:
                    blocks.Add(ImageBlock(image));
                    break;
                case ContentAudio or ContentVideo:
                    throw new InvalidOperationException("Anthropic on Azure does not support audio or video inputs.");
            }
        }

        return blocks;
    }

    private static JsonObject ImageBlock(ContentImage image)
    {
        if (image.Image.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || image.Image.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "url", ["url"] = image.Image } };
        }

        var dataUri = InlineMedia.IsDataUri(image.Image) ? image.Image : InlineMedia.InlineMediaDataUri(image.Image, "image");
        return new JsonObject
        {
            ["type"] = "image",
            ["source"] = new JsonObject
            {
                ["type"] = "base64",
                ["media_type"] = InlineMedia.DataUriMimeType(dataUri) ?? "image/png",
                ["data"] = InlineMedia.DataUriToBase64(dataUri),
            },
        };
    }

    /// <summary>Builds the <see cref="ModelOutput"/> from a Messages response (or an accumulated stream).</summary>
    public ModelOutput ParseMessage(JsonObject message)
    {
        var items = new List<Content>();
        var toolCalls = new List<ToolCall>();
        var pendingServerToolUses = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var pendingMcpToolUses = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var block in message["content"]?.AsArray() ?? [])
        {
            switch (block?["type"]?.ToString())
            {
                case "text":
                    items.Add(new ContentText(block["text"]?.ToString() ?? "") { Citations = AnthropicWebSearch.ReadCitations(block.AsObject()) });
                    break;
                case "server_tool_use":
                    pendingServerToolUses[block["id"]?.ToString() ?? ""] = block.AsObject();
                    break;
                case "web_search_tool_result":
                    var toolUseId = block["tool_use_id"]?.ToString() ?? "";
                    if (!pendingServerToolUses.Remove(toolUseId, out var serverToolUse))
                    {
                        throw new ServiceResponseException("web_search_tool_result without a previous server_tool_use block.");
                    }

                    items.Add(AnthropicWebSearch.ToContentToolUse(serverToolUse, block.AsObject()));
                    break;
                case "mcp_tool_use":
                    pendingMcpToolUses[block["id"]?.ToString() ?? ""] = block.AsObject();
                    break;
                case "mcp_tool_result":
                    if (!pendingMcpToolUses.Remove(block["tool_use_id"]?.ToString() ?? "", out var mcpToolUse))
                    {
                        throw new ServiceResponseException(AnthropicRemoteMcp.OrphanResultError);
                    }

                    items.Add(AnthropicRemoteMcp.ToContentToolUse(mcpToolUse, block.AsObject()));
                    break;
                case "thinking":
                    items.Add(new ContentReasoning(block["thinking"]?.ToString() ?? "", block["signature"]?.ToString()));
                    break;
                case "redacted_thinking":
                    items.Add(new ContentReasoning("", block["data"]?.ToString(), Redacted: true));
                    break;
                case "tool_use":
                    toolCalls.Add(ToolCallParsing.ParseToolCall(
                        block["id"]?.ToString() ?? "", block["name"]?.ToString() ?? "", PythonJson.Dumps(block["input"] ?? new JsonObject())));
                    break;
            }
        }

        var stopReason = message["stop_reason"]?.ToString() switch
        {
            "end_turn" or "stop_sequence" => StopReason.Stop,
            "tool_use" => StopReason.ToolCalls,
            "max_tokens" => StopReason.MaxTokens,
            "refusal" => StopReason.ContentFilter,
            _ => StopReason.Unknown,
        };
        var details = stopReason == StopReason.ContentFilter ? new StopDetails { Type = "refusal" } : null;
        var content = items.Count == 0 ? MessageContent.FromString("") : MessageContent.FromItems(items);
        var assistant = new ChatMessageAssistant(content, toolCalls.Count > 0 ? toolCalls : null, message["model"]?.ToString(), "generate");

        ModelUsage? usage = null;
        if (message["usage"] is JsonObject u)
        {
            var inputTokens = u["input_tokens"]?.GetValue<int>() ?? 0;
            var outputTokens = u["output_tokens"]?.GetValue<int>() ?? 0;
            int? cacheWrite = u["cache_creation_input_tokens"] is JsonValue cw && cw.TryGetValue<int>(out var w) ? w : null;
            int? cacheRead = u["cache_read_input_tokens"] is JsonValue cr && cr.TryGetValue<int>(out var r) ? r : null;
            usage = new ModelUsage(inputTokens, outputTokens, inputTokens + outputTokens + (cacheWrite ?? 0) + (cacheRead ?? 0))
            {
                InputTokensCacheWrite = cacheWrite,
                InputTokensCacheRead = cacheRead,
            };
        }

        return new ModelOutput
        {
            Model = message["model"]?.ToString() ?? DeploymentName,
            Choices = [new ChatCompletionChoice(assistant, stopReason, details)],
            Usage = usage,
        };
    }

    /// <summary>Folds Messages stream events into one message document, delivering text and tool-call deltas as they arrive.</summary>
    public static async Task<JsonObject> AccumulateAsync(IAsyncEnumerable<JsonObject> events, CancellationToken cancellationToken = default)
    {
        var message = new JsonObject { ["content"] = new JsonArray() };
        var blocks = new SortedDictionary<int, (JsonObject Block, StringBuilder Text, StringBuilder Json)>();
        await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            switch (evt["type"]?.ToString())
            {
                case "message_start" when evt["message"] is JsonObject start:
                    foreach (var (key, value) in start)
                    {
                        if (key != "content") message[key] = value?.DeepClone();
                    }

                    break;
                case "content_block_start" when evt["content_block"] is JsonObject block:
                    blocks[evt["index"]?.GetValue<int>() ?? blocks.Count] = (block.DeepClone().AsObject(), new StringBuilder(), new StringBuilder());
                    break;
                case "content_block_delta" when evt["delta"] is JsonObject delta:
                {
                    var index = evt["index"]?.GetValue<int>() ?? 0;
                    if (!blocks.TryGetValue(index, out var entry)) break;
                    switch (delta["type"]?.ToString())
                    {
                        case "text_delta":
                            var text = delta["text"]?.ToString() ?? "";
                            entry.Text.Append(text);
                            await ModelStreamObserver.ReportModelStreamDeltaAsync(new StreamTextEvent(text)).ConfigureAwait(false);
                            break;
                        case "input_json_delta":
                            var partial = delta["partial_json"]?.ToString() ?? "";
                            entry.Json.Append(partial);
                            if (entry.Block["type"]?.ToString() == "tool_use")
                            {
                                await ModelStreamObserver.ReportModelStreamDeltaAsync(new StreamToolCallEvent(entry.Block["id"]?.ToString(), entry.Block["name"]?.ToString(), partial)).ConfigureAwait(false);
                            }

                            break;
                        case "citations_delta" when delta["citation"] is JsonObject citation:
                            if (entry.Block["citations"] is not JsonArray citations)
                            {
                                citations = new JsonArray();
                                entry.Block["citations"] = citations;
                            }

                            citations.Add(citation.DeepClone());
                            break;
                        case "thinking_delta":
                            var thinking = delta["thinking"]?.ToString() ?? "";
                            entry.Text.Append(thinking);
                            await ModelStreamObserver.ReportModelStreamDeltaAsync(new StreamReasoningEvent(thinking)).ConfigureAwait(false);
                            break;
                        case "signature_delta":
                            entry.Block["signature"] = (entry.Block["signature"]?.ToString() ?? "") + (delta["signature"]?.ToString() ?? "");
                            break;
                    }

                    break;
                }

                case "message_delta":
                    if (evt["delta"] is JsonObject d)
                    {
                        foreach (var (key, value) in d) message[key] = value?.DeepClone();
                    }

                    if (evt["usage"] is JsonObject du && message["usage"] is JsonObject mu)
                    {
                        foreach (var (key, value) in du) mu[key] = value?.DeepClone();
                    }
                    else if (evt["usage"] is JsonObject only)
                    {
                        message["usage"] = only.DeepClone();
                    }

                    break;
                case "error":
                    throw new RequestFailedException(evt["error"]?["message"]?.ToString() ?? "stream error");
            }
        }

        var content = message["content"]!.AsArray();
        foreach (var (_, entry) in blocks)
        {
            var block = entry.Block;
            if (block["type"]?.ToString() == "text")
            {
                block["text"] = entry.Text.ToString();
            }
            else if (block["type"]?.ToString() == "thinking")
            {
                block["thinking"] = entry.Text.ToString();
            }
            else if (block["type"]?.ToString() is "tool_use" or "server_tool_use" or "mcp_tool_use" && entry.Json.Length > 0)
            {
                block["input"] = JsonNode.Parse(entry.Json.ToString()) ?? new JsonObject();
            }

            content.Add(block);
        }

        return message;
    }

}

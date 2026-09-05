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

/// <summary>
/// Companion to <see cref="AzureAIModelApi"/> for Anthropic deployments on Azure AI Foundry, which are
/// served on the Anthropic Messages route (<c>/anthropic/v1/messages</c>) rather than the model-inference
/// route. It follows the Azure path of Inspect's <c>anthropic</c> provider
/// (<c>src/inspect_ai/model/_providers/anthropic.py</c>, <c>anthropic/azure/&lt;deployment&gt;</c>): the same
/// base-URL variables (<c>AZUREAI_ANTHROPIC_BASE_URL</c> / <c>AZURE_ANTHROPIC_BASE_URL</c>), the same
/// <c>max_tokens</c> rule, the same stop-reason and usage mapping. Two deliberate differences: it
/// authenticates with Entra ID only (bearer token from the same credential as the main provider; Python
/// requires an API key), and the base URL is derived from the model-inference endpoint when the Anthropic
/// variables are absent. Extended thinking is mapped from <c>GenerateConfig.ReasoningEffort</c> / <c>ReasoningTokens</c>
/// (adaptive thinking plus <c>output_config.effort</c>, or the deprecated <c>budget_tokens</c> form) and
/// thinking blocks are parsed, streamed and replayed with their signature; Claude's server-side web search
/// (the "anthropic" provider of the built-in <c>web_search</c> tool) is passed through and its results and
/// citations replayed (<see cref="AnthropicWebSearch"/>); prompt caching, document citations on input, batch
/// mode and the other server-side tools are not ported. Requests are sent with <see cref="HttpClient"/> over an injectable handler; responses are kept
/// as raw JSON so the recorded <see cref="ModelCall"/> is the wire payload.
/// </summary>
public sealed class AnthropicFoundryModelApi : IModelApi, IDisposable
{
    public const string AzureAIAnthropicBaseUrlVar = "AZUREAI_ANTHROPIC_BASE_URL";

    public const string AzureAnthropicBaseUrlVar = "AZURE_ANTHROPIC_BASE_URL";

    /// <summary>The <c>anthropic-version</c> header sent on every request.</summary>
    public const string AnthropicVersion = "2023-06-01";

    private static readonly string[] BaseUrlVars = [AzureAIAnthropicBaseUrlVar, AzureAnthropicBaseUrlVar];

    private static readonly string[] InferenceEndpointVars = [AzureAIModelApi.AzureEndpointUrlVar, AzureAIModelApi.AzureAIEndpointUrlVar, AzureAIModelApi.AzureAIBaseUrlVar];

    private readonly HttpClient _http;

    public AnthropicFoundryModelApi(
        string modelName,
        string? baseUrl = null,
        GenerateConfig? config = null,
        object? streaming = null,
        IReadOnlyDictionary<string, object?>? modelArgs = null,
        AzureAIClientSettings? settings = null,
        HttpMessageHandler? handler = null)
    {
        ModelName = modelName;
        var parts = modelName.Split('/', 2);
        DeploymentName = parts.Length == 2 && parts[0] == "azure" ? parts[1] : modelName;   // anthropic/azure/<deployment>
        Streaming = ProviderUtil.NormalizeStreamArg(streaming, "streaming");
        Config = config ?? new GenerateConfig();
        Settings = settings ?? new AzureAIClientSettings();

        // Model args become top-level request fields (applied last, so they override derived ones); the
        // reserved anthropic_beta arg is sent as the `anthropic-beta` header instead.
        var args = modelArgs is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(modelArgs);
        if (args.TryGetValue("anthropic_beta", out var beta))
        {
            args.Remove("anthropic_beta");
            AnthropicBeta = beta?.ToString();
        }

        args.Remove("model_format");   // a model-inference-route hint; never a Messages API field
        ModelArgs = args;

        Credential = AzureHosting.ResolveAzureCredential("Anthropic on Azure", Settings.TokenCredential);

        var resolved = ProviderUtil.ModelBaseUrl(baseUrl, BaseUrlVars) ?? ProviderUtil.ModelBaseUrl(null, InferenceEndpointVars);
        if (string.IsNullOrEmpty(resolved))
        {
            throw ProviderUtil.EnvironmentPrerequisiteError("Anthropic on Azure", [.. BaseUrlVars, .. InferenceEndpointVars]);
        }

        BaseUrl = DeriveBaseUrl(resolved);
        MessagesUrl = BaseUrl.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase) ? BaseUrl : BaseUrl.TrimEnd('/') + "/v1/messages";
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
    }

    /// <inheritdoc />
    public string ModelName { get; }

    /// <summary>Deployment name sent as <c>model</c>.</summary>
    public string DeploymentName { get; }

    /// <summary>Normalised <c>streaming</c> arg: null means auto.</summary>
    public bool? Streaming { get; }

    public GenerateConfig Config { get; }

    public AzureAIClientSettings Settings { get; }

    /// <summary>Entra ID credential pinned to <c>AZUREAI_AUDIENCE</c>, shared with the main provider's resolution.</summary>
    public AudienceTokenCredential Credential { get; }

    /// <summary>Value of the <c>anthropic-beta</c> header (from the <c>anthropic_beta</c> model arg), or null.</summary>
    public string? AnthropicBeta { get; }

    /// <summary>Leftover model args, sent as top-level request fields after the derived ones.</summary>
    public IReadOnlyDictionary<string, object?> ModelArgs { get; }

    /// <summary>Always <see cref="ModelFamilyHint.Anthropic"/>; the reasoning mapping is <see cref="ThinkingParams"/>.</summary>
    public ModelFamilyHint FamilyHint => ModelFamilyHint.Anthropic;

    /// <summary>The Anthropic base URL (<c>https://&lt;resource&gt;.services.ai.azure.com/anthropic</c>).</summary>
    public string BaseUrl { get; }

    /// <summary>The Messages endpoint the requests go to.</summary>
    public string MessagesUrl { get; }

    /// <summary>
    /// Turns any of the resource's URLs into the Anthropic base: a model-inference endpoint
    /// (<c>…/models</c>) or a bare host becomes <c>…/anthropic</c>; an Anthropic URL is kept.
    /// </summary>
    public static string DeriveBaseUrl(string url)
    {
        var trimmed = url.TrimEnd('/');
        if (trimmed.EndsWith("/anthropic", StringComparison.OrdinalIgnoreCase) || trimmed.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (trimmed.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^"/models".Length];
        }

        return trimmed + "/anthropic";
    }

    /// <summary>Port of the Python <c>max_tokens</c> rule: 4096 for Claude 3 and 3.5, 32000 otherwise.</summary>
    public int? MaxTokens()
    {
        var name = DeploymentName.ToLowerInvariant();
        var claude3 = name.Contains("claude-3") && !name.Contains("claude-3-7") && !name.Contains("claude-3.7");
        return claude3 ? 4096 : 32000;
    }

    /// <summary>HTTP 408/429/5xx and read failures are retried, 429 as a rate limit; everything else is not.</summary>
    public RetryDecision ShouldRetry(Exception ex)
    {
        if (ex is RequestFailedException { Status: > 0 } http)
        {
            if (!HttpRetryUtil.IsRetryableHttpStatus(http.Status))
            {
                return RetryDecision.No();
            }

            var retryAfter = HttpRetryUtil.ParseRetryAfterFromException(http);
            return http.Status == 429 ? RetryDecision.RateLimit(retryAfter) : RetryDecision.Transient(retryAfter);
        }

        return ex is ServiceResponseException ? RetryDecision.Transient() : RetryDecision.No();
    }

    public bool IsAuthFailure(Exception ex) => ex is RequestFailedException { Status: 401 };

    public Task<GenerateResult> GenerateAsync(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, ToolChoice toolChoice, GenerateConfig config, CancellationToken cancellationToken = default) =>
        GenerateAsync(input, tools, toolChoice, config, null, cancellationToken);

    /// <inheritdoc />
    public async Task<GenerateResult> GenerateAsync(
        IReadOnlyList<ChatMessage> input,
        IReadOnlyList<ToolInfo> tools,
        ToolChoice toolChoice,
        GenerateConfig config,
        StreamHandler? onStream,
        CancellationToken cancellationToken = default)
    {
        using var observer = onStream is null ? null : ModelStreamObserver.Install(new ModelStreamObserver(ModelName, onStream));
        var streaming = Streaming ?? ModelStreamObserver.ModelStreamRequested();
        var request = BuildRequest(input, tools, toolChoice, config, streaming);
        var modelCall = ModelCall.Create(request.DeepClone().AsObject(), MediaFilter);
        var watch = Stopwatch.StartNew();

        using var message = new HttpRequestMessage(HttpMethod.Post, MessagesUrl)
        {
            Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Add("anthropic-version", AnthropicVersion);
        if (AnthropicBeta is { Length: > 0 })
        {
            message.Headers.Add("anthropic-beta", AnthropicBeta);
        }

        await AuthorizeAsync(message, cancellationToken).ConfigureAwait(false);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            modelCall.SetError(ErrorNode(ex.Message), watch.Elapsed.TotalSeconds);
            throw new RequestFailedException(ex.Message, ex);                     // status 0, like Azure.Core's wrap
        }
        catch (Exception ex) when (ex is IOException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            modelCall.SetError(ErrorNode(ex.Message), watch.Elapsed.TotalSeconds);
            throw new ServiceResponseException(ex.Message, ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var text = ErrorMessage(body, response.StatusCode);
                modelCall.SetError(ErrorNode(text), watch.Elapsed.TotalSeconds);
                var error = new RequestFailedException((int)response.StatusCode, text);
                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    return new GenerateResult(null, error, modelCall);
                }

                throw error;
            }

            JsonObject messageJson;
            try
            {
                if (streaming)
                {
                    ModelStreamObserver.ReportModelStreamStart();
                    var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using (stream.ConfigureAwait(false))
                    {
                        messageJson = await AccumulateAsync(SseParser.ReadUpdatesAsync(stream, cancellationToken), cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    messageJson = JsonNode.Parse(body)?.AsObject() ?? throw new ServiceResponseException("Response body was not a JSON object.");
                }
            }
            catch (IOException ex)
            {
                modelCall.SetError(ErrorNode(ex.Message), watch.Elapsed.TotalSeconds);
                throw new ServiceResponseException(ex.Message, ex);
            }

            modelCall.SetResponse(messageJson.DeepClone(), watch.Elapsed.TotalSeconds);
            return new GenerateResult(ParseMessage(messageJson), null, modelCall);
        }
    }

    /// <summary>Builds the Messages request body (system prompt, alternating messages, tools, sampling params).</summary>
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
        if (tools.Count > 0 && choiceKind != "none")
        {
            request["tools"] = new JsonArray(tools.Select(t => (JsonNode?)(AnthropicWebSearch.ServerToolParam(t, DeploymentName) ?? new JsonObject
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

        if (config.Temperature is not null) request["temperature"] = config.Temperature;
        if (config.TopP is not null) request["top_p"] = config.TopP;
        if (config.StopSeqs is not null) request["stop_sequences"] = new JsonArray(config.StopSeqs.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        foreach (var (key, value) in ThinkingParams(config, maxTokens))
        {
            request[key] = value?.DeepClone();
        }

        if (streaming) request["stream"] = true;
        foreach (var (key, value) in ModelArgs)
        {
            request[key] = value is JsonNode node ? node.DeepClone() : JsonSerializer.SerializeToNode(value);
        }

        return request;
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
                    ["content"] = tool.Error is not null ? $"Error: {tool.Error.Message}" : tool.Text,
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

        return blocks;
    }

    private static JsonArray ContentBlocks(IReadOnlyList<Content> items)
    {
        var blocks = new JsonArray();
        foreach (var item in items)
        {
            switch (item)
            {
                case ContentText text when text.Text.Length > 0:
                    var textBlock = new JsonObject { ["type"] = "text", ["text"] = text.Text };
                    AnthropicWebSearch.AddCitations(textBlock, text.Citations);
                    blocks.Add(textBlock);
                    break;
                case ContentToolUse toolUse:
                    foreach (var replayed in AnthropicWebSearch.ReplayBlocks(toolUse))
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
            else if (block["type"]?.ToString() is "tool_use" or "server_tool_use" && entry.Json.Length > 0)
            {
                block["input"] = JsonNode.Parse(entry.Json.ToString()) ?? new JsonObject();
            }

            content.Add(block);
        }

        return message;
    }

    private async Task AuthorizeAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        var token = await Credential.GetTokenAsync(new TokenRequestContext([Credential.Scope]), cancellationToken).ConfigureAwait(false);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    private static string ErrorMessage(string body, HttpStatusCode status)
    {
        try
        {
            if (JsonNode.Parse(body)?["error"]?["message"]?.ToString() is { Length: > 0 } text)
            {
                return text;
            }
        }
        catch (JsonException)
        {
        }

        return body.Length > 0 ? body[..Math.Min(body.Length, 300)] : $"HTTP {(int)status}";
    }

    private static JsonObject ErrorNode(string message) => new() { ["error"] = new JsonObject { ["message"] = message } };

    /// <summary>Redacts base64 image payloads (<c>source.data</c>) in the recorded request.</summary>
    private static JsonNode? MediaFilter(string? key, JsonNode? value) =>
        key == "data" && value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 64 ? JsonValue.Create(OpenAIUtil.Base64DataRemoved) : value;

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
/// environment variables (<c>AZUREAI_ANTHROPIC_API_KEY</c> / <c>AZURE_ANTHROPIC_API_KEY</c>,
/// <c>AZUREAI_ANTHROPIC_BASE_URL</c> / <c>AZURE_ANTHROPIC_BASE_URL</c>), the same <c>max_tokens</c> rule,
/// the same stop-reason and usage mapping. Two deliberate extensions: Entra ID (bearer) is accepted when
/// no key is set, and the base URL is derived from the model-inference endpoint when the Anthropic
/// variables are absent. Thinking, prompt caching, citations, batch mode and server-side tools are not
/// ported. Requests are sent with <see cref="HttpClient"/> over an injectable handler; responses are kept
/// as raw JSON so the recorded <see cref="ModelCall"/> is the wire payload.
/// </summary>
public sealed class AnthropicFoundryModelApi : IModelApi, IDisposable
{
    public const string AzureAIAnthropicApiKeyVar = "AZUREAI_ANTHROPIC_API_KEY";

    public const string AzureAnthropicApiKeyVar = "AZURE_ANTHROPIC_API_KEY";

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
        string? apiKey = null,
        GenerateConfig? config = null,
        object? streaming = null,
        AzureAIClientSettings? settings = null,
        HttpMessageHandler? handler = null)
    {
        ModelName = modelName;
        var parts = modelName.Split('/', 2);
        DeploymentName = parts.Length == 2 && parts[0] == "azure" ? parts[1] : modelName;   // anthropic/azure/<deployment>
        Streaming = ProviderUtil.NormalizeStreamArg(streaming, "streaming");
        Config = config ?? new GenerateConfig();
        Settings = settings ?? new AzureAIClientSettings();

        ApiKey = !string.IsNullOrEmpty(apiKey) ? apiKey
            : Environment.GetEnvironmentVariable(AzureAIAnthropicApiKeyVar) is { Length: > 0 } preferred ? preferred
            : Environment.GetEnvironmentVariable(AzureAnthropicApiKeyVar) is { Length: > 0 } legacy ? legacy
            : null;
        if (ApiKey is null)
        {
            Credential = AzureHosting.ResolveAzureCredential("Anthropic on Azure", Settings.TokenCredential);
        }

        var resolved = ProviderUtil.ModelBaseUrl(baseUrl, BaseUrlVars) ?? ProviderUtil.ModelBaseUrl(null, InferenceEndpointVars);
        if (string.IsNullOrEmpty(resolved))
        {
            throw ProviderUtil.EnvironmentPrerequisiteError("Anthropic on Azure", [AzureAIAnthropicBaseUrlVar]);
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

    /// <summary>Resolved key, or null when Entra ID is used.</summary>
    public string? ApiKey { get; }

    /// <summary>Entra ID credential pinned to <c>AZUREAI_AUDIENCE</c>, set only when no key was found (port-only extension).</summary>
    public AudienceTokenCredential? Credential { get; }

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
            return new GenerateResult(ParseMessage(messageJson, tools), null, modelCall);
        }
    }

    /// <summary>Builds the Messages request body (system prompt, alternating messages, tools, sampling params).</summary>
    public JsonObject BuildRequest(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, ToolChoice toolChoice, GenerateConfig config, bool streaming)
    {
        var request = new JsonObject
        {
            ["model"] = DeploymentName,
            ["max_tokens"] = config.MaxTokens ?? MaxTokens() ?? 4096,
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
            request["tools"] = new JsonArray(tools.Select(t => (JsonNode?)new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = JsonSchemaDump.Dump(t.Parameters.ToJson(), JsonSchemaDump.JsonSchemaExtendedFields),
            }).ToArray());
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
        if (streaming) request["stream"] = true;
        return request;
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

    private static JsonArray AssistantBlocks(ChatMessageAssistant assistant)
    {
        var blocks = ContentBlocks(assistant.ContentList);
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
                    blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
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
    public ModelOutput ParseMessage(JsonObject message, IReadOnlyList<ToolInfo> tools)
    {
        var items = new List<Content>();
        var toolCalls = new List<ToolCall>();
        foreach (var block in message["content"]?.AsArray() ?? [])
        {
            switch (block?["type"]?.ToString())
            {
                case "text":
                    items.Add(new ContentText(block["text"]?.ToString() ?? ""));
                    break;
                case "tool_use":
                    toolCalls.Add(ToolCallParsing.ParseToolCall(
                        block["id"]?.ToString() ?? "", block["name"]?.ToString() ?? "", PythonJson.Dumps(block["input"] ?? new JsonObject()), tools));
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
                            await ModelStreamObserver.ReportModelStreamDeltaAsync(new StreamToolCallEvent(entry.Block["id"]?.ToString(), entry.Block["name"]?.ToString(), partial)).ConfigureAwait(false);
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
            else if (block["type"]?.ToString() == "tool_use" && entry.Json.Length > 0)
            {
                block["input"] = JsonNode.Parse(entry.Json.ToString()) ?? new JsonObject();
            }

            content.Add(block);
        }

        return message;
    }

    private async Task AuthorizeAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        if (ApiKey is not null)
        {
            message.Headers.Add("x-api-key", ApiKey);          // the Anthropic SDK header …
            message.Headers.Add("api-key", ApiKey);            // … and the Azure AI Services one
            return;
        }

        var token = await Credential!.GetTokenAsync(new TokenRequestContext([Credential.Scope]), cancellationToken).ConfigureAwait(false);
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
        catch (System.Text.Json.JsonException)
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

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
/// citations replayed (<see cref="AnthropicWebSearch"/>); remote MCP servers (<c>execution="remote"</c>) are sent
/// as <c>mcp_servers</c> and their calls parsed and replayed (<see cref="AnthropicRemoteMcp"/>); prompt caching,
/// document citations on input, batch mode and the other server-side tools are not ported. Requests are sent with <see cref="HttpClient"/> over an injectable handler; responses are kept
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
    public string ProviderName => "anthropic";
    public string QualifiedModelName => "anthropic/azure/" + DeploymentName;
    public bool IsFoundry => true;
    public string ConnectionKey() => $"{BaseUrl}:{ModelName}";
    public bool CollapseUserMessages() => true;
    public bool SupportsRemoteMcp() => true;

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
        if (BetaHeader(config, remoteMcp: request["mcp_servers"] is JsonArray { Count: > 0 }) is { Length: > 0 } beta)
        {
            message.Headers.Add("anthropic-beta", beta);
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
    public JsonObject BuildRequest(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, ToolChoice toolChoice, GenerateConfig config, bool streaming) => new AnthropicProtocol(DeploymentName, ModelArgs, AnthropicBeta).BuildRequest(input, tools, toolChoice, config, streaming);
    public const string FallbackModelsIgnoredWarning = AnthropicProtocol.FallbackModelsIgnoredWarning;
    public string? BetaHeader(GenerateConfig config, bool remoteMcp = false) => new AnthropicProtocol(DeploymentName, ModelArgs, AnthropicBeta).BetaHeader(config, remoteMcp);
    public static JsonObject ThinkingParams(GenerateConfig config, int maxTokens) => AnthropicProtocol.ThinkingParams(config, maxTokens);
    public static JsonArray Messages(IReadOnlyList<ChatMessage> input) => AnthropicProtocol.Messages(input);
    public ModelOutput ParseMessage(JsonObject message) => new AnthropicProtocol(DeploymentName, ModelArgs).ParseMessage(message);
    public static Task<JsonObject> AccumulateAsync(IAsyncEnumerable<JsonObject> events, CancellationToken cancellationToken = default) => AnthropicProtocol.AccumulateAsync(events, cancellationToken);

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

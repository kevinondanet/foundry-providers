using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Provider.OpenAI;

/// <summary>
/// The OpenAI Responses API route for Azure AI Foundry (<c>…/openai/v1/responses</c>), the third companion
/// next to <see cref="AzureAIModelApi"/> (chat completions) and the Anthropic Messages route. Foundry serves
/// the gpt-5.6 family, gpt-5.4-pro and the o-series on this API: on chat completions they reject function
/// tools combined with <c>reasoning_effort</c> (or, with <c>chatCompletion: false</c> in ARM, everything).
/// It follows the Responses path of Inspect's <c>openai</c> provider (<c>_providers/openai_responses.py</c>,
/// <c>_openai_responses.py</c>): <c>store: false</c> with <c>include: ["reasoning.encrypted_content"]</c> so
/// reasoning replays statelessly, <c>reasoning.effort</c> verbatim, <c>max_output_tokens</c>, flat function
/// tools, <c>text.format</c> for structured output. Deliberate differences: Entra ID bearer auth only (the
/// same credential and audience as the other routes; Python takes an API key), the base URL is derived from
/// the model-inference endpoint when <c>AZUREAI_OPENAI_BASE_URL</c> / <c>AZURE_OPENAI_BASE_URL</c> are absent,
/// reasoning summaries are requested only when the config asks (Azure organisations commonly get a 400 for
/// them), and the built-in tools (web search, computer use, remote MCP, code interpreter) are not ported.
/// Requests are sent with <see cref="HttpClient"/> over an injectable handler and responses are kept as raw
/// JSON so the recorded <see cref="ModelCall"/> is the wire payload.
/// </summary>
public sealed class OpenAIResponsesModelApi : IModelApi, IDisposable
{
    public const string AzureAIOpenAIBaseUrlVar = "AZUREAI_OPENAI_BASE_URL";

    public const string AzureOpenAIBaseUrlVar = "AZURE_OPENAI_BASE_URL";

    /// <summary>The <c>include</c> entry that returns reasoning items in a form that can be replayed with <c>store: false</c>.</summary>
    public const string EncryptedReasoningInclude = "reasoning.encrypted_content";

    public const string TemperatureIgnoredWarning =
        "OpenAI Responses on Azure: temperature is not supported for reasoning models and was ignored.";

    public const string TopPIgnoredWarning =
        "OpenAI Responses on Azure: top_p is not supported for reasoning models and was ignored.";

    public const string FallbackModelsIgnoredWarning =
        "fallback_models is only supported on the first-party OpenAI API (not azure) and will be ignored.";

    private static readonly string[] BaseUrlVars = [AzureAIOpenAIBaseUrlVar, AzureOpenAIBaseUrlVar];

    private static readonly string[] InferenceEndpointVars = [AzureAIModelApi.AzureEndpointUrlVar, AzureAIModelApi.AzureAIEndpointUrlVar, AzureAIModelApi.AzureAIBaseUrlVar];

    /// <summary>Port of <c>responses_extra_body_fields</c>: the <c>extra_body</c> keys forwarded to the request.</summary>
    private static readonly string[] ExtraBodyFields =
    [
        "store", "include", "service_tier", "max_tool_calls", "metadata", "previous_response_id",
        "prompt_cache_key", "prompt_cache_retention", "safety_identifier", "truncation", "background",
    ];

    private readonly HttpClient _http;

    public OpenAIResponsesModelApi(
        string modelName,
        string? baseUrl = null,
        GenerateConfig? config = null,
        object? streaming = null,
        IReadOnlyDictionary<string, object?>? modelArgs = null,
        AzureAIClientSettings? settings = null,
        HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelName);
        ModelName = modelName;
        var parts = modelName.Split('/', 2);
        DeploymentName = parts.Length == 2 && parts[0] == "azure" ? parts[1] : modelName;   // openai/azure/<deployment>
        Streaming = ProviderUtil.NormalizeStreamArg(streaming, "streaming");
        Config = config ?? new GenerateConfig();
        Settings = settings ?? new AzureAIClientSettings();

        // Model args become top-level request fields (applied last, so they override derived ones).
        var args = modelArgs is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(modelArgs);
        args.Remove("model_format");   // a model-inference-route hint; never a Responses API field
        ModelArgs = args;

        Credential = AzureHosting.ResolveAzureCredential("OpenAI on Azure", Settings.TokenCredential);

        var resolved = ProviderUtil.ModelBaseUrl(baseUrl, BaseUrlVars) ?? ProviderUtil.ModelBaseUrl(null, InferenceEndpointVars);
        if (string.IsNullOrEmpty(resolved))
        {
            throw ProviderUtil.EnvironmentPrerequisiteError("OpenAI on Azure", [.. BaseUrlVars, .. InferenceEndpointVars]);
        }

        BaseUrl = DeriveBaseUrl(resolved);
        ResponsesUrl = BaseUrl + "/responses";
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;   // gpt-5.4-pro answers can outlast the 100 s default; the model layer's attempt timeout governs
    }

    /// <inheritdoc />
    public string ModelName { get; }

    /// <summary>Deployment name sent as <c>model</c>.</summary>
    public string DeploymentName { get; }

    /// <summary>Normalised <c>streaming</c> arg: null means auto.</summary>
    public bool? Streaming { get; }

    public GenerateConfig Config { get; }

    public AzureAIClientSettings Settings { get; }

    /// <summary>Entra ID credential pinned to <c>AZUREAI_AUDIENCE</c>, shared with the other routes' resolution.</summary>
    public AudienceTokenCredential Credential { get; }

    /// <summary>Leftover model args, sent as top-level request fields after the derived ones.</summary>
    public IReadOnlyDictionary<string, object?> ModelArgs { get; }

    /// <summary>Always <see cref="ModelFamilyHint.OpenAI"/>; the per-family mapping of <c>ReasoningParams</c> is bypassed (this route always speaks <c>reasoning.effort</c>).</summary>
    public ModelFamilyHint FamilyHint => ModelFamilyHint.OpenAI;

    /// <summary>Whether the deployment name looks like a reasoning model (port of <c>has_reasoning_options</c>).</summary>
    public bool IsReasoningModel => OpenAIUtil.HasReasoningOptions(DeploymentName);

    /// <summary>The OpenAI v1 base URL (<c>https://&lt;resource&gt;.services.ai.azure.com/openai/v1</c>).</summary>
    public string BaseUrl { get; }

    /// <summary>The Responses endpoint the requests go to.</summary>
    public string ResponsesUrl { get; }

    /// <summary>
    /// Turns any of the resource's URLs into the OpenAI v1 base: a model-inference endpoint (<c>…/models</c>),
    /// an Anthropic base (<c>…/anthropic</c>) or a bare host becomes <c>…/openai/v1</c>; <c>…/openai</c> gets
    /// <c>/v1</c>; an OpenAI v1 URL (with or without <c>/responses</c>) is kept.
    /// </summary>
    public static string DeriveBaseUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var trimmed = url.TrimEnd('/');
        if (trimmed.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^"/responses".Length];
        }

        if (trimmed.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (trimmed.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed + "/v1";
        }

        if (trimmed.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^"/models".Length];
        }
        else if (trimmed.EndsWith("/anthropic", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^"/anthropic".Length];
        }

        return trimmed + "/openai/v1";
    }

    /// <summary>No default: Python's OpenAI provider leaves <c>max_output_tokens</c> to the service, so reasoning output is not capped.</summary>
    public int? MaxTokens() => null;

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
        ArgumentNullException.ThrowIfNull(config);
        using var observer = onStream is null ? null : ModelStreamObserver.Install(new ModelStreamObserver(ModelName, onStream));
        var streaming = Streaming ?? ModelStreamObserver.ModelStreamRequested();
        var request = BuildRequest(input, tools, toolChoice, config, streaming);
        var modelCall = ModelCall.Create(request.DeepClone().AsObject(), MediaFilter);
        var watch = Stopwatch.StartNew();

        using var message = new HttpRequestMessage(HttpMethod.Post, ResponsesUrl)
        {
            Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in config.ExtraHeaders ?? new Dictionary<string, string>())
        {
            message.Headers.TryAddWithoutValidation(name, value);
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
                var (code, type, text) = ErrorDetails(body, response.StatusCode);
                modelCall.SetError(ErrorNode(text, code, type), watch.Elapsed.TotalSeconds);
                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    // Port of openai_handle_bad_request: context length and content-policy errors are outputs, any other 400 is terminal.
                    var refusal = ResponsesOutput.RefusalOutput(DeploymentName, code, type, text);
                    return refusal is not null
                        ? new GenerateResult(refusal, null, modelCall)
                        : new GenerateResult(null, new RequestFailedException((int)response.StatusCode, text), modelCall);
                }

                throw new RequestFailedException((int)response.StatusCode, text);
            }

            JsonObject responseJson;
            try
            {
                if (streaming)
                {
                    ModelStreamObserver.ReportModelStreamStart();
                    var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using (stream.ConfigureAwait(false))
                    {
                        responseJson = await ResponsesStreamAccumulator.AccumulateAsync(SseParser.ReadUpdatesAsync(stream, cancellationToken), cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    responseJson = JsonNode.Parse(body)?.AsObject() ?? throw new ServiceResponseException("Response body was not a JSON object.");
                }
            }
            catch (IOException ex)
            {
                modelCall.SetError(ErrorNode(ex.Message), watch.Elapsed.TotalSeconds);
                throw new ServiceResponseException(ex.Message, ex);
            }

            // A failed response (status "failed", or a stream-level error event) carries `error` instead of output.
            if (responseJson["error"] is JsonObject error)
            {
                var code = error["code"]?.ToString();
                var text = error["message"]?.ToString() is { Length: > 0 } m ? m : "response failed";
                modelCall.SetError(responseJson.DeepClone(), watch.Elapsed.TotalSeconds);
                var refusal = ResponsesOutput.RefusalOutput(DeploymentName, code, error["type"]?.ToString(), text);
                if (refusal is not null)
                {
                    return new GenerateResult(refusal, null, modelCall);
                }

                throw code is "server_error" or "rate_limit_exceeded"
                    ? new ServiceResponseException(text)
                    : new RequestFailedException(text);
            }

            modelCall.SetResponse(responseJson.DeepClone(), watch.Elapsed.TotalSeconds);
            return new GenerateResult(ResponsesOutput.Parse(responseJson, DeploymentName), null, modelCall);
        }
    }

    /// <summary>
    /// Builds the Responses request body (port of <c>completion_params_responses</c>): the input items, the
    /// flat tools with <c>tool_choice</c> and <c>parallel_tool_calls</c>, <c>store: false</c> plus the encrypted
    /// reasoning include, <c>max_output_tokens</c>, sampling parameters only when reasoning is off,
    /// <c>reasoning</c>, <c>text</c> (format and verbosity), the whitelisted <c>extra_body</c> fields,
    /// <c>stream</c>, then the model args last.
    /// </summary>
    public JsonObject BuildRequest(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, ToolChoice toolChoice, GenerateConfig config, bool streaming)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(toolChoice);
        ArgumentNullException.ThrowIfNull(config);
        var request = new JsonObject
        {
            ["model"] = DeploymentName,
            ["input"] = ResponsesInput.InputItems(input),
        };

        if (tools.Count > 0)
        {
            request["tools"] = ResponsesTools.ToolParams(tools);
            if (ResponsesTools.ToolChoiceParam(toolChoice) is { } choice)
            {
                request["tool_choice"] = choice;
            }

            if (config.ParallelToolCalls is not null && !OpenAIUtil.IsOSeriesModel(DeploymentName))
            {
                request["parallel_tool_calls"] = config.ParallelToolCalls;
            }
        }

        var store = IsTrue(config.ExtraBody?["store"]) || (ModelArgs.TryGetValue("store", out var storeArg) && IsTrue(storeArg));
        request["store"] = store;
        var reasoningRequested = !string.IsNullOrEmpty(config.ReasoningEffort) || !string.IsNullOrEmpty(config.ReasoningMode) || !string.IsNullOrEmpty(config.ReasoningSummary);
        if (!store && (IsReasoningModel || reasoningRequested))
        {
            request["include"] = new JsonArray(EncryptedReasoningInclude);
        }

        if (config.MaxTokens is not null) request["max_output_tokens"] = config.MaxTokens;

        var reasoningOn = ReasoningEnabled(DeploymentName, config);
        if (config.Temperature is not null)
        {
            if (reasoningOn) ProviderLogger.WarnOnce(TemperatureIgnoredWarning);
            else request["temperature"] = config.Temperature;
        }

        if (config.TopP is not null)
        {
            if (reasoningOn) ProviderLogger.WarnOnce(TopPIgnoredWarning);
            else request["top_p"] = config.TopP;
        }

        if (config.FrequencyPenalty is not null) ProviderLogger.WarnOnce(UnsupportedParamWarning("frequency_penalty"));
        if (config.PresencePenalty is not null) ProviderLogger.WarnOnce(UnsupportedParamWarning("presence_penalty"));
        if (config.StopSeqs is not null) ProviderLogger.WarnOnce(UnsupportedParamWarning("stop_seqs"));
        if (config.Seed is not null) ProviderLogger.WarnOnce(UnsupportedParamWarning("seed"));
        if (config.LogitBias is not null) ProviderLogger.WarnOnce(UnsupportedParamWarning("logit_bias"));
        if (config.NumChoices is not null) ProviderLogger.WarnOnce(UnsupportedParamWarning("num_choices"));
        if (config.Logprobs is not null) ProviderLogger.WarnOnce(UnsupportedParamWarning("logprobs"));
        if (config.TopLogprobs is not null) ProviderLogger.WarnOnce(UnsupportedParamWarning("top_logprobs"));

        if (ReasoningParams(DeploymentName, config) is { Count: > 0 } reasoning)
        {
            request["reasoning"] = reasoning;
        }

        var text = new JsonObject();
        if (config.ResponseSchema is { } responseSchema)
        {
            text["format"] = ResponsesTools.TextFormat(responseSchema);
        }

        if (config.Verbosity is not null)
        {
            text["verbosity"] = config.Verbosity;
        }

        if (text.Count > 0)
        {
            request["text"] = text;
        }

        if (config.FallbackModels is { Count: > 0 })
        {
            ProviderLogger.WarnOnce(FallbackModelsIgnoredWarning);
        }

        foreach (var field in ExtraBodyFields)
        {
            if (config.ExtraBody?[field] is { } value && !request.ContainsKey(field))
            {
                request[field] = value.DeepClone();
            }
        }

        if (streaming) request["stream"] = true;
        foreach (var (key, value) in ModelArgs)
        {
            request[key] = value is JsonNode node ? node.DeepClone() : JsonSerializer.SerializeToNode(value);
        }

        return request;
    }

    /// <summary>
    /// The <c>reasoning</c> object: <c>effort</c> verbatim (<c>max</c> becomes <c>xhigh</c> for models before
    /// gpt-5.6), <c>mode</c> verbatim (<c>pro</c>), and <c>summary</c> only when the config asks for one (and not
    /// <c>none</c>). Empty when no reasoning setting is given. Not gated on the model name: Azure deployment
    /// names are arbitrary, and a deployment that does not reason answers with a clear 400.
    /// </summary>
    public static JsonObject ReasoningParams(string deploymentName, GenerateConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var reasoning = new JsonObject();
        var effort = config.ReasoningEffort?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(effort))
        {
            if (effort == "max" && !OpenAIUtil.SupportsMaxReasoningEffort(deploymentName))
            {
                effort = "xhigh";
            }

            reasoning["effort"] = effort;
        }

        if (!string.IsNullOrEmpty(config.ReasoningMode))
        {
            reasoning["mode"] = config.ReasoningMode;
        }

        var summary = config.ReasoningSummary?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(summary) && summary != "none")
        {
            reasoning["summary"] = summary;
        }

        return reasoning;
    }

    /// <summary>
    /// Port of the "reasoning enabled" test that decides whether sampling parameters are sent: the o-series
    /// always reason; gpt-5 (not gpt-5.x) reasons unless the effort is <c>none</c>; gpt-5.x reasons when an
    /// effort other than <c>none</c> or the <c>pro</c> mode is set; any other name follows the config.
    /// </summary>
    public static bool ReasoningEnabled(string deploymentName, GenerateConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var name = deploymentName.ToLowerInvariant();
        var effort = config.ReasoningEffort?.Trim().ToLowerInvariant();
        var explicitReasoning = (!string.IsNullOrEmpty(effort) && effort != "none") || config.ReasoningMode == "pro";
        if (OpenAIUtil.IsOSeriesModel(name))
        {
            return true;
        }

        if (OpenAIUtil.IsGpt5Model(name) && !name.Contains("-chat") && !OpenAIUtil.IsGpt5Plus(name))
        {
            return effort != "none";
        }

        return explicitReasoning;
    }

    public static string UnsupportedParamWarning(string param) =>
        $"OpenAI Responses on Azure: the '{param}' parameter is not supported by the Responses API and was ignored.";

    /// <summary>A boolean-ish <c>store</c> value from <c>extra_body</c> or a model arg (<c>true</c>, <c>"true"</c>, or a JSON true).</summary>
    private static bool IsTrue(object? value) => value switch
    {
        bool b => b,
        string s => s.Equals("true", StringComparison.OrdinalIgnoreCase),
        JsonValue json => (json.TryGetValue<bool>(out var jb) && jb) || (json.TryGetValue<string>(out var js) && js.Equals("true", StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };

    private async Task AuthorizeAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        var token = await Credential.GetTokenAsync(new TokenRequestContext([Credential.Scope]), cancellationToken).ConfigureAwait(false);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    /// <summary>The error's <c>code</c>, <c>type</c> and <c>message</c> from an error body, or a truncated body / the status when it is not JSON.</summary>
    private static (string? Code, string? Type, string Message) ErrorDetails(string body, HttpStatusCode status)
    {
        try
        {
            if (JsonNode.Parse(body)?["error"] is JsonObject error)
            {
                var message = error["message"]?.ToString();
                return (error["code"]?.ToString(), error["type"]?.ToString(), message is { Length: > 0 } ? message : Truncate(body, status));
            }
        }
        catch (JsonException)
        {
        }

        return (null, null, Truncate(body, status));
    }

    private static string Truncate(string body, HttpStatusCode status) =>
        body.Length > 0 ? body[..Math.Min(body.Length, 300)] : $"HTTP {(int)status}";

    private static JsonObject ErrorNode(string message, string? code = null, string? type = null)
    {
        var error = new JsonObject { ["message"] = message };
        if (code is not null) error["code"] = code;
        if (type is not null) error["type"] = type;
        return new JsonObject { ["error"] = error };
    }

    /// <summary>Redacts inline media (<c>image_url</c> and <c>file_data</c> data URIs) in the recorded request.</summary>
    private static JsonNode? MediaFilter(string? key, JsonNode? value) =>
        key is "image_url" or "file_data" && value is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith("data:", StringComparison.Ordinal)
            ? JsonValue.Create(OpenAIUtil.Base64DataRemoved)
            : value;

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}

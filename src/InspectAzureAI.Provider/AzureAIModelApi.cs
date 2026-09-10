using System.ClientModel.Primitives;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.AI.Inference;
using Azure.Core;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Tools;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Provider;

/// <summary>
/// Lite port of <c>AzureAIAPI</c> (<c>src/inspect_ai/model/_providers/azureai.py</c>): the Inspect model
/// provider for Azure AI Foundry model-inference endpoints, built on the official
/// <see cref="ChatCompletionsClient"/>. This variant authenticates with Entra ID only (the
/// <c>DefaultAzureCredential</c> chain, i.e. <c>az login</c>) and uses native tool calling only; the
/// Python provider's API-key path and its Llama <c>&lt;tool_call&gt;</c> prompt emulation are not
/// included. <c>GenerateAsync</c> reproduces <c>generate()</c>: request assembly, streaming
/// accumulation, <see cref="ModelCall"/> capture and Azure error handling. The <c>ModelAPI</c> hooks the
/// Python class overrides (<see cref="MaxTokens"/>, <see cref="ShouldRetry"/>, <see cref="IsAuthFailure"/>,
/// <see cref="CollapseUserMessages"/>, <see cref="ConnectionKey"/>, <see cref="CanonicalName"/>,
/// <see cref="ServiceModelName"/>) are exposed as methods.
/// </summary>
public sealed class AzureAIModelApi : IModelApi
{
    public const string AzureAIBaseUrlVar = "AZUREAI_BASE_URL";

    public const string AzureAIEndpointUrlVar = "AZUREAI_ENDPOINT_URL";

    public const string AzureAIAudienceVar = AzureHosting.AzureAIAudience;

    /// <summary>Legacy endpoint variable.</summary>
    public const string AzureEndpointUrlVar = "AZURE_ENDPOINT_URL";

    /// <summary>Port of <c>DEFAULT_MAX_TOKENS</c> (<c>src/inspect_ai/_util/constants.py</c>).</summary>
    public const int DefaultMaxTokens = 2048;

    /// <summary>Port of <c>DEFAULT_MAX_CONNECTIONS</c>.</summary>
    public const int DefaultMaxConnections = 10;

    private readonly Dictionary<string, object?> _modelArgs;

    /// <summary>The boolean <c>max_completion_tokens</c> model arg as given (null when absent).</summary>
    private readonly bool? _maxCompletionTokensArg;

    /// <summary>
    /// Port of <c>AzureAIAPI.__init__</c>, minus the API-key resolution. <paramref name="streaming"/>
    /// accepts <c>true</c>/<c>false</c> or the strings <c>"auto"</c>/<c>"true"</c>/<c>"false"</c> (as
    /// <c>-M streaming=...</c> arrives); null is the same as <c>"auto"</c>. <paramref name="modelArgs"/>
    /// mirrors <c>**model_args</c>: a boolean <c>max_completion_tokens</c> is popped and the remainder is
    /// forwarded to the request body as <c>model_extras</c>. The credential is resolved eagerly, as in
    /// Python, so a broken sign-in fails at construction rather than on the first call.
    /// </summary>
    public AzureAIModelApi(
        string modelName,
        string? baseUrl = null,
        GenerateConfig? config = null,
        object? streaming = null,
        IReadOnlyDictionary<string, object?>? modelArgs = null,
        AzureAIClientSettings? settings = null)
    {
        Streaming = ProviderUtil.NormalizeStreamArg(streaming, "streaming");

        if (modelName.Contains('/'))
        {
            OrgPrefix = modelName.Split('/', 2)[0];
        }

        ModelName = modelName;
        BaseUrl = baseUrl;
        Config = config ?? new GenerateConfig();
        Settings = settings ?? new AzureAIClientSettings();

        _modelArgs = modelArgs is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(modelArgs);

        // Port-only model arg: -M max_completion_tokens=true sends config.MaxTokens as max_completion_tokens
        // for any model family; false keeps Python's name-only rule (gpt-5 / o-series). Absent, the Microsoft
        // family (MAI-Thinking-1, which rejects max_tokens) is added to that rule. A non-boolean value is left
        // in model_extras as a body field.
        if (_modelArgs.TryGetValue("max_completion_tokens", out var forceMct) && forceMct is bool force)
        {
            _modelArgs.Remove("max_completion_tokens");
            _maxCompletionTokensArg = force;
        }

        // Port-only model arg: -M model_format=<ARM Format> names the vendor when the deployment name does not
        // (the sample passes the Foundry deployment's Format); it selects the reasoning parameter mapping.
        if (_modelArgs.TryGetValue("model_format", out var modelFormat))
        {
            _modelArgs.Remove("model_format");
            ModelFormat = modelFormat?.ToString();
        }

        Credential = AzureHosting.ResolveAzureCredential("AzureAI", Settings.TokenCredential);

        var endpointUrl = ProviderUtil.ModelBaseUrl(baseUrl, [AzureEndpointUrlVar, AzureAIEndpointUrlVar, AzureAIBaseUrlVar]);
        if (string.IsNullOrEmpty(endpointUrl))
        {
            throw ProviderUtil.EnvironmentPrerequisiteError("AzureAI", [AzureAIBaseUrlVar]);
        }

        EndpointUrl = endpointUrl;
    }

    /// <summary>Full model name including any org prefix (as it appears in logs).</summary>
    string? IModelApi.BaseUrl => EndpointUrl;
    public string ProviderName => "azureai";
    public string QualifiedModelName => "azureai/" + ModelName;
    public bool IsFoundry => true;
    string IModelApi.ConnectionKey() => $"{EndpointUrl}:{ModelName}";

    public string ModelName { get; }

    /// <summary>The explicit base URL argument (may be null).</summary>
    public string? BaseUrl { get; }

    /// <summary>Config passed at construction (the Python base class does not store it; kept for the sample).</summary>
    public GenerateConfig Config { get; }

    /// <summary>Host settings (transport, credential).</summary>
    public AzureAIClientSettings Settings { get; }

    /// <summary>Normalised <c>streaming</c> arg: null means auto.</summary>
    public bool? Streaming { get; }

    /// <summary>Org prefix when the name is <c>org/model</c>.</summary>
    public string? OrgPrefix { get; }

    /// <summary>
    /// Entra ID credential pinned to <see cref="TokenAudience"/>. It is handed to the SDK's
    /// <c>TokenCredential</c> constructor, so the token travels only as <c>Authorization: Bearer</c> and
    /// is cached and refreshed by the SDK (README fidelity note 17). By default it is a
    /// <c>DefaultAzureCredential</c>, which picks up <c>az login</c>.
    /// </summary>
    public AudienceTokenCredential Credential { get; }

    /// <summary>Port-only: <c>max_completion_tokens=true</c> model arg, forcing <c>max_completion_tokens</c> for every family (README fidelity note 13).</summary>
    public bool ForceMaxCompletionTokens => _maxCompletionTokensArg == true;

    /// <summary>
    /// Whether <c>config.MaxTokens</c> goes out as <c>max_completion_tokens</c>: forced by the model arg, Python's
    /// gpt-5 / o-series name rule, or (port-only, unless the arg is explicitly false) the Microsoft family, whose
    /// reasoning deployments reject <c>max_tokens</c> (README fidelity note 13).
    /// </summary>
    public bool SendsMaxCompletionTokens =>
        ForceMaxCompletionTokens
        || OpenAIUtil.NeedsMaxCompletionTokens(ModelFamily())
        || (_maxCompletionTokensArg is null && FamilyHint == ModelFamilyHint.Microsoft);

    /// <summary>Port-only: the deployment's vendor (the ARM <c>Format</c> string) from the <c>model_format</c> model arg, when given.</summary>
    public string? ModelFormat { get; }

    /// <summary>The family the reasoning parameters are mapped for (<see cref="ReasoningParams.FamilyOf"/>).</summary>
    public ModelFamilyHint FamilyHint => ReasoningParams.FamilyOf(ModelFormat, ServiceModelName());

    /// <summary>Resolved endpoint (stored verbatim; the SDK appends <c>/chat/completions?api-version=...</c>).</summary>
    public string EndpointUrl { get; }

    /// <summary>Leftover model args forwarded as <c>model_extras</c> (top-level JSON body fields).</summary>
    public IReadOnlyDictionary<string, object?> ModelArgs => _modelArgs;

    /// <summary>The audience/scope requested for Entra ID tokens.</summary>
    public static string TokenAudience => AzureHosting.ResolveAudience();

    /// <summary>Port of <c>service_model_name</c>: the name without its org prefix, used on the wire.</summary>
    public string ServiceModelName() =>
        OrgPrefix is not null ? ReplaceFirst(ModelName, $"{OrgPrefix}/", "") : ModelName;

    /// <summary>
    /// Port of <c>ModelAPI.model_family</c>. Inspect consults its model-info registry first; the sample has
    /// no registry, so this is always <see cref="ServiceModelName"/>.
    /// </summary>
    public string ModelFamily() => ServiceModelName();

    public bool IsMistral() => IsMistralModel(ModelFamily());

    /// <summary>Port of <c>canonical_name</c>: explicit org prefix wins, else <c>openai/</c> or <c>mistral/</c> auto-detection.</summary>
    public string CanonicalName()
    {
        var baseName = ServiceModelName();
        if (OrgPrefix is not null)
        {
            return $"{OrgPrefix}/{baseName}";
        }

        if (IsOpenAIModelName(baseName))
        {
            return $"openai/{baseName}";
        }

        if (IsMistralModel(baseName))
        {
            return $"mistral/{baseName}";
        }

        return baseName;
    }

    /// <summary>Port of <c>max_tokens</c>: null for Mistral (the service default applies), <see cref="DefaultMaxTokens"/> otherwise.</summary>
    public int? MaxTokens()
    {
        if (IsMistral())
        {
            return null;
        }

        return DefaultMaxTokens;
    }

    /// <summary>Not overridden in Python: the <c>ModelAPI</c> default.</summary>
    public int MaxConnections() => DefaultMaxConnections;

    /// <summary>Port of <c>collapse_user_messages</c> (true: the model layer merges consecutive user messages).</summary>
    public bool CollapseUserMessages() => true;

    /// <summary>Port of <c>connection_key</c>: with no API key in play, one connection pool per model name.</summary>
    public string ConnectionKey() => ModelName;

    /// <summary>
    /// Port of <c>should_retry</c>: HTTP 408/429/5xx retry (429 as rate-limit, with Retry-After parsing),
    /// other HTTP statuses do not; a <see cref="ServiceResponseException"/> is transient; anything else
    /// (including connection failures, Python's <c>ServiceRequestError</c>) is not retried. Expects the
    /// exception as thrown by <see cref="GenerateAsync(IReadOnlyList{ChatMessage}, IReadOnlyList{ToolInfo}, ToolChoice, GenerateConfig, CancellationToken)"/>,
    /// which has already normalised the SDK's transport failures via <see cref="AsAzureError"/>.
    /// </summary>
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

        if (ex is ServiceResponseException)
        {
            return RetryDecision.Transient();
        }

        return RetryDecision.No();
    }

    /// <summary>Port of <c>is_auth_failure</c>: HTTP 401.</summary>
    public bool IsAuthFailure(Exception ex) => ex is RequestFailedException { Status: 401 };

    /// <summary>
    /// Port of <c>completion_params</c>: the forwarded <see cref="GenerateConfig"/> fields in Python order.
    /// <c>max_tokens</c> is emitted as <c>max_completion_tokens</c> when <see cref="SendsMaxCompletionTokens"/>
    /// (gpt-5 / o-series, the Microsoft family, or the forcing model arg). Port-only: the family's reasoning fields for
    /// <c>ReasoningEffort</c> / <c>ReasoningTokens</c> follow (<see cref="ReasoningRequestParams"/>).
    /// Every other config field is silently ignored.
    /// </summary>
    public JsonObject CompletionParams(GenerateConfig config)
    {
        var parameters = new JsonObject();
        if (config.FrequencyPenalty is not null)
        {
            parameters["frequency_penalty"] = config.FrequencyPenalty;
        }

        if (config.PresencePenalty is not null)
        {
            parameters["presence_penalty"] = config.PresencePenalty;
        }

        if (config.Temperature is not null)
        {
            parameters["temperature"] = config.Temperature;
        }

        if (config.TopP is not null)
        {
            parameters["top_p"] = config.TopP;
        }

        if (config.MaxTokens is not null)
        {
            parameters[SendsMaxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = config.MaxTokens;
        }

        if (config.StopSeqs is not null)
        {
            parameters["stop"] = new JsonArray(config.StopSeqs.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        }

        if (config.Seed is not null)
        {
            parameters["seed"] = config.Seed;
        }

        foreach (var (key, value) in ReasoningRequestParams(config))
        {
            parameters[key] = value?.DeepClone();
        }

        // Port-only: Python's azureai provider ignores response_schema; the chat-completions gateway accepts the
        // OpenAI response_format shape, so it is forwarded like the openai provider does (extended validation
        // fields stripped, as for tool schemas on this route). It travels as a pass-through extra.
        if (config.ResponseSchema is { } responseSchema)
        {
            parameters["response_format"] = ResponseFormat.JsonSchemaResponseFormat(responseSchema, JsonSchemaDump.JsonSchemaExtendedFields);
        }

        return parameters;
    }

    /// <summary>
    /// Port-only: the reasoning fields derived for this family from <c>config.ReasoningEffort</c> /
    /// <c>ReasoningTokens</c> (<see cref="ReasoningParams.RequestParams"/>); empty when neither is set.
    /// They travel as pass-through extras and are recorded in the <see cref="ModelCall"/>; a model arg with
    /// the same key overrides them on the wire (the recorded request then shows the derived value, as the
    /// Python snapshot excludes model extras).
    /// </summary>
    public JsonObject ReasoningRequestParams(GenerateConfig config) => ReasoningParams.RequestParams(FamilyHint, config);

    /// <summary>Port of <c>resolve_streaming</c>: explicit setting wins, otherwise stream iff an on_stream consumer is installed.</summary>
    public bool ResolveStreaming() => Streaming ?? ModelStreamObserver.ModelStreamRequested();

    /// <summary>
    /// Convenience overload installing a <see cref="ModelStreamObserver"/> for <paramref name="onStream"/>
    /// (what <c>Model.generate(on_stream=...)</c> does around the provider call).
    /// </summary>
    public async Task<GenerateResult> GenerateAsync(
        IReadOnlyList<ChatMessage> input,
        IReadOnlyList<ToolInfo> tools,
        ToolChoice toolChoice,
        GenerateConfig config,
        StreamHandler? onStream,
        CancellationToken cancellationToken = default)
    {
        if (onStream is null)
        {
            return await GenerateAsync(input, tools, toolChoice, config, cancellationToken).ConfigureAwait(false);
        }

        using (ModelStreamObserver.Install(new ModelStreamObserver(ModelName, onStream)))
        {
            return await GenerateAsync(input, tools, toolChoice, config, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Port of <c>generate()</c>. Returns the <see cref="ModelOutput"/> (or, for a terminal 400, the
    /// exception) together with the recorded <see cref="ModelCall"/>. Retryable Azure failures are
    /// thrown so the caller can consult <see cref="ShouldRetry"/>.
    /// </summary>
    public async Task<GenerateResult> GenerateAsync(
        IReadOnlyList<ChatMessage> input,
        IReadOnlyList<ToolInfo> tools,
        ToolChoice toolChoice,
        GenerateConfig config,
        CancellationToken cancellationToken = default)
    {
        var streaming = ResolveStreaming();
        var options = new ChatCompletionsOptions();
        foreach (var message in AzureMessageConversion.ChatRequestMessages(input, IsMistral()))
        {
            options.Messages.Add(message);
        }

        var completionParams = CompletionParams(config);
        ApplyCompletionParams(options, completionParams);
        var sendTools = tools.Count > 0;
        if (sendTools)
        {
            foreach (var tool in AzureToolConversion.ChatTools(tools))
            {
                options.Tools.Add(tool);
            }

            options.ToolChoice = AzureToolConversion.ChatToolChoice(toolChoice);
        }

        options.Model = ServiceModelName();
        foreach (var (key, value) in _modelArgs)
        {
            options.AdditionalProperties[key] = BinaryData.FromObjectAsJson(value);
        }

        // Entra ID (az login, managed identity, ...): the SDK's bearer-token policy sends only
        // `Authorization: Bearer` and refreshes the token itself; Credential pins the scope to
        // AZUREAI_AUDIENCE instead of the SDK's ml.azure.com default. Python tunnels the token through
        // AzureKeyCredential, which also puts it in `api-key` — a header Azure gateways may reject.
        var client = new ChatCompletionsClient(new Uri(EndpointUrl), Credential, CreateClientOptions(passThrough: options.AdditionalProperties.Count > 0));

        var modelCall = ModelCall.Create(RequestSnapshot(options, completionParams, streaming, sendTools), OpenAIUtil.OpenAIMediaFilter);

        try
        {
            AzureChatCompletions response;
            if (streaming)
            {
                using var streamingResponse = await client.CompleteStreamingAsync(options, cancellationToken).ConfigureAwait(false);
                var contentStream = streamingResponse.GetRawResponse().ContentStream
                                    ?? throw new ServiceResponseException("Streaming response carried no body.");
                await using (contentStream.ConfigureAwait(false))
                {
                    response = await ReadStreamAsync(contentStream, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                var completion = await client.CompleteAsync(options, cancellationToken).ConfigureAwait(false);
                response = AzureChatCompletions.FromJson(completion.GetRawResponse().Content);
            }

            modelCall.SetResponse(response.ToJson());

            if (streaming && response.Usage is null)
            {
                ProviderLogger.WarnOnce(
                    $"azureai model '{ModelName}' reported no token usage for a streamed response; pass -M streaming=false if you require usage reporting.");
            }

            var output = new ModelOutput
            {
                Model = response.Model,
                Choices = ChatCompletionChoices(response.Model, response.Choices),
                Usage = response.Usage is { } usage
                    ? new ModelUsage(usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens)
                    {
                        ReasoningTokens = usage.ReasoningTokens,
                        InputTokensCacheRead = usage.CachedTokens,
                    }
                    : null,
            };
            return new GenerateResult(output, null, modelCall);
        }
        catch (Exception ex) when (AsAzureError(ex, cancellationToken) is { } azureError)
        {
            modelCall.SetError(new JsonObject { ["error"] = new JsonObject { ["message"] = AzureErrorMessage(azureError) } });
            return HandleAzureError(azureError, modelCall);
        }
    }

    /// <summary>
    /// Maps what the .NET SDK throws onto the <c>AzureError</c> family the Python <c>except AzureError</c>
    /// clause catches, or returns null for anything that is not an Azure error (which then propagates
    /// unrecorded and unretried, like a Python non-<c>AzureError</c>):
    /// <list type="bullet">
    /// <item><see cref="RequestFailedException"/> (HTTP error, or <c>Status == 0</c> for a connection
    /// failure ↔ <c>ServiceRequestError</c>) and <see cref="ServiceResponseException"/> pass through;</item>
    /// <item><see cref="IOException"/> and a timeout <see cref="OperationCanceledException"/> (one not
    /// caused by <paramref name="cancellationToken"/> — Azure.Core's network timeout surfaces as a
    /// <see cref="TaskCanceledException"/>) become <see cref="ServiceResponseException"/>, Python's
    /// <c>ServiceResponseError</c>, on both the streaming and the non-streaming path;</item>
    /// <item>the <see cref="AggregateException"/> Azure.Core's retry policy throws once its own attempts
    /// are exhausted is unwrapped to its last inner exception (azure-core re-raises the last
    /// <c>AzureError</c>), which is then mapped by the same rules.</item>
    /// </list>
    /// </summary>
    public static Exception? AsAzureError(Exception ex, CancellationToken cancellationToken = default)
    {
        switch (ex)
        {
            case RequestFailedException or ServiceResponseException:
                return ex;
            case IOException:
                return new ServiceResponseException(ex.Message, ex);
            case OperationCanceledException when !cancellationToken.IsCancellationRequested:
                return new ServiceResponseException(ex.Message, ex);
            case AggregateException { InnerExceptions.Count: > 0 } aggregate:
                return AsAzureError(aggregate.InnerExceptions[^1], cancellationToken);
            default:
                return null;
        }
    }

    /// <summary>
    /// Port of <c>handle_azure_error</c>: an HTTP error mentioning "maximum context length" becomes a
    /// <c>model_length</c> output, an HTTP 400 is returned as the terminal error, everything else is
    /// re-thrown for retry classification. Expects an exception already normalised by <see cref="AsAzureError"/>.
    /// </summary>
    public GenerateResult HandleAzureError(Exception ex, ModelCall modelCall)
    {
        if (ex is RequestFailedException { Status: > 0 } http)
        {
            var response = AzureErrorMessage(http);
            if (response.ToLowerInvariant().Contains("maximum context length"))
            {
                return new GenerateResult(ModelOutput.FromContent(ModelName, response, StopReason.ModelLength), null, modelCall);
            }

            if (http.Status == 400)
            {
                return new GenerateResult(null, http, modelCall);
            }
        }

        ExceptionDispatchInfo.Capture(ex).Throw();
        throw ex;
    }

    /// <summary>
    /// The service error message (<c>str(ex.message)</c>): Azure.Core appends status/content/header
    /// dumps to <c>RequestFailedException.Message</c>, so only the leading message line is kept.
    /// </summary>
    public static string AzureErrorMessage(Exception ex)
    {
        if (ex is RequestFailedException)
        {
            var message = ex.Message;
            var cut = message.IndexOf("\nStatus:", StringComparison.Ordinal);
            return cut >= 0 ? message[..cut] : message;
        }

        return ex.Message;
    }

    /// <summary>Port of <c>chat_completion_choices</c>: choices sorted by index.</summary>
    public static List<ChatCompletionChoice> ChatCompletionChoices(string model, IReadOnlyList<AzureChatChoice> choices) =>
        choices.OrderBy(c => c.Index).Select(choice => ChatCompletionChoice(model, choice)).ToList();

    /// <summary>Port of <c>chat_complection_choice</c> (sic): message, stop reason and best-effort stop details.</summary>
    public static ChatCompletionChoice ChatCompletionChoice(string model, AzureChatChoice choice) =>
        new(
            ChatCompletionAssistantMessage(model, choice.Message),
            ChatCompletionStopReason(choice.FinishReason),
            ModelOutputUtil.CollectStopDetails("azureai", () => OpenAIUtil.OpenAIStopDetails(choice.Raw)));

    /// <summary>
    /// Port of <c>chat_completion_assistant_message</c>: the native <c>tool_calls</c>, parsed, plus the text.
    /// When the model exposes its reasoning (<see cref="AzureChatResponseMessage.ReasoningContent"/>) it is
    /// placed first as <see cref="ContentReasoning"/>, the Inspect convention; Cohere's text markers are
    /// stripped from the parsed text (the raw response on the <see cref="ModelCall"/> keeps them).
    /// </summary>
    public static ChatMessageAssistant ChatCompletionAssistantMessage(string model, AzureChatResponseMessage response)
    {
        var text = StripCohereTextMarkers(response.Content ?? "");
        var toolCalls = response.ToolCalls?.Select(call => ToolCallParsing.ParseToolCall(call.Id, call.Name, call.Arguments)).ToList();
        MessageContent content = response.ReasoningContent is { } reasoning
            ? MessageContent.FromItems([new ContentReasoning(reasoning), new ContentText(text)])
            : text;
        return new ChatMessageAssistant(content, toolCalls, model);
    }

    /// <summary>
    /// Cohere command deployments on the model-inference route wrap the answer in
    /// <c>&lt;|START_TEXT|&gt;…&lt;|END_TEXT|&gt;</c>; the markers are removed (a missing end marker, e.g. after a
    /// length cut-off, is tolerated). Text without the start marker is returned unchanged.
    /// </summary>
    internal static string StripCohereTextMarkers(string text)
    {
        const string start = "<|START_TEXT|>";
        const string end = "<|END_TEXT|>";
        if (!text.StartsWith(start, StringComparison.Ordinal))
        {
            return text;
        }

        text = text[start.Length..];
        return text.EndsWith(end, StringComparison.Ordinal) ? text[..^end.Length] : text;
    }

    /// <summary>Port of <c>chat_completion_stop_reason</c> over the wire <c>finish_reason</c>.</summary>
    public static StopReason ChatCompletionStopReason(string? reason) => reason switch
    {
        "stop" => StopReason.Stop,
        "length" => StopReason.MaxTokens,
        "content_filter" => StopReason.ContentFilter,
        "tool_calls" => StopReason.ToolCalls,
        _ => StopReason.Unknown,
    };

    /// <summary>Port of <c>_is_mistral_model</c>.</summary>
    public static bool IsMistralModel(string name) => name.ToLowerInvariant().Contains("mistral");

    /// <summary>Port of <c>_is_openai_model</c>: gpt-*, o1*, o3*, o4*.</summary>
    public static bool IsOpenAIModelName(string name)
    {
        name = name.ToLowerInvariant();
        return name.StartsWith("gpt-", StringComparison.Ordinal)
               || name.StartsWith("o1", StringComparison.Ordinal)
               || name.StartsWith("o3", StringComparison.Ordinal)
               || name.StartsWith("o4", StringComparison.Ordinal);
    }

    /// <summary>
    /// The request snapshot recorded in the <see cref="ModelCall"/>: messages and tools in their wire form
    /// (<c>as_dict()</c>), the completion params, <c>stream</c> when streaming, <c>tools: null</c> when no
    /// tools were sent, and <c>tool_choice</c> only alongside tools — never <c>model</c> or the model
    /// extras, matching the Python snapshot.
    /// </summary>
    internal static JsonObject RequestSnapshot(ChatCompletionsOptions options, JsonObject completionParams, bool streaming, bool sendTools)
    {
        var body = JsonNode.Parse(ModelReaderWriter.Write(options).ToString())!.AsObject();
        var request = new JsonObject { ["messages"] = body["messages"]?.DeepClone() ?? new JsonArray() };
        foreach (var (key, value) in completionParams)
        {
            request[key] = value?.DeepClone();
        }

        if (streaming)
        {
            request["stream"] = true;
        }

        request["tools"] = sendTools ? body["tools"]?.DeepClone() : null;
        if (sendTools)
        {
            request["tool_choice"] = body["tool_choice"]?.DeepClone();
        }

        return request;
    }

    private static void ApplyCompletionParams(ChatCompletionsOptions options, JsonObject completionParams)
    {
        foreach (var (key, value) in completionParams)
        {
            switch (key)
            {
                case "frequency_penalty":
                    options.FrequencyPenalty = (float)value!.GetValue<double>();
                    break;
                case "presence_penalty":
                    options.PresencePenalty = (float)value!.GetValue<double>();
                    break;
                case "temperature":
                    options.Temperature = (float)value!.GetValue<double>();
                    break;
                case "top_p":
                    options.NucleusSamplingFactor = (float)value!.GetValue<double>();
                    break;
                case "max_tokens":
                    options.MaxTokens = value!.GetValue<int>();
                    break;
                case "stop":
                    foreach (var stop in value!.AsArray())
                    {
                        options.StopSequences.Add(stop!.GetValue<string>());
                    }

                    break;
                case "seed":
                    options.Seed = value!.GetValue<int>();
                    break;
                default:
                    // max_completion_tokens and response_format are not declared SDK options (the SDK's json_schema
                    // response-format types are internal); they travel as pass-through extras.
                    options.AdditionalProperties[key] = BinaryData.FromString(value!.ToJsonString());
                    break;
            }
        }
    }

    private AzureAIInferenceClientOptions CreateClientOptions(bool passThrough)
    {
        var clientOptions = new AzureAIInferenceClientOptions();
        if (Settings.Transport is not null)
        {
            clientOptions.Transport = Settings.Transport;
        }

        if (passThrough)
        {
            // The SDK stamps `extra-parameters: pass-through` only on the non-streaming path; the gateway
            // rejects unknown body fields without it, so streamed extras need the same header (Python sends
            // it on both paths whenever model_extras are present).
            clientOptions.AddPolicy(new PassThroughExtraParametersPolicy(), HttpPipelinePosition.PerCall);
        }

        // The SDK pipeline keeps its own retry policy underneath Inspect's should_retry loop, just as the
        // Python azure-core pipeline does; Settings.ConfigureClientOptions can tune or disable it.
        Settings.ConfigureClientOptions?.Invoke(clientOptions);
        return clientOptions;
    }

    /// <summary>
    /// Consumes the SSE body. Transport failures while reading are normalised by the caller's
    /// <see cref="AsAzureError"/>; a malformed chunk raises <see cref="JsonException"/>, which — like the
    /// <c>json.JSONDecodeError</c> the Python SDK raises — is not an Azure error and is neither recorded
    /// on the <see cref="ModelCall"/> nor retried.
    /// </summary>
    private static Task<AzureChatCompletions> ReadStreamAsync(Stream contentStream, CancellationToken cancellationToken) =>
        AzureAIStreamAccumulator.CompletionFromStreamAsync(SseParser.ReadUpdatesAsync(contentStream, cancellationToken), cancellationToken);

    private static string ReplaceFirst(string text, string search, string replacement)
    {
        var index = text.IndexOf(search, StringComparison.Ordinal);
        return index < 0 ? text : text[..index] + replacement + text[(index + search.Length)..];
    }
}

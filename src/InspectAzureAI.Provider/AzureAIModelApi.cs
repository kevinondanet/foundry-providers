using System.ClientModel.Primitives;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.AI.Inference;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Tools;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Provider;

/// <summary>
/// Port of <c>AzureAIAPI</c> (<c>src/inspect_ai/model/_providers/azureai.py</c>): the Inspect model
/// provider for Azure AI Foundry model-inference endpoints, built on the official
/// <see cref="ChatCompletionsClient"/>. Construction resolves credentials and the endpoint exactly like
/// the Python constructor; <c>GenerateAsync</c> reproduces <c>generate()</c> including tool
/// emulation, streaming accumulation, <see cref="ModelCall"/> capture and Azure error handling. The
/// <c>ModelAPI</c> hooks the Python class overrides (<see cref="MaxTokens"/>, <see cref="ShouldRetry"/>,
/// <see cref="IsAuthFailure"/>, <see cref="CollapseUserMessages"/>, <see cref="ConnectionKey"/>,
/// <see cref="CanonicalName"/>, <see cref="ServiceModelName"/>) are exposed as methods.
/// </summary>
public sealed class AzureAIModelApi
{
    public const string AzureAIApiKeyVar = "AZUREAI_API_KEY";

    public const string AzureAIBaseUrlVar = "AZUREAI_BASE_URL";

    public const string AzureAIEndpointUrlVar = "AZUREAI_ENDPOINT_URL";

    public const string AzureAIAudienceVar = AzureHosting.AzureAIAudience;

    /// <summary>Legacy (preferred) api-key variable.</summary>
    public const string AzureApiKeyVar = "AZURE_API_KEY";

    /// <summary>Legacy endpoint variable.</summary>
    public const string AzureEndpointUrlVar = "AZURE_ENDPOINT_URL";

    /// <summary>Port of <c>DEFAULT_MAX_TOKENS</c> (<c>src/inspect_ai/_util/constants.py</c>).</summary>
    public const int DefaultMaxTokens = 2048;

    /// <summary>Port of <c>DEFAULT_MAX_CONNECTIONS</c>.</summary>
    public const int DefaultMaxConnections = 10;

    private static readonly string[] ApiKeyVars = [AzureApiKeyVar, AzureAIApiKeyVar];

    private readonly Dictionary<string, object?> _modelArgs;

    /// <summary>
    /// Port of <c>AzureAIAPI.__init__</c>. <paramref name="streaming"/> accepts <c>true</c>/<c>false</c>
    /// or the strings <c>"auto"</c>/<c>"true"</c>/<c>"false"</c> (as <c>-M streaming=...</c> arrives); null is the same as <c>"auto"</c>;
    /// <paramref name="modelArgs"/> mirrors <c>**model_args</c>: <c>emulate_tools</c> is popped and the
    /// remainder is forwarded to the request body as <c>model_extras</c>.
    /// </summary>
    public AzureAIModelApi(
        string modelName,
        string? baseUrl = null,
        string? apiKey = null,
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
        InitialApiKey = apiKey;
        ApiKey = apiKey;
        ApplyApiKeyOverrides();

        _modelArgs = modelArgs is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(modelArgs);
        var emulateTools = CollectModelArg("emulate_tools");
        EmulateTools = emulateTools is not null ? PythonSemantics.Truthy(emulateTools) : null;

        if (string.IsNullOrEmpty(ApiKey))
        {
            // os.environ.get(AZURE_API_KEY, os.environ.get(AZUREAI_API_KEY)): a set-but-empty
            // AZURE_API_KEY is taken as-is and falls through to managed identity below.
            ApiKey = Environment.GetEnvironmentVariable(AzureApiKeyVar) ?? Environment.GetEnvironmentVariable(AzureAIApiKeyVar);
        }

        if (string.IsNullOrEmpty(ApiKey))
        {
            TokenProvider = AzureHosting.ResolveAzureTokenProvider("AzureAI", Settings.TokenCredential);
        }

        if (string.IsNullOrEmpty(ApiKey) && TokenProvider is null)
        {
            throw ProviderUtil.EnvironmentPrerequisiteError(
                "AzureAI",
                [AzureApiKeyVar, AzureAIApiKeyVar, "or managed identity (Entra ID)"]);
        }

        var endpointUrl = ProviderUtil.ModelBaseUrl(baseUrl, [AzureEndpointUrlVar, AzureAIEndpointUrlVar, AzureAIBaseUrlVar]);
        if (string.IsNullOrEmpty(endpointUrl))
        {
            throw ProviderUtil.EnvironmentPrerequisiteError("AzureAI", [AzureAIBaseUrlVar]);
        }

        EndpointUrl = endpointUrl;
    }

    /// <summary>Full model name including any org prefix (as it appears in logs).</summary>
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

    /// <summary>The api key argument as passed (frozen; used by <see cref="ConnectionKey"/>).</summary>
    public string? InitialApiKey { get; }

    /// <summary>Resolved api key (explicit, hook-overridden, or from the environment).</summary>
    public string? ApiKey { get; private set; }

    /// <summary>Entra ID token provider, set only when no api key was found.</summary>
    public TokenProvider? TokenProvider { get; }

    /// <summary>Tool emulation setting: null (auto), true, or false. Flips to true on the first generate for Llama models.</summary>
    public bool? EmulateTools { get; private set; }

    /// <summary>Resolved endpoint (stored verbatim; the SDK appends <c>/chat/completions?api-version=...</c>).</summary>
    public string EndpointUrl { get; }

    /// <summary>Leftover model args forwarded as <c>model_extras</c> (top-level JSON body fields).</summary>
    public IReadOnlyDictionary<string, object?> ModelArgs => _modelArgs;

    /// <summary>The audience/scope requested for Entra ID tokens.</summary>
    public static string TokenAudience => AzureHosting.ResolveAudience();

    /// <summary>Port of <c>ModelAPI.initialize()</c>: re-applies the api-key override hook.</summary>
    public void Initialize() => ApplyApiKeyOverrides();

    /// <summary>Port of <c>service_model_name</c>: the name without its org prefix, used on the wire.</summary>
    public string ServiceModelName() =>
        OrgPrefix is not null ? ReplaceFirst(ModelName, $"{OrgPrefix}/", "") : ModelName;

    /// <summary>
    /// Port of <c>ModelAPI.model_family</c>. Inspect consults its model-info registry first; the sample has
    /// no registry, so this is always <see cref="ServiceModelName"/>.
    /// </summary>
    public string ModelFamily() => ServiceModelName();

    public bool IsLlama() => IsLlamaModel(ModelFamily());

    public bool IsLlama3() => IsLlama3Model(ModelFamily());

    public bool IsMistral() => IsMistralModel(ModelFamily());

    public bool IsOpenAIModel() => IsOpenAIModelName(ModelFamily());

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

    /// <summary>Port of <c>max_tokens</c>: 2048 for Llama, null for Mistral, <see cref="DefaultMaxTokens"/> otherwise.</summary>
    public int? MaxTokens()
    {
        if (IsLlama())
        {
            return 2048;
        }

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

    /// <summary>Port of <c>connection_key</c>: <c>f"{initial_api_key}:{model_name}"</c> (a missing key renders as <c>None</c>).</summary>
    public string ConnectionKey() => $"{InitialApiKey ?? "None"}:{ModelName}";

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
    /// <c>max_tokens</c> is emitted as <c>max_completion_tokens</c> for gpt-5 / o-series families.
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
            parameters[OpenAIUtil.NeedsMaxCompletionTokens(ModelFamily()) ? "max_completion_tokens" : "max_tokens"] = config.MaxTokens;
        }

        if (config.StopSeqs is not null)
        {
            parameters["stop"] = new JsonArray(config.StopSeqs.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        }

        if (config.Seed is not null)
        {
            parameters["seed"] = config.Seed;
        }

        return parameters;
    }

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
        ChatApiHandler? handler;
        if (EmulateTools is null && IsLlama())
        {
            EmulateTools = true;
            handler = new Llama31Handler(ModelName);
        }
        else if (EmulateTools == true)
        {
            handler = new Llama31Handler(ModelName);
        }
        else
        {
            handler = null;
        }

        if (handler is not null)
        {
            input = handler.InputWithTools(input, tools);
        }

        var streaming = ResolveStreaming();
        var options = new ChatCompletionsOptions();
        foreach (var message in AzureMessageConversion.ChatRequestMessages(input, handler, IsMistral()))
        {
            options.Messages.Add(message);
        }

        var completionParams = CompletionParams(config);
        ApplyCompletionParams(options, completionParams);
        var sendTools = EmulateTools != true && tools.Count > 0;
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

        AzureKeyCredential credential;
        if (!string.IsNullOrEmpty(ApiKey))
        {
            credential = new AzureKeyCredential(ApiKey);
        }
        else if (TokenProvider is not null)
        {
            credential = new AzureKeyCredential(await TokenProvider(cancellationToken).ConfigureAwait(false));
        }
        else
        {
            throw new PrerequisiteError("Azure AI must have either an API key or token provider.");
        }

        var client = new ChatCompletionsClient(new Uri(EndpointUrl), credential, CreateClientOptions());
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
                Choices = ChatCompletionChoices(response.Model, response.Choices, tools, handler),
                Usage = response.Usage is { } usage
                    ? new ModelUsage(usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens)
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
    public static List<ChatCompletionChoice> ChatCompletionChoices(string model, IReadOnlyList<AzureChatChoice> choices, IReadOnlyList<ToolInfo> tools, ChatApiHandler? handler) =>
        choices.OrderBy(c => c.Index).Select(choice => ChatCompletionChoice(model, choice, tools, handler)).ToList();

    /// <summary>Port of <c>chat_complection_choice</c> (sic): message, stop reason and best-effort stop details.</summary>
    public static ChatCompletionChoice ChatCompletionChoice(string model, AzureChatChoice choice, IReadOnlyList<ToolInfo> tools, ChatApiHandler? handler) =>
        new(
            ChatCompletionAssistantMessage(model, choice.Message, tools, handler),
            ChatCompletionStopReason(choice.FinishReason),
            ModelOutputUtil.CollectStopDetails("azureai", () => OpenAIUtil.OpenAIStopDetails(choice.Raw)));

    /// <summary>Port of <c>chat_completion_assistant_message</c>.</summary>
    public static ChatMessageAssistant ChatCompletionAssistantMessage(string model, AzureChatResponseMessage response, IReadOnlyList<ToolInfo> tools, ChatApiHandler? handler)
    {
        if (handler is not null)
        {
            return handler.ParseAssistantResponse(response.Content ?? "", tools);
        }

        return new ChatMessageAssistant(
            response.Content ?? "",
            response.ToolCalls?.Select(call => ToolCallParsing.ParseToolCall(call.Id, call.Name, call.Arguments, tools)).ToList(),
            model);
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

    /// <summary>Port of <c>_is_llama_model</c>.</summary>
    public static bool IsLlamaModel(string name) => name.ToLowerInvariant().Contains("llama");

    /// <summary>Port of <c>_is_llama3_model</c>.</summary>
    public static bool IsLlama3Model(string name) => name.ToLowerInvariant().Contains("llama-3");

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
                    // max_completion_tokens is not a declared SDK option; it travels as a pass-through extra.
                    options.AdditionalProperties[key] = BinaryData.FromString(value!.ToJsonString());
                    break;
            }
        }
    }

    private AzureAIInferenceClientOptions CreateClientOptions()
    {
        var clientOptions = new AzureAIInferenceClientOptions();
        if (Settings.Transport is not null)
        {
            clientOptions.Transport = Settings.Transport;
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

    private object? CollectModelArg(string name)
    {
        if (_modelArgs.TryGetValue(name, out var value) && value is not null)
        {
            _modelArgs.Remove(name);
            return value;
        }

        return null;
    }

    /// <summary>Port of <c>ModelAPI._apply_api_key_overrides</c>.</summary>
    private void ApplyApiKeyOverrides()
    {
        var apiKey = ApiKey;
        foreach (var key in ApiKeyVars)
        {
            if (apiKey is not null)
            {
                var overrideValue = ModelApiHooks.OverrideApiKey?.Invoke(key, apiKey);
                if (overrideValue is not null)
                {
                    apiKey = overrideValue;
                }
            }
            else
            {
                var value = Environment.GetEnvironmentVariable(key);
                if (value is not null)
                {
                    var overrideValue = ModelApiHooks.OverrideApiKey?.Invoke(key, value);
                    if (overrideValue is not null)
                    {
                        Environment.SetEnvironmentVariable(key, overrideValue);
                    }
                }
                else if (ModelApiHooks.HasApiKeyOverride)
                {
                    var overrideValue = ModelApiHooks.OverrideApiKey?.Invoke(key, "");
                    if (overrideValue is not null)
                    {
                        apiKey = overrideValue;
                    }
                }
            }
        }

        ApiKey = apiKey;
    }

    private static string ReplaceFirst(string text, string search, string replacement)
    {
        var index = text.IndexOf(search, StringComparison.Ordinal);
        return index < 0 ? text : text[..index] + replacement + text[(index + search.Length)..];
    }
}

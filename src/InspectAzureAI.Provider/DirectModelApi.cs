using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Provider;

/// <summary>Direct API transport; protocol implementations own request and response conversion.</summary>
public abstract class DirectModelApi : IModelApi, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private protected DirectProviderOptions Options { get; }
    private protected DirectModelApi(string modelName, GenerateConfig? config, DirectProviderOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ModelName = modelName;
        Options = options;
        Config = (config ?? new()) with
        {
            MaxRetries = config?.MaxRetries ?? options.MaxRetries,
        };
        _ownsHttp = options.Settings.HttpClient is null;
        _http = options.Settings.HttpClient ?? (options.Settings.Handler is { } handler ? new HttpClient(handler, false) : new HttpClient());
        if (_ownsHttp) _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
    }
    public string ModelName { get; }
    public string ProviderName => Options.Provider;
    public string QualifiedModelName => $"{ProviderName}/{ModelName}";
    public string BaseUrl => Options.BaseUrl;
    public GenerateConfig DefaultConfig => Config;
    public GenerateConfig Config { get; }
    public IReadOnlyDictionary<string, object?> ModelArgsForLog => Options.ForLog;
    public string ConnectionKey() => $"{BaseUrl}:{Options.AccountFingerprint}:{ModelName}";
    public virtual int? MaxTokens() => null;
    public virtual int? MaxTokensForConfig(GenerateConfig config) => MaxTokens();
    public virtual bool CollapseUserMessages() => false;
    public virtual bool SupportsRemoteMcp() => false;
    public virtual bool ApplyRedactedReasoningTokensToInput() => false;
    public RetryDecision ShouldRetry(Exception ex) => IsAuthFailure(ex) && Options.Settings.ApiKeyOverride is not null ? RetryDecision.Transient() : HttpRetryUtil.RetryDecisionFor(ex);
    public bool IsAuthFailure(Exception ex) => HttpRetryUtil.StatusCodeOf(ex) == 401;
    public abstract JsonObject BuildRequest(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, ToolChoice toolChoice, GenerateConfig config, bool streaming);
    protected abstract string RequestPath(GenerateConfig config);
    protected abstract ModelOutput ParseOutput(JsonObject response, GenerateConfig config);
    protected abstract Task<JsonObject> AccumulateAsync(IAsyncEnumerable<JsonObject> events, GenerateConfig config, CancellationToken cancellationToken);
    protected virtual bool AutoStream(GenerateConfig config) => ModelStreamObserver.ModelStreamRequested();
    protected virtual void AddHeaders(HttpRequestMessage request, GenerateConfig config) { }
    protected virtual Task<JsonObject> CompleteResponseAsync(JsonObject response, GenerateConfig config, CancellationToken cancellationToken) => Task.FromResult(response);

    public Task<GenerateResult> GenerateAsync(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, ToolChoice toolChoice, GenerateConfig config, CancellationToken cancellationToken = default) => GenerateAsync(input, tools, toolChoice, config, null, cancellationToken);
    public async Task<GenerateResult> GenerateAsync(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, ToolChoice toolChoice, GenerateConfig config, StreamHandler? onStream, CancellationToken cancellationToken = default)
    {
        var merged = Config with { };
        foreach (var property in typeof(GenerateConfig).GetProperties())
            if (property.CanWrite && property.GetValue(config) is { } value) property.SetValue(merged, value);
        config = merged;
        if (config.MaxTokens is null) config = config with { MaxTokens = MaxTokensForConfig(config) };
        using var observer = onStream is null ? null : ModelStreamObserver.Install(new(ModelName, onStream));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (Options.Timeout is { } seconds) timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        var token = timeout.Token;
        var streaming = Options.Streaming ?? AutoStream(config);
        try
        {
            try { return await GenerateOnce(streaming).ConfigureAwait(false); }
            catch (ProviderHttpException ex) when (ProviderName == "openai" && streaming && Options.Streaming is null && ex.Status == 400 && ex.Error?["param"]?.ToString() == "stream")
            {
                ProviderLogger.WarnOnce($"{QualifiedModelName}: automatic streaming rejected; retrying without streaming.");
                return await GenerateOnce(false).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ServiceResponseException("Direct provider request timed out.", ex);
        }

        async Task<GenerateResult> GenerateOnce(bool useStream)
        {
            var body = BuildRequest(input, tools, toolChoice, config, useStream);
            var call = ModelCall.Create(body, OpenAIUtil.OpenAIMediaFilter);
            var watch = Stopwatch.StartNew();
            try
            {
                using var response = await SendAsync(HttpMethod.Post, RequestPath(config), body, config, token).ConfigureAwait(false);
                JsonObject result;
                if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                {
                    ModelStreamObserver.ReportModelStreamStart();
                    await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    result = await AccumulateAsync(SseParser.ReadUpdatesAsync(stream, token), config, token).ConfigureAwait(false);
                }
                else result = JsonNode.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false))?.AsObject() ?? throw new JsonException("Empty response.");
                result = await CompleteResponseAsync(result, config, token).ConfigureAwait(false);
                call.SetResponse(result, watch.Elapsed.TotalSeconds);
                return new GenerateResult(ParseOutput(result, config) with { Time = watch.Elapsed.TotalSeconds }, null, call);
            }
            catch (ProviderHttpException ex)
            {
                call.SetError(ex.Error ?? JsonValue.Create(ex.Message), watch.Elapsed.TotalSeconds);
                if (ProviderName == "openai" && ex.Status is 400 or 422 && OpenAI.ResponsesOutput.RefusalOutput(ModelName, ex.Error?["code"]?.ToString(), ex.Error?["type"]?.ToString(), ex.Error?["message"]?.ToString() ?? "") is { } refusal)
                    return new GenerateResult(refusal, null, call);
                throw;
            }
        }
    }
    protected async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, JsonObject? body, GenerateConfig config, CancellationToken cancellationToken)
    {
        if (config.ExtraHeaders?.Keys.Any(ModelArgumentSanitizer.IsSecret) == true)
            throw new PrerequisiteError("Authentication headers are not accepted in extra_headers. Use api_key or DirectClientSettings.ApiKeyOverride.");
        using var request = new HttpRequestMessage(method, BaseUrl + path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        foreach (var pair in Options.Headers.Concat(config.ExtraHeaders ?? new Dictionary<string,string>()))
        {
            if (!ModelArgumentSanitizer.IsSecret(pair.Key))
            {
                request.Headers.Remove(pair.Key);
                request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }
        }
        AddHeaders(request, config);
        var key = Options.ResolveKey();
        if (ProviderName == "anthropic") request.Headers.Add("x-api-key", key);
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            var headers = response.Headers.Concat(response.Content.Headers).ToDictionary(p => p.Key, p => string.Join(",", p.Value), StringComparer.OrdinalIgnoreCase);
            throw new ProviderHttpException((int)response.StatusCode, headers, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
    }
    protected Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) => (Options.Settings.Delay ?? Task.Delay)(duration, cancellationToken);
    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}

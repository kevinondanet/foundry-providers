using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tools.Builtin;

/// <summary>
/// Port of <c>httpx.HTTPStatusError</c> as the web search providers raise it: a non-success HTTP status,
/// carrying the status and the response body. Not a <see cref="ToolError"/>, so it is fatal to the sample as
/// in Python (Tavily turns its query-too-long 400 into a <see cref="ToolError"/> before it gets that far).
/// </summary>
public sealed class HttpStatusException(int statusCode, string body, string message)
    : HttpRequestException(message, null, (HttpStatusCode)statusCode)
{
    /// <summary>The HTTP status code.</summary>
    public int Status { get; } = statusCode;

    /// <summary>The response body.</summary>
    public string Body { get; } = body;
}

/// <summary>
/// Port of <c>BaseHttpProvider</c> (<c>tool/_tools/_web_search/_base_http_provider.py</c>): the shared shape of
/// the HTTP web search providers (Tavily, Exa, and the .NET-only Perplexity HTTP fallback). The constructor validates the API key from the environment
/// (<see cref="PrerequisiteError"/> when unset), strips the Inspect-only <c>max_connections</c> option
/// (default 10) and applies the provider's default options. <see cref="SearchAsync"/> POSTs
/// <c>{"query": ..., ...options}</c> under a named concurrency limit, retrying HTTP 408/429/5xx and transport
/// failures with exponential jitter (up to 5 attempts within 60 seconds, as tenacity does), and hands the JSON
/// body to <see cref="ParseResponse"/>.
/// </summary>
public abstract class BaseHttpProvider : IDisposable
{
    /// <summary>The httpx client timeout Python uses.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary><c>stop_after_attempt(5)</c>.</summary>
    public const int MaxAttempts = 5;

    /// <summary><c>stop_after_delay(60)</c>.</summary>
    public static readonly TimeSpan MaxRetryDuration = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;

    /// <summary>Port of <c>BaseHttpProvider.__init__</c>: reads <c>max_connections</c>, applies the provider defaults and validates the API key.</summary>
    protected BaseHttpProvider(
        string envKeyName,
        string apiEndpoint,
        string providerName,
        string concurrencyKey,
        JsonObject? options = null,
        HttpMessageHandler? handler = null)
    {
        EnvKeyName = envKeyName;
        ApiEndpoint = apiEndpoint;
        ProviderName = providerName;
        ConcurrencyKey = concurrencyKey;
        MaxConnections = ExtractMaxConnections(options);
        ApiOptions = PrepareApiOptions(options);
        ApiKey = ValidateApiKey();
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = RequestTimeout;
    }

    /// <summary>Environment variable holding the API key.</summary>
    public string EnvKeyName { get; }

    /// <summary>The search endpoint the query is POSTed to.</summary>
    public string ApiEndpoint { get; }

    /// <summary>Human readable provider name ("Tavily", "Exa").</summary>
    public string ProviderName { get; }

    /// <summary>Key of the named concurrency limit shared by every instance of the provider.</summary>
    public string ConcurrencyKey { get; }

    /// <summary>Maximum concurrent requests (the <c>max_connections</c> option, default 10).</summary>
    public int MaxConnections { get; }

    /// <summary>The options sent with every request (caller options minus <c>max_connections</c>, plus provider defaults).</summary>
    public JsonObject ApiOptions { get; }

    /// <summary>The API key read from <see cref="EnvKeyName"/>.</summary>
    protected string ApiKey { get; }

    /// <summary>Waits between retries; tests replace it to avoid sleeping.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>The jitter source (a uniform sample in [0, 1)); tests replace it for determinism.</summary>
    internal Func<double> Jitter { get; set; } = Random.Shared.NextDouble;

    /// <summary>Port of <c>prepare_headers</c>: the HTTP headers for the request.</summary>
    public abstract IReadOnlyDictionary<string, string> PrepareHeaders(string apiKey);

    /// <summary>Port of <c>parse_response</c>: the answer with citations, or null when the provider found nothing.</summary>
    public abstract ContentText? ParseResponse(JsonObject responseData);

    /// <summary>Port of <c>set_default_options</c>: applies provider-specific defaults to the caller's options (returns a new object).</summary>
    public abstract JsonObject SetDefaultOptions(JsonObject options);

    /// <summary>Port of <c>search</c>: executes the query under the provider's concurrency limit, with retries.</summary>
    public virtual async Task<ContentText?> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        using var lease = await NamedConcurrency.EnterAsync(ConcurrencyKey, MaxConnections, cancellationToken).ConfigureAwait(false);
        var body = await PostWithRetryAsync(query, cancellationToken).ConfigureAwait(false);
        JsonObject data;
        try
        {
            data = JsonNode.Parse(body) as JsonObject ?? throw new InvalidDataException($"{ProviderName} returned a response that is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{ProviderName} returned a response that is not valid JSON: {ex.Message}", ex);
        }

        return ParseResponse(data);
    }

    /// <summary>The request body: <c>{"query": query, **api_options}</c> (a provider with a different wire shape overrides it).</summary>
    public virtual JsonObject RequestBody(string query)
    {
        var body = new JsonObject { ["query"] = query };
        foreach (var (key, value) in ApiOptions)
        {
            body[key] = value?.DeepClone();
        }

        return body;
    }

    /// <summary>The wait before retry number <paramref name="attempt"/> (tenacity's <c>wait_exponential_jitter()</c>: min(2^(n-1) + U[0,1), 10) seconds).</summary>
    internal TimeSpan RetryWait(int attempt) => TimeSpan.FromSeconds(Math.Max(0, Math.Min(Math.Pow(2, attempt - 1) + Jitter(), 10)));

    /// <summary>Port of <c>httpx_should_retry</c>: retryable statuses, and transport failures without a status.</summary>
    internal static bool ShouldRetry(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        HttpStatusException status => HttpRetryUtil.IsRetryableHttpStatus(status.Status),
        HttpRequestException => true,
        IOException => true,
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        _ => false,
    };

    private async Task<string> PostWithRetryAsync(string query, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await PostOnceAsync(query, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ShouldRetry(ex, cancellationToken) && attempt < MaxAttempts && watch.Elapsed < MaxRetryDuration)
            {
                var wait = RetryWait(attempt);
                ProviderLogger.Info($"{ApiEndpoint} connection retry {attempt} (retrying in {wait.TotalSeconds.ToString("N0", CultureInfo.InvariantCulture)} seconds)");
                await Delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<string> PostOnceAsync(string query, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiEndpoint)
        {
            Content = new StringContent(RequestBody(query).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in PrepareHeaders(ApiKey))
        {
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var kind = status >= 500 ? "Server error" : "Client error";
            throw new HttpStatusException(status, body, $"{kind} '{status} {response.ReasonPhrase}' for url '{ApiEndpoint}'");
        }

        return body;
    }

    private static int ExtractMaxConnections(JsonObject? options)
    {
        if (options is null || !options.TryGetPropertyValue("max_connections", out var node) || node is null)
        {
            return 10;
        }

        return node is JsonValue value && value.TryGetValue<int>(out var max)
            ? max
            : throw new ArgumentException($"max_connections must be an integer, got {ToolInputValidator.Repr(node)}", nameof(options));
    }

    private JsonObject PrepareApiOptions(JsonObject? options)
    {
        var apiOptions = new JsonObject();
        if (options is not null)
        {
            foreach (var (key, value) in options)
            {
                if (key != "max_connections")
                {
                    apiOptions[key] = value?.DeepClone();
                }
            }
        }

        return SetDefaultOptions(apiOptions);
    }

    private string ValidateApiKey()
    {
        var apiKey = Environment.GetEnvironmentVariable(EnvKeyName);
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new PrerequisiteError(
                $"{EnvKeyName} not set in the environment. Please ensure this variable is defined to use {ProviderName} with the web_search tool.\n\n"
                + $"Learn more about the {ProviderName} web search provider at https://inspect.aisi.org.uk/tools.html#{ProviderName.ToLowerInvariant()}-provider");
        }

        return apiKey;
    }

    /// <summary>Releases the HTTP client. A <see cref="SearchProvider"/> created from a provider keeps it for the tool's lifetime, as Python keeps its httpx client.</summary>
    public void Dispose()
    {
        _http.Dispose();
        GC.SuppressFinalize(this);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tools.Builtin;

/// <summary>
/// The Perplexity web search provider as an HTTP fallback. In Python "perplexity" is an internal provider:
/// <c>model/_providers/perplexity.py</c> forwards the <c>perplexity</c> options into the chat completions
/// request and turns the response's top-level <c>search_results</c> into <see cref="UrlCitation"/>s. The
/// Azure model providers cannot do that, so this .NET-only provider makes the same call itself
/// (<c>POST https://api.perplexity.ai/chat/completions</c>, bearer <c>PERPLEXITY_API_KEY</c>, the query as the
/// user message, every other option forwarded verbatim as Python's <c>extra_body</c> does) and formats the
/// result the way the Python provider does: the answer text with one citation per search result (url and
/// title; Perplexity gives no cited text). It is used only when the caller listed "perplexity" explicitly.
/// </summary>
public sealed class PerplexitySearchProvider : BaseHttpProvider
{
    /// <summary>Environment variable holding the Perplexity API key.</summary>
    public const string EnvKey = "PERPLEXITY_API_KEY";

    /// <summary>The Perplexity endpoint the query is POSTed to.</summary>
    public const string Endpoint = "https://api.perplexity.ai/chat/completions";

    /// <summary>The model used when the options name none (Perplexity's search model).</summary>
    public const string DefaultModel = "sonar";

    /// <summary>The options this provider interprets itself; everything else is forwarded to the API.</summary>
    public static readonly IReadOnlyList<string> OptionNames = ["model", "max_connections"];

    private static readonly IReadOnlyList<SearchOptionField> OptionFields =
    [
        new("model", SearchOptionKind.String),
        new("max_connections", SearchOptionKind.Int),
    ];

    /// <summary>Creates the provider from validated options (see <see cref="ValidateOptions"/>); the API key is read from <see cref="EnvKey"/> now.</summary>
    public PerplexitySearchProvider(JsonObject? options = null, HttpMessageHandler? handler = null)
        : base(EnvKey, Endpoint, "Perplexity", "perplexity_web_search", options, handler)
    {
    }

    /// <summary>Checks <c>model</c> and <c>max_connections</c>; the other options are Perplexity search parameters and pass through unchanged.</summary>
    public static JsonObject ValidateOptions(JsonObject options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var validated = SearchOptionsValidator.Validate("PerplexityOptions", options, OptionFields);
        foreach (var (key, value) in options)
        {
            if (!OptionNames.Contains(key) && value is not null)
            {
                validated[key] = value.DeepClone();
            }
        }

        return validated;
    }

    /// <summary>Validates the options (an empty object counts as none) and wraps the provider's search as a <see cref="SearchProvider"/>.</summary>
    public static SearchProvider Create(JsonObject? inOptions = null, HttpMessageHandler? handler = null)
    {
        var options = inOptions is { Count: > 0 } ? ValidateOptions(inOptions) : null;
        var provider = new PerplexitySearchProvider(options, handler);
        return async (query, cancellationToken) =>
            await provider.SearchAsync(query, cancellationToken).ConfigureAwait(false) is { } text ? ToolResult.FromContents([text]) : null;
    }

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> PrepareHeaders(string apiKey) => new Dictionary<string, string>
    {
        ["Authorization"] = $"Bearer {apiKey}",
        ["Content-Type"] = "application/json",
    };

    /// <summary>Defaults <c>model</c> to <see cref="DefaultModel"/>.</summary>
    public override JsonObject SetDefaultOptions(JsonObject options)
    {
        var newOptions = options.DeepClone().AsObject();
        if (!newOptions.ContainsKey("model"))
        {
            newOptions["model"] = DefaultModel;
        }

        return newOptions;
    }

    /// <summary>A chat completions request: the query as the single user message, the options as top-level fields.</summary>
    public override JsonObject RequestBody(string query)
    {
        var body = new JsonObject
        {
            ["model"] = ApiOptions["model"]?.DeepClone() ?? DefaultModel,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = query }),
        };
        foreach (var (key, value) in ApiOptions)
        {
            if (key != "model")
            {
                body[key] = value?.DeepClone();
            }
        }

        return body;
    }

    /// <summary>The first choice's message text with a <see cref="UrlCitation"/> per <c>search_results</c> entry that has a url.</summary>
    public override ContentText? ParseResponse(JsonObject responseData)
    {
        ArgumentNullException.ThrowIfNull(responseData);
        var choices = ResponseFields.RequiredArray(responseData, "choices", "PerplexityResponse");
        var text = "";
        if (choices.Count > 0)
        {
            var message = (choices[0] as JsonObject)?["message"] as JsonObject
                ?? throw ResponseFields.Invalid("PerplexityResponse", "choices", "the first choice must carry a message");
            text = ResponseFields.OptionalString(message, "content", "PerplexityMessage") ?? "";
        }

        var citations = new List<Citation>();
        foreach (var item in ResponseFields.OptionalArray(responseData, "search_results", "PerplexityResponse") ?? [])
        {
            if (item is JsonObject result && result["url"] is JsonValue url && url.GetValueKind() == JsonValueKind.String)
            {
                citations.Add(new UrlCitation(url.GetValue<string>()) { Title = ResponseFields.OptionalString(result, "title", "PerplexitySearchResult") });
            }
        }

        if (text.Length == 0 && citations.Count == 0)
        {
            return null;
        }

        return new ContentText(text) { Citations = citations.Count > 0 ? citations : null };
    }
}

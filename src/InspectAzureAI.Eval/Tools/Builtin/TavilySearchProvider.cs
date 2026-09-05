using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tools.Builtin;

/// <summary>
/// Port of <c>TavilySearchProvider</c> (<c>tool/_tools/_web_search/_tavily.py</c>): Tavily's Research API
/// (<c>POST https://api.tavily.com/search</c>, bearer <c>TAVILY_API_KEY</c>). <c>include_answer</c> is always
/// forced on; the answer becomes the text and each result a <see cref="UrlCitation"/> with the result content
/// as cited text. A 400 whose <c>detail</c> says the query is too long is reported to the model as a
/// <see cref="ToolError"/>.
/// </summary>
public sealed class TavilySearchProvider : BaseHttpProvider
{
    /// <summary>Environment variable holding the Tavily API key.</summary>
    public const string EnvKey = "TAVILY_API_KEY";

    /// <summary>The Tavily endpoint the query is POSTed to.</summary>
    public const string Endpoint = "https://api.tavily.com/search";

    private const string QueryTooLong = "query is too long";

    /// <summary>Port of <c>TavilyOptions</c>: the option fields and their types (<c>max_connections</c> is an Inspect option, not sent to Tavily).</summary>
    public static readonly IReadOnlyList<string> OptionNames =
    [
        "topic", "search_depth", "chunks_per_source", "max_results", "time_range", "days", "include_answer",
        "include_raw_content", "include_images", "include_image_descriptions", "include_domains", "exclude_domains", "max_connections",
    ];

    private static readonly IReadOnlyList<SearchOptionField> OptionFields =
    [
        new("topic", SearchOptionKind.StringLiteral, ["general", "news"]),
        new("search_depth", SearchOptionKind.StringLiteral, ["basic", "advanced"]),
        new("chunks_per_source", SearchOptionKind.IntLiteral, IntLiterals: [1, 2, 3]),
        new("max_results", SearchOptionKind.Int),
        new("time_range", SearchOptionKind.StringLiteral, ["day", "week", "month", "year", "d", "w", "m", "y"]),
        new("days", SearchOptionKind.Int),
        new("include_answer", SearchOptionKind.BoolOrStringLiteral, ["basic", "advanced"]),
        new("include_raw_content", SearchOptionKind.Bool),
        new("include_images", SearchOptionKind.Bool),
        new("include_image_descriptions", SearchOptionKind.Bool),
        new("include_domains", SearchOptionKind.StringList),
        new("exclude_domains", SearchOptionKind.StringList),
        new("max_connections", SearchOptionKind.Int),
    ];

    /// <summary>Creates the provider from validated options (see <see cref="ValidateOptions"/>); the API key is read from <see cref="EnvKey"/> now.</summary>
    public TavilySearchProvider(JsonObject? options = null, HttpMessageHandler? handler = null)
        : base(EnvKey, Endpoint, "Tavily", "tavily_web_search", options, handler)
    {
    }

    /// <summary>Port of <c>TavilyOptions.model_validate(...).model_dump(exclude_none=True)</c>.</summary>
    public static JsonObject ValidateOptions(JsonObject options) => SearchOptionsValidator.Validate("TavilyOptions", options, OptionFields);

    /// <summary>Port of <c>tavily_search_provider</c>: validates the options (an empty object counts as none) and wraps the provider's search as a <see cref="SearchProvider"/>.</summary>
    public static SearchProvider Create(JsonObject? inOptions = null, HttpMessageHandler? handler = null)
    {
        var options = inOptions is { Count: > 0 } ? ValidateOptions(inOptions) : null;
        var provider = new TavilySearchProvider(options, handler);
        return async (query, cancellationToken) =>
            await provider.SearchAsync(query, cancellationToken).ConfigureAwait(false) is { } text ? ToolResult.FromContents([text]) : null;
    }

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> PrepareHeaders(string apiKey) => new Dictionary<string, string>
    {
        ["Authorization"] = $"Bearer {apiKey}",
    };

    /// <inheritdoc />
    public override JsonObject SetDefaultOptions(JsonObject options)
    {
        var newOptions = options.DeepClone().AsObject();
        newOptions["include_answer"] = true;
        return newOptions;
    }

    /// <summary>Executes a search, converting query-too-long 400s to <see cref="ToolError"/>.</summary>
    public override async Task<ContentText?> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpStatusException ex) when (ex.Status == 400 && IsQueryTooLongError(ex) is { } detail)
        {
            throw new ToolError(detail);
        }
    }

    /// <summary>Port of <c>_is_query_too_long_error</c>: the error message when the body's <c>detail</c> (a string, or an object with <c>error</c>) says the query is too long.</summary>
    public static string? IsQueryTooLongError(HttpStatusException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        try
        {
            if (JsonNode.Parse(ex.Body) is not JsonObject body)
            {
                return null;
            }

            var message = body["detail"] switch
            {
                JsonObject detail => detail["error"] is JsonValue error && error.GetValueKind() == JsonValueKind.String ? error.GetValue<string>() : "",
                JsonValue text when text.GetValueKind() == JsonValueKind.String => text.GetValue<string>(),
                null => "",
                _ => null,
            };
            return message is not null && message.Contains(QueryTooLong, StringComparison.OrdinalIgnoreCase) ? message : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public override ContentText? ParseResponse(JsonObject responseData)
    {
        ArgumentNullException.ThrowIfNull(responseData);
        var response = ResponseModel.Parse(responseData);
        if (response.Results.Count == 0 && string.IsNullOrEmpty(response.Answer))
        {
            return null;
        }

        return new ContentText(string.IsNullOrEmpty(response.Answer) ? "No answer found." : response.Answer)
        {
            Citations = response.Results.Select(r => (Citation)new UrlCitation(r.Url) { CitedText = r.Content, Title = r.Title }).ToList(),
        };
    }

    /// <summary>Port of <c>TavilySearchResult</c>.</summary>
    public sealed record SearchResult(string Title, string Url, string Content, double Score);

    /// <summary>Port of <c>TavilySearchResponse</c> validation: query, answer (nullable), images, results and response_time are required.</summary>
    private sealed record ResponseModel(string Query, string? Answer, IReadOnlyList<SearchResult> Results)
    {
        public static ResponseModel Parse(JsonObject data)
        {
            var query = ResponseFields.RequiredString(data, "query", "TavilySearchResponse");
            var answer = ResponseFields.OptionalString(data, "answer", "TavilySearchResponse");
            ResponseFields.RequiredArray(data, "images", "TavilySearchResponse");
            ResponseFields.RequiredNumber(data, "response_time", "TavilySearchResponse");
            var results = ResponseFields.RequiredArray(data, "results", "TavilySearchResponse")
                .Select(item => item as JsonObject ?? throw ResponseFields.Invalid("TavilySearchResponse", "results", "each result must be an object"))
                .Select(item => new SearchResult(
                    ResponseFields.RequiredString(item, "title", "TavilySearchResult"),
                    ResponseFields.RequiredString(item, "url", "TavilySearchResult"),
                    ResponseFields.RequiredString(item, "content", "TavilySearchResult"),
                    ResponseFields.RequiredNumber(item, "score", "TavilySearchResult")))
                .ToList();
            return new ResponseModel(query, answer, results);
        }
    }
}

/// <summary>Field readers for the provider response models; a missing or mistyped field is an <see cref="InvalidDataException"/> (pydantic's <c>ValidationError</c>).</summary>
internal static class ResponseFields
{
    public static InvalidDataException Invalid(string model, string field, string problem) =>
        new($"{model}: {field}: {problem}");

    public static string RequiredString(JsonObject data, string name, string model) =>
        data[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : throw Invalid(model, name, data.ContainsKey(name) && data[name] is not null ? "Input should be a valid string" : "Field required");

    public static string? OptionalString(JsonObject data, string name, string model)
    {
        if (!data.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        return node is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : throw Invalid(model, name, "Input should be a valid string");
    }

    public static double RequiredNumber(JsonObject data, string name, string model) =>
        data[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number
            ? value.GetValue<double>()
            : throw Invalid(model, name, data.ContainsKey(name) && data[name] is not null ? "Input should be a valid number" : "Field required");

    public static JsonArray RequiredArray(JsonObject data, string name, string model) =>
        data[name] as JsonArray ?? throw Invalid(model, name, data.ContainsKey(name) && data[name] is not null ? "Input should be a valid list" : "Field required");

    public static JsonArray? OptionalArray(JsonObject data, string name, string model)
    {
        if (!data.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        return node as JsonArray ?? throw Invalid(model, name, "Input should be a valid list");
    }
}

using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tools.Builtin;

/// <summary>
/// Port of <c>ExaSearchProvider</c> (<c>tool/_tools/_web_search/_exa.py</c>): Exa's Answer API
/// (<c>POST https://api.exa.ai/answer</c>, <c>x-api-key: EXA_API_KEY</c>). Citation text is requested
/// (<c>text: true</c>) unless the caller said otherwise; the answer becomes the text and each citation a
/// <see cref="UrlCitation"/>.
/// </summary>
public sealed class ExaSearchProvider : BaseHttpProvider
{
    /// <summary>Environment variable holding the Exa API key.</summary>
    public const string EnvKey = "EXA_API_KEY";

    /// <summary>The Exa endpoint the query is POSTed to.</summary>
    public const string Endpoint = "https://api.exa.ai/answer";

    /// <summary>Port of <c>ExaOptions</c>: <c>text</c>, <c>model</c> ("exa" or "exa-pro") and the Inspect-only <c>max_connections</c>.</summary>
    public static readonly IReadOnlyList<string> OptionNames = ["text", "model", "max_connections"];

    private static readonly IReadOnlyList<SearchOptionField> OptionFields =
    [
        new("text", SearchOptionKind.Bool),
        new("model", SearchOptionKind.StringLiteral, ["exa", "exa-pro"]),
        new("max_connections", SearchOptionKind.Int),
    ];

    /// <summary>Creates the provider from validated options (see <see cref="ValidateOptions"/>); the API key is read from <see cref="EnvKey"/> now.</summary>
    public ExaSearchProvider(JsonObject? options = null, HttpMessageHandler? handler = null)
        : base(EnvKey, Endpoint, "Exa", "exa_web_search", options, handler)
    {
    }

    /// <summary>Port of <c>ExaOptions.model_validate(...).model_dump(exclude_none=True)</c>.</summary>
    public static JsonObject ValidateOptions(JsonObject options) => SearchOptionsValidator.Validate("ExaOptions", options, OptionFields);

    /// <summary>Port of <c>exa_search_provider</c>: validates the options (an empty object counts as none) and wraps the provider's search as a <see cref="SearchProvider"/>.</summary>
    public static SearchProvider Create(JsonObject? inOptions = null, HttpMessageHandler? handler = null)
    {
        var options = inOptions is { Count: > 0 } ? ValidateOptions(inOptions) : null;
        var provider = new ExaSearchProvider(options, handler);
        return async (query, cancellationToken) =>
            await provider.SearchAsync(query, cancellationToken).ConfigureAwait(false) is { } text ? ToolResult.FromContents([text]) : null;
    }

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> PrepareHeaders(string apiKey) => new Dictionary<string, string>
    {
        ["x-api-key"] = apiKey,
        ["Content-Type"] = "application/json",
    };

    /// <summary>Requests citation text unless the caller said otherwise (Exa omits it by default, leaving citations without cited text).</summary>
    public override JsonObject SetDefaultOptions(JsonObject options)
    {
        var newOptions = options.DeepClone().AsObject();
        if (!newOptions.ContainsKey("text"))
        {
            newOptions["text"] = true;
        }

        return newOptions;
    }

    /// <inheritdoc />
    public override ContentText? ParseResponse(JsonObject responseData)
    {
        ArgumentNullException.ThrowIfNull(responseData);
        var answer = ResponseFields.RequiredString(responseData, "answer", "ExaSearchResponse");
        var citations = (ResponseFields.OptionalArray(responseData, "citations", "ExaSearchResponse") ?? [])
            .Select(item => item as JsonObject ?? throw ResponseFields.Invalid("ExaSearchResponse", "citations", "each citation must be an object"))
            .Select(item => new ExaCitation(
                ResponseFields.RequiredString(item, "url", "ExaCitation"),
                ResponseFields.RequiredString(item, "title", "ExaCitation"),
                ResponseFields.OptionalString(item, "id", "ExaCitation"),
                ResponseFields.OptionalString(item, "author", "ExaCitation"),
                ResponseFields.OptionalString(item, "publishedDate", "ExaCitation"),
                ResponseFields.OptionalString(item, "text", "ExaCitation")))
            .ToList();

        if (answer.Length == 0 && citations.Count == 0)
        {
            return null;
        }

        return new ContentText(answer)
        {
            Citations = citations.Select(c => (Citation)new UrlCitation(c.Url) { CitedText = c.Text, Title = c.Title }).ToList(),
        };
    }

    /// <summary>Port of <c>ExaCitation</c>: only <c>url</c> and <c>title</c> are guaranteed; <c>text</c> is present only when requested.</summary>
    public sealed record ExaCitation(string Url, string Title, string? Id = null, string? Author = null, string? PublishedDate = null, string? Text = null);
}

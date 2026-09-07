using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tools.Builtin;

public static partial class BuiltinTools
{
    /// <summary>The <c>web_search</c> tool description.</summary>
    public const string WebSearchDescription = "Use the web_search tool to perform keyword searches of the web.";

    /// <summary>The answer when the provider found nothing relevant.</summary>
    public const string WebSearchNoResults = "I couldn't find any relevant information on the web.";

    /// <summary>
    /// Port of <c>web_search(providers)</c> (<c>tool/_tools/_web_search/_web_search.py</c>). Providers are
    /// internal ("openai", "anthropic", "gemini", "grok", "mistral", "perplexity": the model's own search, on by
    /// default when no external provider is given) or external ("tavily", "exa", "google": separate services
    /// with API keys in <c>TAVILY_API_KEY</c> / <c>EXA_API_KEY</c>). The provider configuration is carried in the
    /// tool's <c>options</c> under the provider names plus the <c>__internal_tool_type__</c> marker, exactly as
    /// Python does. The Anthropic route on Foundry passes the "anthropic" provider through as Claude's own web
    /// search (see <c>AnthropicWebSearch</c>); the other Azure model providers do not execute internal providers
    /// server-side, so a configuration without tavily, exa or an explicit "perplexity" (the .NET HTTP fallback,
    /// <see cref="PerplexitySearchProvider"/>) fails on first use with <see cref="InvalidOperationException"/>.
    /// </summary>
    /// <param name="providers">
    /// Provider names, <see cref="WebSearchProviders"/> dictionaries, or a mix: <c>WebSearch()</c>,
    /// <c>WebSearch("tavily")</c>, <c>WebSearch("openai", "tavily")</c>,
    /// <c>WebSearch(new WebSearchProviders { ["tavily"] = new JsonObject { ["max_results"] = 5 } })</c>.
    /// </param>
    public static ToolDef WebSearch(params WebSearchProviderSpec[] providers) => WebSearch(providers, handler: null);

    /// <summary>
    /// <see cref="WebSearch(WebSearchProviderSpec[])"/> with an <see cref="HttpMessageHandler"/> for the external
    /// provider's HTTP client (tests substitute a fake transport).
    /// </summary>
    public static ToolDef WebSearch(IReadOnlyList<WebSearchProviderSpec> providers, HttpMessageHandler? handler)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var normalized = WebSearchProviderConfig.Normalize(providers);
        var options = new JsonObject { [WebSearchProviderConfig.InternalToolType] = "web_search" };
        foreach (var (name, providerOptions) in normalized)
        {
            options[name] = providerOptions.DeepClone();
        }

        // Created on first use, as in Python, so a tool whose external provider lacks an API key can still be
        // constructed; the failure (PrerequisiteError) surfaces on the first call and every later one.
        var explicitProviders = WebSearchProviderConfig.ExplicitProviders(providers);
        var searchProvider = new Lazy<SearchProvider>(
            () => WebSearchProviderConfig.CreateExternalProvider(normalized, handler, explicitProviders),
            LazyThreadSafetyMode.ExecutionAndPublication);

        var parameters = new ToolParams
        {
            Properties = new Dictionary<string, ToolParam> { ["query"] = ToolParam.Of("string", "Search query.") },
            Required = ["query"],
        };
        return new ToolDef("web_search", WebSearchDescription, parameters, async (arguments, cancellationToken) =>
        {
            ToolInputValidator.Validate(arguments, parameters);
            var query = ToolArguments.String(arguments, "query");
            var result = await searchProvider.Value(query, cancellationToken).ConfigureAwait(false);
            return result ?? WebSearchNoResults;
        })
        { Parallel = true, Options = options };
    }
}

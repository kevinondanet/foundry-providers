using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Tools.Builtin;

/// <summary>
/// Port of the <c>WebSearchProviders</c> dict of <c>tool/_tools/_web_search/_web_search.py</c>: provider name
/// to options. A value is a <see cref="JsonObject"/> of provider-specific options, <c>true</c> or <c>null</c>
/// (use the provider with default options) or <c>false</c> (disable a provider that would otherwise be on by
/// default). Internal providers ("openai", "anthropic", "gemini", "grok", "mistral", "perplexity") use the
/// model's own search capability and are enabled by default when no external provider is configured; external
/// providers ("tavily", "exa", "google") are separate services with their own API keys.
/// </summary>
public sealed class WebSearchProviders : Dictionary<string, object?>
{
    /// <summary>An empty provider map (ordinal keys, insertion ordered as Python dicts are).</summary>
    public WebSearchProviders()
        : base(StringComparer.Ordinal)
    {
    }
}

/// <summary>
/// One entry of the <c>providers</c> argument of <see cref="BuiltinTools.WebSearch(WebSearchProviderSpec[])"/>:
/// either a bare provider name (Python's <c>WebSearchProvider</c> literal) or a <see cref="WebSearchProviders"/>
/// dictionary, mirroring <c>str | WebSearchProviders</c>. Both convert implicitly.
/// </summary>
public sealed class WebSearchProviderSpec
{
    private WebSearchProviderSpec(string? name, WebSearchProviders? providers)
    {
        Name = name;
        Providers = providers;
    }

    /// <summary>The provider name, when the entry is the string form.</summary>
    public string? Name { get; }

    /// <summary>The providers dictionary, when the entry is the dict form.</summary>
    public WebSearchProviders? Providers { get; }

    /// <summary>A bare provider name.</summary>
    public static implicit operator WebSearchProviderSpec(string name) => new(name ?? throw new ArgumentNullException(nameof(name)), null);

    /// <summary>A providers dictionary.</summary>
    public static implicit operator WebSearchProviderSpec(WebSearchProviders providers) => new(null, providers ?? throw new ArgumentNullException(nameof(providers)));

    /// <summary>The name, or the dictionary in Python's <c>{key: value}</c> form.</summary>
    public override string ToString() => Name ?? "{" + string.Join(", ", Providers!.Select(p => $"{p.Key}: {p.Value ?? "None"}")) + "}";
}

/// <summary>
/// Port of the provider-configuration helpers of <c>_web_search.py</c>: <see cref="Normalize"/>
/// (<c>_normalize_config</c>), <see cref="HasExternalProvider"/> (<c>_has_external_provider</c>) and
/// <see cref="CreateExternalProvider"/> (<c>_create_external_provider</c>). The deprecated keyword form of
/// <c>web_search()</c> (<c>provider=</c>, <c>num_results=</c>, ...) is not ported.
/// </summary>
public static class WebSearchProviderConfig
{
    /// <summary>Port of <c>INTERNAL_TOOL_TYPE</c>: the <c>ToolInfo.options</c> key marking a tool a model provider may execute server-side.</summary>
    public const string InternalToolType = "__internal_tool_type__";

    /// <summary>Port of <c>valid_providers</c>.</summary>
    public static readonly IReadOnlyList<string> ValidProviders = ["grok", "gemini", "openai", "anthropic", "mistral", "perplexity", "tavily", "google", "exa"];

    /// <summary>Port of <c>EXTERNAL_PROVIDERS</c>.</summary>
    public static readonly IReadOnlyList<string> ExternalProviders = ["tavily", "google", "exa"];

    /// <summary>The internal providers, in the order <c>_normalize_config</c> enables them by default.</summary>
    public static readonly IReadOnlyList<string> InternalProviders = ["openai", "anthropic", "grok", "gemini", "mistral", "perplexity"];

    /// <summary>
    /// Port of <c>_normalize_config</c>: the provider name to options map the tool carries in its
    /// <c>options</c>. With no external provider every internal provider is enabled with empty options;
    /// otherwise only the listed providers are. A later entry for the same provider replaces the earlier
    /// options (a bare name resets them to empty); <c>false</c> removes the provider. Insertion order is kept.
    /// </summary>
    /// <exception cref="ArgumentException">An unknown provider name, or an option value that is not a <see cref="JsonObject"/>, bool or null.</exception>
    public static IReadOnlyDictionary<string, JsonObject> Normalize(IReadOnlyList<WebSearchProviderSpec> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var normalized = new OrderedDictionary<string, JsonObject>(StringComparer.Ordinal);
        if (!HasExternalProvider(providers))
        {
            foreach (var name in InternalProviders)
            {
                normalized[name] = new JsonObject();
            }
        }

        foreach (var entry in providers)
        {
            if (entry.Name is { } name)
            {
                if (!ValidProviders.Contains(name))
                {
                    throw new ArgumentException($"Invalid provider: '{name}'", nameof(providers));
                }

                normalized[name] = new JsonObject();
                continue;
            }

            foreach (var (key, value) in entry.Providers!)
            {
                if (!ValidProviders.Contains(key))
                {
                    throw new ArgumentException($"Invalid provider: '{key}'", nameof(providers));
                }

                switch (value)
                {
                    case JsonObject options:
                        normalized[key] = options.DeepClone().AsObject();
                        break;
                    case null or true:
                        normalized[key] = new JsonObject();
                        break;
                    case false:
                        normalized.Remove(key);
                        break;
                    default:
                        throw new ArgumentException($"Invalid value for provider '{key}': {value}. Expected a dict, bool, or None", nameof(providers));
                }
            }
        }

        return normalized;
    }

    /// <summary>Port of <c>_has_external_provider</c>: whether any entry enables tavily, google or exa (a <c>false</c> value does not count).</summary>
    public static bool HasExternalProvider(IReadOnlyList<WebSearchProviderSpec> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        foreach (var entry in providers)
        {
            if (entry.Name is { } name)
            {
                if (ExternalProviders.Contains(name))
                {
                    return true;
                }
            }
            else if (entry.Providers!.Any(p => ExternalProviders.Contains(p.Key) && p.Value is not false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Port of <c>_create_external_provider</c>: tavily wins over exa, which wins over google. The google
    /// provider (Programmable Search Engine plus a relevance model over fetched pages) is not ported and is
    /// reported as <see cref="NotSupportedException"/>. A .NET addition: when the caller listed "perplexity"
    /// explicitly (<paramref name="explicitProviders"/>, see <see cref="ExplicitProviders"/>) and no other
    /// external provider is configured, the <see cref="PerplexitySearchProvider"/> HTTP fallback is used. With
    /// nothing else the result is an <see cref="InvalidOperationException"/> ("No valid provider found."), which
    /// is fatal to the sample as in Python because the Azure providers do not execute internal providers
    /// server-side (the Anthropic route is the exception: it passes the "anthropic" provider through as
    /// Claude's own web search, in which case the tool's execute is never called).
    /// </summary>
    public static SearchProvider CreateExternalProvider(
        IReadOnlyDictionary<string, JsonObject> providers,
        HttpMessageHandler? handler = null,
        IReadOnlySet<string>? explicitProviders = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (providers.TryGetValue("tavily", out var tavily))
        {
            return TavilySearchProvider.Create(tavily, handler);
        }

        if (providers.TryGetValue("exa", out var exa))
        {
            return ExaSearchProvider.Create(exa, handler);
        }

        if (providers.ContainsKey("google"))
        {
            throw new NotSupportedException(
                "The 'google' web search provider is not available in this port (it needs an HTML parser and a relevance model); "
                + "configure 'tavily' or 'exa' instead.");
        }

        if (explicitProviders is not null && explicitProviders.Contains("perplexity") && providers.TryGetValue("perplexity", out var perplexity))
        {
            return PerplexitySearchProvider.Create(perplexity, handler);
        }

        throw new InvalidOperationException(
            "No valid provider found. The internal web search providers (openai, anthropic, gemini, grok, mistral, perplexity) "
            + "are not executed by the Azure model providers; configure an external provider such as \"tavily\" or \"exa\" "
            + "(or list \"perplexity\" explicitly to search over Perplexity's API).");
    }

    /// <summary>
    /// The providers the caller named themselves (a bare name, or a dictionary entry whose value is not
    /// <c>false</c>), as opposed to the internal providers <see cref="Normalize"/> enables by default. Python
    /// has no such distinction; it decides which perplexity path to take when the HTTP fallback is considered.
    /// </summary>
    public static IReadOnlySet<string> ExplicitProviders(IReadOnlyList<WebSearchProviderSpec> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var explicitProviders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in providers)
        {
            if (entry.Name is { } name)
            {
                explicitProviders.Add(name);
                continue;
            }

            foreach (var (key, value) in entry.Providers!)
            {
                if (value is false)
                {
                    explicitProviders.Remove(key);
                }
                else
                {
                    explicitProviders.Add(key);
                }
            }
        }

        return explicitProviders;
    }
}

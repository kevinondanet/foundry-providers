using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace InspectAzureAI.Swe.CodexCli;

/// <summary>The Codex <c>--model</c> slug chosen for the real bridged model, and why (port of <c>CodexModelResolution</c>).</summary>
/// <param name="Slug">The slug passed to <c>codex --model</c>.</param>
/// <param name="Reason">The capabilities Codex binds for the slug and the policy branch that chose it, for logging.</param>
public sealed record CodexModelResolution(string Slug, string Reason);

/// <summary>
/// Port of inspect_swe <c>_codex_cli/model_catalog.py</c>: maps the real model served through the bridge onto a
/// Codex <c>--model</c> slug. Codex picks its system prompt and tool set from a catalog keyed by that slug
/// (longest-prefix match; an unknown slug gets a generic prompt without <c>apply_patch</c>), so the slug is chosen
/// to keep Codex's tooling aligned with what the served model supports. Pure: the catalog is fetched separately by
/// <see cref="CodexCliBinary.CatalogAsync"/>, and deciding whether a served model counts as OpenAI (and "latest")
/// is the caller's job.
/// </summary>
public static partial class CodexCliModelCatalog
{
    /// <summary>
    /// The slug that forces Codex's generic fallback (no <c>apply_patch</c>, no <c>tool_search</c>) for a model in
    /// the catalog that predates public-API <c>tool_search</c> support. No catalog slug is a prefix of it.
    /// </summary>
    public const string GenericFallbackSlug = "inspect-generic";

    /// <summary>The priority of a catalog entry without one (<c>m.get("priority", 1_000_000)</c>).</summary>
    public const double DefaultPriority = 1_000_000;

    /// <summary>
    /// The bundled fallback catalog (Python <c>BUNDLED_CODEX_CATALOG</c>, <c>rust-v0.145.0</c>, 8 slugs). Each read
    /// returns a fresh copy, so callers may not mutate shared state.
    /// </summary>
    public static JsonObject Bundled => JsonNode.Parse(CodexCliBundledCatalog.Json)!.AsObject();

    /// <summary>
    /// Port of <c>resolve_codex_model_slug</c>, with the policy and <c>reason</c> texts verbatim:
    /// <list type="number">
    /// <item><description>an explicit <paramref name="modelConfig"/> is returned as is;</description></item>
    /// <item><description>a non-OpenAI model keeps its name (Codex's generic prompt);</description></item>
    /// <item><description>a name matching a catalog entry keeps its name, unless the entry binds
    /// <c>tool_search</c> and the model supports neither it (gpt-5.4+) nor is a latest/codename model, which gives
    /// <see cref="GenericFallbackSlug"/>;</description></item>
    /// <item><description>with no usable catalog the name is kept;</description></item>
    /// <item><description>a model absent from the catalog aliases to <see cref="LatestSlug"/> when it supports
    /// <c>tool_search</c> or <paramref name="isLatest"/> is set, and otherwise keeps its name.</description></item>
    /// </list>
    /// </summary>
    public static CodexModelResolution ResolveSlug(string modelName, bool openAi, JsonObject? catalog, string? modelConfig, bool isLatest)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        if (modelConfig is not null)
        {
            return new CodexModelResolution(modelConfig, $"explicit model_config override '{modelConfig}'");
        }

        if (!openAi)
        {
            return new CodexModelResolution(modelName, $"non-openai model '{modelName}' → Codex generic prompt (no apply_patch)");
        }

        var models = CatalogModels(catalog);
        var toolSearchOk = SupportsToolSearch(modelName) || isLatest;
        var matched = MatchedEntry(modelName, models);
        if (matched is not null)
        {
            if (Truthy(matched["supports_search_tool"]) && !toolSearchOk)
            {
                return new CodexModelResolution(
                    GenericFallbackSlug,
                    $"openai '{modelName}' is in the Codex catalog but predates tool_search support (gpt-5.4+) → forcing generic prompt via '{GenericFallbackSlug}' (no apply_patch, no tool_search) to avoid a tool_search 400");
            }

            return new CodexModelResolution(modelName, $"openai '{modelName}' matches Codex catalog → native prompt/tools ({SlugCapabilities(modelName, models)})");
        }

        var latest = LatestSlug(catalog);
        if (latest is null)
        {
            return new CodexModelResolution(modelName, $"Codex catalog unavailable → deferring '{modelName}' to Codex's bundled catalog");
        }

        if (toolSearchOk)
        {
            var why = SupportsToolSearch(modelName) ? "supports tool_search (gpt-5.4+)" : "is a latest/codename model (likely pre-deployment)";
            return new CodexModelResolution(latest, $"openai '{modelName}' absent from catalog but {why} → aliased to latest '{latest}' ({SlugCapabilities(latest, models)})");
        }

        return new CodexModelResolution(modelName, $"openai '{modelName}' predates tool_search support (gpt-5.4+) → Codex generic prompt (no apply_patch, no tool_search)");
    }

    /// <summary>
    /// Port of <c>latest_openai_slug</c>: the entry with the lowest <c>priority</c> (default
    /// <see cref="DefaultPriority"/>), excluding <c>-mini</c> slugs unless only minis exist; null without usable
    /// entries. Ties keep catalog order.
    /// </summary>
    public static string? LatestSlug(JsonObject? catalog)
    {
        var models = CatalogModels(catalog);
        if (models.Count == 0)
        {
            return null;
        }

        var nonMini = models.Where(model => !Slug(model).EndsWith("-mini", StringComparison.Ordinal)).ToList();
        var preferred = nonMini.Count > 0 ? nonMini : models;
        return Slug(preferred.OrderBy(Priority).First());
    }

    /// <summary>
    /// Port of <c>_supports_tool_search</c>: whether the name starts with <c>gpt-&lt;major&gt;[.&lt;minor&gt;]</c> at or
    /// above 5.4 (the public Responses API boundary for <c>tool_search</c>); o-series and other names are not.
    /// </summary>
    public static bool SupportsToolSearch(string modelName)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        var match = GptVersionRegex().Match(modelName);
        if (!match.Success)
        {
            return false;
        }

        var major = VersionPart(match.Groups[1].Value);
        var minor = match.Groups[2].Success ? VersionPart(match.Groups[2].Value) : 0;
        return major > 5 || (major == 5 && minor >= 4);
    }

    /// <summary>Port of <c>_catalog_models</c>: the object entries of <c>models</c> that carry a string <c>slug</c>.</summary>
    private static List<JsonObject> CatalogModels(JsonObject? catalog)
    {
        if (catalog is null || catalog["models"] is not JsonArray models)
        {
            return [];
        }

        return models.OfType<JsonObject>().Where(model => StringOf(model["slug"]) is not null).ToList();
    }

    /// <summary>Port of <c>_matched_entry</c>: the longest catalog slug that prefixes the name (the first such on ties).</summary>
    private static JsonObject? MatchedEntry(string modelName, List<JsonObject> models)
    {
        JsonObject? best = null;
        var bestLength = -1;
        foreach (var model in models)
        {
            var slug = Slug(model);
            if (modelName.StartsWith(slug, StringComparison.Ordinal) && slug.Length > bestLength)
            {
                best = model;
                bestLength = slug.Length;
            }
        }

        return best;
    }

    /// <summary>Port of <c>_slug_capabilities</c>.</summary>
    private static string SlugCapabilities(string slug, List<JsonObject> models)
    {
        var entry = MatchedEntry(slug, models);
        if (entry is null)
        {
            return "generic fallback (no apply_patch, no tool_search)";
        }

        var applyPatchNode = entry["apply_patch_tool_type"];
        var applyPatch = Truthy(applyPatchNode) ? StringOf(applyPatchNode) ?? applyPatchNode!.ToJsonString() : "none";
        var toolSearch = Truthy(entry["supports_search_tool"]) ? "yes" : "no";
        return $"apply_patch={applyPatch}, tool_search={toolSearch}";
    }

    private static string Slug(JsonObject model) => StringOf(model["slug"])!;

    private static double Priority(JsonObject model) =>
        model["priority"] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<double>(out var priority)
            ? priority
            : DefaultPriority;

    private static long VersionPart(string digits) =>
        long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : long.MaxValue;

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>Python truthiness of a JSON value.</summary>
    private static bool Truthy(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Count > 0,
        JsonArray array => array.Count > 0,
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            JsonValueKind.Number => value.TryGetValue<double>(out var number) && number != 0,
            _ => false,
        },
        _ => false,
    };

    [GeneratedRegex(@"^gpt-([0-9]+)(?:\.([0-9]+))?")]
    private static partial Regex GptVersionRegex();
}

using System.Text.RegularExpressions;

namespace InspectAzureAI.Eval.Model.Cost;

/// <summary>
/// Port of <c>model/_model_info.py</c>: model metadata lookup from the embedded database (exact, case-insensitive
/// and fuzzy matching over Inspect <c>provider/model</c> strings) plus the custom registry of
/// <c>set_model_info</c> / <c>set_model_cost</c>. Two port-only additions: a bare name (a Foundry deployment name,
/// which is what <see cref="Model.Name"/> reports) and an <c>azureai/</c> prefixed name first go through
/// <see cref="FoundryModelOverlay"/>, and the cost override file named by
/// <see cref="ModelCostConfig.EnvironmentVariable"/> is applied on first use. Python's fallback of instantiating
/// the provider to canonicalise a name is not ported (this solution has no provider registry). All state is
/// process-wide and guarded by one lock because samples run concurrently.
/// </summary>
public static class ModelInfoLookup
{
    /// <summary>Port of <c>SERVICE_PREFIXES</c>: routing prefixes stripped from the middle of three-part names.</summary>
    public static readonly IReadOnlySet<string> ServicePrefixes = new HashSet<string>(StringComparer.Ordinal) { "azure", "bedrock", "vertex" };

    /// <summary>Port of <c>HOSTING_PROVIDERS</c>: providers serving several organizations, whose org is detected from the model name.</summary>
    public static readonly IReadOnlySet<string> HostingProviders = new HashSet<string>(StringComparer.Ordinal) { "azureai", "bedrock", "vertex" };

    /// <summary>Port of <c>PROVIDER_SCOPED_ORGS</c>: organizations that name a hosting deployment and are never fuzzy candidates.</summary>
    public static readonly IReadOnlySet<string> ProviderScopedOrgs = new HashSet<string>(StringComparer.Ordinal) { "fireworks" };

    private const string AzureAIPrefix = "azureai/";

    private static readonly object Sync = new();

    private static readonly Dictionary<string, ModelInfo> CustomModels = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, ModelInfo?> ResultCache = new(StringComparer.Ordinal);

    private static IReadOnlyList<KeyValuePair<string, ModelInfo>>? _database;

    private static Dictionary<string, ModelInfo>? _databaseByKey;

    private static Dictionary<string, string>? _lookupIndex;

    private static bool _overridesApplied;

    private static bool _applyingOverrides;

    /// <summary>The embedded database keyed by <c>organization/model</c> (aliases and versions included).</summary>
    internal static IReadOnlyDictionary<string, ModelInfo> Database
    {
        get
        {
            lock (Sync)
            {
                EnsureDatabase();
                return _databaseByKey!;
            }
        }
    }

    /// <summary>
    /// Port of <c>get_model_info</c> (without the provider-instantiation fallback): the model's metadata, or null
    /// when it is not in the database or the custom registry. Results, including misses, are memoised.
    /// </summary>
    public static ModelInfo? GetModelInfo(string model)
    {
        ArgumentNullException.ThrowIfNull(model);
        lock (Sync)
        {
            EnsureDatabase();
            EnsureOverrides();
            if (ResultCache.TryGetValue(model, out var cached))
            {
                return cached;
            }

            var result = Resolve(model);
            ResultCache[model] = result;
            return result;
        }
    }

    /// <summary>The metadata of a wrapped model, looked up by its name (<see cref="Model.Name"/>).</summary>
    public static ModelInfo? GetModelInfo(Model model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return GetModelInfo(model.Name);
    }

    /// <summary>Port of <c>set_model_info</c>: registers metadata for a model not in the database (or overrides a built-in one).</summary>
    public static void SetModelInfo(string model, ModelInfo info)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(info);
        lock (Sync)
        {
            CustomModels[model] = info;
            ResultCache.Clear();
        }
    }

    /// <summary>
    /// Port of <c>set_model_cost</c>: sets the cost of a model already in the database or the custom registry;
    /// throws <see cref="ArgumentException"/> when the model is not found.
    /// </summary>
    public static void SetModelCost(string model, ModelCost cost)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(cost);
        lock (Sync)
        {
            var info = GetModelInfo(model) ?? throw new ArgumentException($"Model '{model}' not found.", nameof(model));
            CustomModels[model] = info with { Cost = cost };
            ResultCache.Clear();
        }
    }

    /// <summary>
    /// Port of <c>clear_model_info_cache</c>: drops the loaded database, the custom registry and the memoised
    /// results; the next lookup reloads the resources and re-applies the cost override file.
    /// </summary>
    public static void ClearModelInfoCache()
    {
        lock (Sync)
        {
            _database = null;
            _databaseByKey = null;
            _lookupIndex = null;
            _overridesApplied = false;
            CustomModels.Clear();
            ResultCache.Clear();
        }
    }

    /// <summary>The custom registry entry for <paramref name="model"/>, if any (port of <c>_get_custom_model_info</c>).</summary>
    internal static ModelInfo? GetCustomModelInfo(string model)
    {
        lock (Sync)
        {
            return CustomModels.GetValueOrDefault(model);
        }
    }

    private static void EnsureDatabase()
    {
        if (_database is not null)
        {
            return;
        }

        _database = ModelData.ReadModelInfo();
        _databaseByKey = _database.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        // port of _build_lookup_index: a later entry with the same normalised name wins
        _lookupIndex = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, _) in _database)
        {
            _lookupIndex[NormalizeForLookup(key)] = key;
        }
    }

    private static void EnsureOverrides()
    {
        // applying the file looks models up (re-entrantly, under the same lock); the applied flag is set only
        // after a successful load so a broken file keeps failing loudly rather than silently pricing nothing
        if (_overridesApplied || _applyingOverrides)
        {
            return;
        }

        _applyingOverrides = true;
        try
        {
            ModelCostConfig.ApplyFromEnvironment();
            _overridesApplied = true;
        }
        finally
        {
            _applyingOverrides = false;
        }
    }

    private static ModelInfo? Resolve(string model)
    {
        if (CustomModels.TryGetValue(model, out var custom))
        {
            return custom;
        }

        if (!model.Contains('/'))
        {
            // a Foundry deployment name: an override keyed the Python way (azureai/<name>) applies to it
            if (CustomModels.TryGetValue(AzureAIPrefix + model, out custom))
            {
                return custom;
            }

            return ResolveThroughOverlay(model) ?? LookupInDb(AzureAIPrefix + model);
        }

        if (model.StartsWith(AzureAIPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ResolveThroughOverlay(model[AzureAIPrefix.Length..]) ?? LookupInDb(model);
        }

        return LookupInDb(model);
    }

    private static ModelInfo? ResolveThroughOverlay(string deploymentName) =>
        FoundryModelOverlay.BaseModelKey(deploymentName) is { } candidate ? LookupInDb(candidate) : null;

    /// <summary>Port of <c>_lookup_in_db</c>: exact match, then case-insensitive, then fuzzy.</summary>
    internal static ModelInfo? LookupInDb(string name)
    {
        lock (Sync)
        {
            EnsureDatabase();
            if (_databaseByKey!.TryGetValue(name, out var exact))
            {
                return exact;
            }

            if (_lookupIndex!.TryGetValue(NormalizeForLookup(name), out var key))
            {
                return _databaseByKey[key];
            }

            return FuzzyMatch(name, _database!);
        }
    }

    /// <summary>Port of <c>_normalize_for_lookup</c>: lower-case with underscores as hyphens.</summary>
    internal static string NormalizeForLookup(string name) => name.ToLowerInvariant().Replace('_', '-');

    /// <summary>
    /// Port of <c>_detect_org_from_model_name</c>: the organization implied by a model name pattern
    /// (gpt-/o1/o3/o4 → openai, claude → anthropic, mistral/mixtral → mistral, gemini → google, kimi → moonshotai,
    /// deepseek → deepseek), or null.
    /// </summary>
    internal static string? DetectOrgFromModelName(string modelName)
    {
        var name = modelName.ToLowerInvariant();
        if (name.StartsWith("gpt-", StringComparison.Ordinal) || name.StartsWith("o1", StringComparison.Ordinal)
            || name.StartsWith("o3", StringComparison.Ordinal) || name.StartsWith("o4", StringComparison.Ordinal))
        {
            return "openai";
        }

        if (name.StartsWith("claude", StringComparison.Ordinal))
        {
            return "anthropic";
        }

        if (name.StartsWith("mistral", StringComparison.Ordinal) || name.StartsWith("mixtral", StringComparison.Ordinal))
        {
            return "mistral";
        }

        if (name.StartsWith("gemini", StringComparison.Ordinal))
        {
            return "google";
        }

        if (name.StartsWith("kimi", StringComparison.Ordinal))
        {
            return "moonshotai";
        }

        if (name.StartsWith("deepseek", StringComparison.Ordinal))
        {
            return "deepseek";
        }

        return null;
    }

    /// <summary>
    /// Port of <c>_extract_model_name</c>: <c>org/model</c> for two-part names, <c>org/model</c> for
    /// <c>provider/org/model</c>, the service dropped from <c>provider/service/model</c> when the service is one of
    /// <see cref="ServicePrefixes"/>, and the organization detected from the model name for <see cref="HostingProviders"/>.
    /// </summary>
    internal static string ExtractModelName(string name)
    {
        var parts = name.Split('/');
        if (parts.Length >= 3 && ServicePrefixes.Contains(parts[^2].ToLowerInvariant()))
        {
            return $"{parts[^3]}/{parts[^1]}";
        }

        if (parts.Length >= 2)
        {
            var provider = parts[^2].ToLowerInvariant();
            if (HostingProviders.Contains(provider) && DetectOrgFromModelName(parts[^1]) is { } detected)
            {
                return $"{detected}/{parts[^1]}";
            }

            return $"{parts[^2]}/{parts[^1]}";
        }

        return parts.Length > 0 ? parts[^1] : name;
    }

    /// <summary>Port of <c>_normalize_for_fuzzy</c>: case and underscore normalisation plus stripped <c>-vN</c> / <c>:N</c> suffixes.</summary>
    internal static string NormalizeForFuzzy(string name)
    {
        name = name.ToLowerInvariant().Replace('_', '-');
        name = VersionSuffix.Replace(name, "");
        name = ColonSuffix.Replace(name, "");
        return name;
    }

    private static readonly Regex VersionSuffix = new(@"-v\d+$", RegexOptions.CultureInvariant);

    private static readonly Regex ColonSuffix = new(@":\d+$", RegexOptions.CultureInvariant);

    /// <summary>Port of <c>_compute_match_score</c>: 100 for an exact match, 50-99 for a substring match by overlap ratio, else 0.</summary>
    internal static int ComputeMatchScore(string query, string target)
    {
        if (query == target)
        {
            return 100;
        }

        if (target.Contains(query, StringComparison.Ordinal) || query.Contains(target, StringComparison.Ordinal))
        {
            var overlap = target.Contains(query, StringComparison.Ordinal) ? query.Length : target.Length;
            var maxLen = Math.Max(query.Length, target.Length);
            return 50 + (int)(50.0 * overlap / maxLen);
        }

        return 0;
    }

    /// <summary>Port of <c>_fuzzy_match</c>: the best-scoring entry (first wins ties) when it scores at least 60.</summary>
    private static ModelInfo? FuzzyMatch(string name, IReadOnlyList<KeyValuePair<string, ModelInfo>> database)
    {
        var normalizedQuery = NormalizeForFuzzy(ExtractModelName(name));
        (int Score, ModelInfo Info)? best = null;
        foreach (var (key, info) in database)
        {
            var slash = key.IndexOf('/', StringComparison.Ordinal);
            var organization = (slash < 0 ? key : key[..slash]).ToLowerInvariant();
            if (ProviderScopedOrgs.Contains(organization))
            {
                continue;
            }

            var score = ComputeMatchScore(normalizedQuery, NormalizeForFuzzy(ExtractModelName(key)));
            if (score > 0 && (best is null || score > best.Value.Score))
            {
                best = (score, info);
            }
        }

        return best is { Score: >= 60 } match ? match.Info : null;
    }
}

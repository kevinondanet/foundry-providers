using System.Text.Json;

namespace InspectAzureAI.Eval.Model.Cost;

/// <summary>
/// Port of the <c>--model-cost-config</c> / <c>model_cost_config</c> option of <c>_eval/eval.py</c>: a file of
/// model prices applied to the registry. The file is JSON (Python also accepts YAML; no YAML parser here) shaped
/// like Python's, a map of model name to <c>{"input", "output", "input_cache_write", "input_cache_read"}</c> in
/// dollars per million tokens, all four required. Names may be Inspect strings (<c>openai/gpt-4o</c>) or bare
/// Foundry deployment names (<c>gpt-5.4-mini</c>). Unlike <see cref="ModelInfoLookup.SetModelCost"/>, an entry
/// for a model the database does not know registers cost-only metadata instead of failing, because Foundry
/// deployment names are user-chosen; an entry for a known model keeps its metadata and sets the cost, so the
/// file always wins over the embedded data. The file named by <see cref="EnvironmentVariable"/> is applied on
/// the first lookup.
/// </summary>
public static class ModelCostConfig
{
    /// <summary>Environment variable naming the cost override file.</summary>
    public const string EnvironmentVariable = "INSPECT_AZUREAI_MODEL_COST_CONFIG";

    /// <summary>Applies the file named by <see cref="EnvironmentVariable"/>, if set; a missing or invalid file throws.</summary>
    public static void ApplyFromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{EnvironmentVariable} names a model cost config file that does not exist: {path}", path);
        }

        Apply(path);
    }

    /// <summary>Reads and applies a cost config file; throws <see cref="FileNotFoundException"/> or <see cref="InvalidDataException"/>.</summary>
    public static void Apply(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Model cost config file not found: {path}", path);
        }

        Apply(Parse(File.ReadAllText(path), path));
    }

    /// <summary>Port of the <c>dict[str, ModelCost]</c> form of <c>model_cost_config</c>: applies the given prices.</summary>
    public static void Apply(IReadOnlyDictionary<string, ModelCost> costs)
    {
        ArgumentNullException.ThrowIfNull(costs);
        foreach (var (model, cost) in costs)
        {
            var existing = ModelInfoLookup.GetModelInfo(model) ?? new ModelInfo();
            ModelInfoLookup.SetModelInfo(model, existing with { Cost = cost });
        }
    }

    /// <summary>Parses the JSON text of a cost config file (<paramref name="source"/> names it in errors).</summary>
    public static IReadOnlyDictionary<string, ModelCost> Parse(string json, string source = "model cost config")
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{source}: invalid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"{source}: expected an object mapping model names to prices.");
            }

            var costs = new Dictionary<string, ModelCost>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException($"{source}: model '{entry.Name}' must map to an object of prices.");
                }

                costs[entry.Name] = new ModelCost(
                    Price(entry.Value, "input", entry.Name, source),
                    Price(entry.Value, "output", entry.Name, source),
                    Price(entry.Value, "input_cache_write", entry.Name, source),
                    Price(entry.Value, "input_cache_read", entry.Name, source));
            }

            return costs;
        }
    }

    private static double Price(JsonElement prices, string field, string model, string source)
    {
        if (!prices.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            throw new InvalidDataException($"{source}: model '{model}' is missing the required price '{field}' (dollars per million tokens; set unused fields to 0).");
        }

        if (value.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidDataException($"{source}: model '{model}' price '{field}' must be a number.");
        }

        return value.GetDouble();
    }
}

using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace InspectAzureAI.Eval.Model.Cost;

/// <summary>
/// Port of <c>model/_model_data/model_data.py</c> <c>read_model_info</c>: builds the model metadata database
/// from the JSON resources embedded under <c>Model/Cost/ModelData</c>. The resources are the Python YAML files
/// converted one-to-one by <c>scripts/convert-model-data.py</c>; <c>manifest.json</c> pins the file order (Python
/// walks the YAML files in filesystem order and later entries win in the case-insensitive lookup index).
/// </summary>
internal static class ModelData
{
    /// <summary>Logical resource name prefix (set in the csproj <c>EmbeddedResource</c> item).</summary>
    public const string ResourcePrefix = "InspectAzureAI.Eval.ModelData.";

    private sealed record Definition(
        string? DisplayName,
        DateOnly? ReleaseDate,
        DateOnly? KnowledgeCutoffDate,
        int? ContextLength,
        int? OutputTokens,
        int? InputTokens,
        bool? Reasoning,
        string? ReasoningEffortDefault,
        string? Family,
        string? Snapshot,
        IReadOnlyList<string> Aliases,
        IReadOnlyList<KeyValuePair<string, Definition>> Versions);

    /// <summary>Port of <c>model_key</c>: the database key of a model, <c>organization/model</c>.</summary>
    public static string ModelKey(string organization, string model) => $"{organization}/{model}";

    /// <summary>
    /// The database in Python dict order: entries are keyed like <see cref="ModelKey"/>, a repeated key keeps its
    /// first position and takes the last value (the <c>versions</c> and <c>aliases</c> of every model are entries too).
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, ModelInfo>> ReadModelInfo()
    {
        var assembly = typeof(ModelData).Assembly;
        var entries = new List<KeyValuePair<string, ModelInfo>>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);

        void Add(string key, ModelInfo info)
        {
            if (positions.TryGetValue(key, out var index))
            {
                entries[index] = new KeyValuePair<string, ModelInfo>(key, info);
            }
            else
            {
                positions[key] = entries.Count;
                entries.Add(new KeyValuePair<string, ModelInfo>(key, info));
            }
        }

        foreach (var file in ReadManifest(assembly))
        {
            using var document = JsonDocument.Parse(ReadResource(assembly, file));
            foreach (var organization in document.RootElement.EnumerateObject())
            {
                var organizationName = organization.Value.GetProperty("display_name").GetString()
                    ?? throw new InvalidDataException($"{file}: organization '{organization.Name}' has no display_name.");
                foreach (var model in organization.Value.GetProperty("models").EnumerateObject())
                {
                    var definition = ReadDefinition(model.Value, $"{file}:{organization.Name}/{model.Name}");
                    Add(ModelKey(organization.Name, model.Name), CreateModelInfo(organizationName, definition, null));

                    foreach (var (versionName, version) in definition.Versions)
                    {
                        Add(ModelKey(organization.Name, versionName), CreateModelInfo(organizationName, definition, version));
                    }

                    foreach (var alias in definition.Aliases)
                    {
                        Add(ModelKey(organization.Name, alias), CreateModelInfo(organizationName, definition, null));
                    }

                    foreach (var (_, version) in definition.Versions)
                    {
                        foreach (var alias in version.Aliases)
                        {
                            Add(ModelKey(organization.Name, alias), CreateModelInfo(organizationName, definition, version));
                        }
                    }
                }
            }
        }

        return entries;
    }

    private static IReadOnlyList<string> ReadManifest(Assembly assembly)
    {
        using var manifest = JsonDocument.Parse(ReadResource(assembly, "manifest.json"));
        return manifest.RootElement.GetProperty("files").EnumerateArray()
            .Select(f => f.GetString() ?? throw new InvalidDataException("manifest.json: file names must be strings."))
            .ToArray();
    }

    private static Stream ReadResource(Assembly assembly, string file) =>
        assembly.GetManifestResourceStream(ResourcePrefix + file)
        ?? throw new FileNotFoundException($"Embedded model data resource '{ResourcePrefix + file}' is missing; run scripts/convert-model-data.py and rebuild.");

    /// <summary>
    /// Port of <c>create_model_info</c>: the version's value when it is truthy, else the model's (Python's <c>or</c>,
    /// so <c>False</c>, <c>0</c> and <c>""</c> fall through like <c>None</c>); <c>snapshot</c> never falls through.
    /// </summary>
    private static ModelInfo CreateModelInfo(string organizationName, Definition model, Definition? version)
    {
        var source = version ?? model;
        return new ModelInfo
        {
            Snapshot = source.Snapshot,
            Organization = organizationName,
            Model = Or(source.DisplayName, model.DisplayName),
            KnowledgeCutoffDate = source.KnowledgeCutoffDate ?? model.KnowledgeCutoffDate,
            ReleaseDate = source.ReleaseDate ?? model.ReleaseDate,
            ContextLength = Or(source.ContextLength, model.ContextLength),
            OutputTokens = Or(source.OutputTokens, model.OutputTokens),
            InputTokensOverride = Or(source.InputTokens, model.InputTokens),
            Reasoning = Or(source.Reasoning, model.Reasoning),
            ReasoningEffortDefault = Or(source.ReasoningEffortDefault, model.ReasoningEffortDefault),
            Family = Or(source.Family, model.Family),
        };
    }

    private static string? Or(string? a, string? b) => string.IsNullOrEmpty(a) ? b : a;

    private static int? Or(int? a, int? b) => a is null or 0 ? b : a;

    private static bool? Or(bool? a, bool? b) => a == true ? a : b;

    private static Definition ReadDefinition(JsonElement element, string where)
    {
        var versions = new List<KeyValuePair<string, Definition>>();
        if (element.TryGetProperty("versions", out var versionsElement))
        {
            foreach (var version in versionsElement.EnumerateObject())
            {
                versions.Add(new KeyValuePair<string, Definition>(version.Name, ReadDefinition(version.Value, $"{where}.versions.{version.Name}")));
            }
        }

        var aliases = element.TryGetProperty("aliases", out var aliasesElement)
            ? aliasesElement.EnumerateArray().Select(a => a.GetString() ?? throw new InvalidDataException($"{where}: aliases must be strings.")).ToArray()
            : [];

        return new Definition(
            GetString(element, "display_name", where),
            GetDate(element, "release_date", where),
            GetDate(element, "knowledge_cutoff_date", where),
            GetInt(element, "context_length", where),
            GetInt(element, "output_tokens", where),
            GetInt(element, "input_tokens", where),
            GetBool(element, "reasoning", where),
            GetString(element, "reasoning_effort_default", where),
            GetString(element, "family", where),
            GetString(element, "snapshot", where),
            aliases,
            versions);
    }

    private static string? GetString(JsonElement element, string name, string where) =>
        !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
            ? null
            : value.ValueKind == JsonValueKind.String ? value.GetString() : throw new InvalidDataException($"{where}.{name}: expected a string.");

    private static int? GetInt(JsonElement element, string name, string where) =>
        !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
            ? null
            : value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i) ? i : throw new InvalidDataException($"{where}.{name}: expected an integer.");

    private static bool? GetBool(JsonElement element, string name, string where) =>
        !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
            ? null
            : value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new InvalidDataException($"{where}.{name}: expected a boolean.");

    private static DateOnly? GetDate(JsonElement element, string name, string where)
    {
        var text = GetString(element, name, where);
        if (text is null)
        {
            return null;
        }

        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new InvalidDataException($"{where}.{name}: expected a yyyy-MM-dd date, got '{text}'.");
    }
}

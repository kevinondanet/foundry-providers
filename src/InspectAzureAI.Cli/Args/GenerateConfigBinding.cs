using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Cli.Args;

/// <summary>
/// Builds a <see cref="GenerateConfig"/> from a mapping of Python field names (<c>--generate-config</c> files, the
/// generate-config keys of <c>--model-role</c> mappings): a key that names no field is a <see cref="PrerequisiteError"/>,
/// as pydantic rejects it, and the values are read with the eval log's JSON converters.
/// </summary>
public static class GenerateConfigBinding
{
    private static readonly IReadOnlyDictionary<string, PropertyInfo> Fields = typeof(GenerateConfig)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.GetSetMethod(nonPublic: true) is not null)
        .ToDictionary(FieldName, property => property, StringComparer.Ordinal);

    /// <summary>The Python names of every <see cref="GenerateConfig"/> field.</summary>
    public static IReadOnlyCollection<string> FieldNames => Fields.Keys.ToList();

    /// <summary>Converts <paramref name="values"/> into a config; <paramref name="subject"/> names the source in errors.</summary>
    public static GenerateConfig FromValues(IReadOnlyDictionary<string, object?> values, string subject)
    {
        ArgumentNullException.ThrowIfNull(values);
        var unknown = values.Keys.Where(key => !Fields.ContainsKey(key)).ToList();
        if (unknown.Count > 0)
        {
            throw new PrerequisiteError($"Invalid config for {subject}: unknown field(s) {string.Join(", ", unknown)}.");
        }

        var json = new JsonObject();
        foreach (var (key, value) in values)
        {
            json[key] = CliArgs.ValueToJson(value);
        }

        try
        {
            return json.Deserialize<GenerateConfig>(EvalLogWriter.Options) ?? new GenerateConfig();
        }
        catch (JsonException ex)
        {
            throw new PrerequisiteError($"Invalid config for {subject}: {ex.Message}");
        }
    }

    private static string FieldName(PropertyInfo property) =>
        property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name);
}

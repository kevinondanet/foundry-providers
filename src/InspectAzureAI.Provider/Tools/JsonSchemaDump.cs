using System.Text.Json.Nodes;

namespace InspectAzureAI.Provider.Tools;

/// <summary>Port of <c>json_schema_dump</c> / <c>JSON_SCHEMA_EXTENDED_FIELDS</c> in <c>src/inspect_ai/util/_json.py</c>.</summary>
public static class JsonSchemaDump
{
    /// <summary>Extended JSON Schema validation fields that some providers may not support.</summary>
    public static readonly IReadOnlySet<string> JsonSchemaExtendedFields = new HashSet<string>
    {
        "pattern", "minLength", "maxLength", "minimum", "maximum", "examples",
    };

    /// <summary>
    /// Returns a copy of <paramref name="schema"/> (already dumped with None fields excluded) with every
    /// key in <paramref name="exclude"/> stripped recursively from <c>properties</c> values,
    /// <c>items</c>, <c>anyOf</c> entries and a dict-valued <c>additionalProperties</c>.
    /// </summary>
    public static JsonObject Dump(JsonObject schema, IReadOnlySet<string>? exclude = null)
    {
        var result = schema.DeepClone().AsObject();
        if (exclude is { Count: > 0 })
        {
            StripKeysRecursive(result, exclude);
        }

        return result;
    }

    private static void StripKeysRecursive(JsonObject d, IReadOnlySet<string> keys)
    {
        foreach (var key in keys)
        {
            d.Remove(key);
        }

        if (d["properties"] is JsonObject props)
        {
            foreach (var (_, propSchema) in props)
            {
                if (propSchema is JsonObject propObj)
                {
                    StripKeysRecursive(propObj, keys);
                }
            }
        }

        if (d["items"] is JsonObject items)
        {
            StripKeysRecursive(items, keys);
        }

        if (d["anyOf"] is JsonArray anyOf)
        {
            foreach (var item in anyOf)
            {
                if (item is JsonObject itemObj)
                {
                    StripKeysRecursive(itemObj, keys);
                }
            }
        }

        if (d["additionalProperties"] is JsonObject additional)
        {
            StripKeysRecursive(additional, keys);
        }
    }
}

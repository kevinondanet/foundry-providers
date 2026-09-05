using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace InspectAzureAI.Eval.Log.Json;

/// <summary>
/// JSON ↔ plain CLR values (null, bool, int/long/double, string, <c>List&lt;object?&gt;</c>,
/// <c>Dictionary&lt;string, object?&gt;</c>), the C# counterpart of Python's <c>json.loads</c> result used for
/// sample ids, metadata and the store.
/// </summary>
internal static class PlainJson
{
    public static object? ToObject(JsonNode? node) => node switch
    {
        null => null,
        JsonArray array => array.Select(ToObject).ToList(),
        JsonObject obj => ToDictionary(obj),
        JsonValue value => ToScalar(value),
        _ => throw new JsonException($"Unsupported JSON node {node.GetType().Name}."),
    };

    public static Dictionary<string, object?> ToDictionary(JsonObject obj) =>
        obj.ToDictionary(pair => pair.Key, pair => ToObject(pair.Value), StringComparer.Ordinal);

    public static object? ToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.GetString(),
        // box each branch separately: a shared ternary would promote everything to double
        JsonValueKind.Number => element.TryGetInt32(out var i) ? i : element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
        JsonValueKind.Array => element.EnumerateArray().Select(ToObject).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToObject(p.Value), StringComparer.Ordinal),
        _ => throw new JsonException($"Unsupported JSON value kind {element.ValueKind}."),
    };

    private static object? ToScalar(JsonValue value)
    {
        if (value.TryGetValue<bool>(out var b))
        {
            return b;
        }

        if (value.TryGetValue<int>(out var i))
        {
            return i;
        }

        if (value.TryGetValue<long>(out var l))
        {
            return l;
        }

        if (value.TryGetValue<double>(out var d))
        {
            return d;
        }

        if (value.TryGetValue<string>(out var s))
        {
            return s;
        }

        if (value.TryGetValue<JsonElement>(out var element))
        {
            return ToObject(element);
        }

        throw new JsonException($"Unsupported JSON scalar {value.ToJsonString()}.");
    }
}

/// <summary>
/// Makes <c>object</c>-typed members (sample ids, metadata and store values) read back as plain CLR values
/// instead of <see cref="JsonElement"/>, and write by runtime type.
/// </summary>
internal sealed class PlainObjectConverter : JsonConverter<object>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return PlainJson.ToObject(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        var type = value.GetType();
        // a bare object has nothing to serialize and would recurse into this converter
        if (type == typeof(object))
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
            return;
        }

        JsonSerializer.Serialize(writer, value, type, options);
    }
}

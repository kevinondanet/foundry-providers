using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace InspectAzureAI.Provider.Core;

/// <summary>
/// JSON Schema for a type (port of <c>JSONSchema</c> in <c>src/inspect_ai/util/_json.py</c>). Python aliases
/// <c>ToolParam</c> to this class; here <see cref="ToolParam"/> is a separate record with the same fields, and
/// <see cref="ToToolParam"/> / <see cref="FromToolParam"/> (plus the implicit conversions) bridge the two. Every
/// field defaults to null and <see cref="ToJson"/> mirrors <c>model_dump(exclude_none=True)</c>; System.Text.Json
/// serialises the record through the same dump (and reads it back with <see cref="FromJson"/>), so a logged
/// <c>response_schema</c> has Python's exact shape.
/// </summary>
[JsonConverter(typeof(JsonSchemaConverter))]
public sealed record JsonSchema
{
    /// <summary>Port of the <c>JSONType</c> literal: the valid values of <see cref="Type"/>.</summary>
    public static readonly IReadOnlySet<string> JsonTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "string", "integer", "number", "boolean", "array", "object", "null",
    };

    /// <summary>JSON type, or list of JSON types (<c>type: JSONType | list[JSONType]</c>).</summary>
    public IReadOnlyList<string>? Type { get; init; }

    /// <summary>Format of the parameter (e.g. <c>date-time</c>).</summary>
    public string? Format { get; init; }

    public string? Description { get; init; }

    public JsonNode? Default { get; init; }

    /// <summary>Valid values for enum parameters.</summary>
    public IReadOnlyList<JsonNode?>? Enum { get; init; }

    /// <summary>Valid type for array parameters.</summary>
    public JsonSchema? Items { get; init; }

    /// <summary>Valid fields for object parameters.</summary>
    public IReadOnlyDictionary<string, JsonSchema>? Properties { get; init; }

    /// <summary>A nested <see cref="JsonSchema"/>, a bool, or null.</summary>
    public object? AdditionalProperties { get; init; }

    /// <summary>Valid types for union parameters.</summary>
    public IReadOnlyList<JsonSchema>? AnyOf { get; init; }

    /// <summary>Required fields for object parameters.</summary>
    public IReadOnlyList<string>? Required { get; init; }

    public string? Pattern { get; init; }

    public int? MinLength { get; init; }

    public int? MaxLength { get; init; }

    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    public IReadOnlyList<JsonNode?>? Examples { get; init; }

    /// <summary>Convenience constructor for a single-typed schema.</summary>
    public static JsonSchema Of(string type, string? description = null)
    {
        if (!JsonTypes.Contains(type))
        {
            throw new ArgumentException($"'{type}' is not a JSON Schema type (expected one of {string.Join(", ", JsonTypes)}).", nameof(type));
        }

        return new JsonSchema { Type = [type], Description = description };
    }

    /// <summary>Port of <c>model_dump(exclude_none=True)</c> for this schema.</summary>
    public JsonObject ToJson() => ToToolParam().ToJson();

    /// <summary>
    /// Port of <c>JSONSchema(**dict)</c>: reads a schema object (a single or list-valued <c>type</c>, nested
    /// <c>items</c> / <c>properties</c> / <c>anyOf</c> / <c>additionalProperties</c>); keys pydantic would ignore
    /// (<c>title</c>, <c>$ref</c>, ...) are ignored. Anything but a JSON object is a <see cref="JsonException"/>.
    /// </summary>
    public static JsonSchema FromJson(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            throw new JsonException("A JSON schema must be a JSON object.");
        }

        return new JsonSchema
        {
            Type = obj["type"] switch
            {
                null => null,
                JsonArray types => types.Select(t => t?.GetValue<string>() ?? throw new JsonException("type entries must be strings.")).ToArray(),
                JsonValue single => [single.GetValue<string>()],
                _ => throw new JsonException("type must be a string or a list of strings."),
            },
            Format = obj["format"]?.GetValue<string>(),
            Description = obj["description"]?.GetValue<string>(),
            Default = obj["default"]?.DeepClone(),
            Enum = (obj["enum"] as JsonArray)?.Select(e => e?.DeepClone()).ToArray(),
            Items = obj["items"] is { } items ? FromJson(items) : null,
            Properties = (obj["properties"] as JsonObject)?.ToDictionary(p => p.Key, p => FromJson(p.Value), StringComparer.Ordinal),
            AdditionalProperties = obj["additionalProperties"] switch
            {
                null => null,
                JsonObject nested => FromJson(nested),
                JsonValue flag when flag.TryGetValue<bool>(out var allowed) => allowed,
                _ => throw new JsonException("additionalProperties must be a schema or a bool."),
            },
            AnyOf = (obj["anyOf"] as JsonArray)?.Select(FromJson).ToArray(),
            Required = (obj["required"] as JsonArray)?.Select(r => r?.GetValue<string>() ?? throw new JsonException("required entries must be strings.")).ToArray(),
            Pattern = obj["pattern"]?.GetValue<string>(),
            MinLength = obj["minLength"]?.GetValue<int>(),
            MaxLength = obj["maxLength"]?.GetValue<int>(),
            Minimum = obj["minimum"]?.GetValue<double>(),
            Maximum = obj["maximum"]?.GetValue<double>(),
            Examples = (obj["examples"] as JsonArray)?.Select(e => e?.DeepClone()).ToArray(),
        };
    }

    /// <summary>The same schema as a <see cref="ToolParam"/> (Python's alias), converted recursively.</summary>
    public ToolParam ToToolParam() => new()
    {
        Type = Type,
        Format = Format,
        Description = Description,
        Default = Default?.DeepClone(),
        Enum = Enum?.Select(e => e?.DeepClone()).ToArray(),
        Items = Items?.ToToolParam(),
        Properties = Properties?.ToDictionary(p => p.Key, p => p.Value.ToToolParam(), StringComparer.Ordinal),
        AdditionalProperties = AdditionalProperties switch
        {
            JsonSchema schema => schema.ToToolParam(),
            ToolParam param => param,
            bool allowed => allowed,
            null => null,
            _ => throw new InvalidOperationException($"additionalProperties must be a JsonSchema, a bool or null, not {AdditionalProperties.GetType().Name}."),
        },
        AnyOf = AnyOf?.Select(s => s.ToToolParam()).ToArray(),
        Required = Required,
        Pattern = Pattern,
        MinLength = MinLength,
        MaxLength = MaxLength,
        Minimum = Minimum,
        Maximum = Maximum,
        Examples = Examples?.Select(e => e?.DeepClone()).ToArray(),
    };

    /// <summary>A <see cref="ToolParam"/> as a <see cref="JsonSchema"/>, converted recursively.</summary>
    public static JsonSchema FromToolParam(ToolParam param)
    {
        ArgumentNullException.ThrowIfNull(param);
        return new JsonSchema
        {
            Type = param.Type,
            Format = param.Format,
            Description = param.Description,
            Default = param.Default?.DeepClone(),
            Enum = param.Enum?.Select(e => e?.DeepClone()).ToArray(),
            Items = param.Items is null ? null : FromToolParam(param.Items),
            Properties = param.Properties?.ToDictionary(p => p.Key, p => FromToolParam(p.Value), StringComparer.Ordinal),
            AdditionalProperties = param.AdditionalProperties switch
            {
                ToolParam nested => FromToolParam(nested),
                JsonSchema schema => schema,
                bool allowed => allowed,
                null => null,
                _ => throw new InvalidOperationException($"additionalProperties must be a ToolParam, a bool or null, not {param.AdditionalProperties.GetType().Name}."),
            },
            AnyOf = param.AnyOf?.Select(FromToolParam).ToArray(),
            Required = param.Required,
            Pattern = param.Pattern,
            MinLength = param.MinLength,
            MaxLength = param.MaxLength,
            Minimum = param.Minimum,
            Maximum = param.Maximum,
            Examples = param.Examples?.Select(e => e?.DeepClone()).ToArray(),
        };
    }

    /// <summary>The object schema of a <see cref="ToolParams"/> as a <see cref="JsonSchema"/>.</summary>
    public static JsonSchema FromToolParams(ToolParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return new JsonSchema
        {
            Type = ["object"],
            Properties = parameters.Properties.ToDictionary(p => p.Key, p => FromToolParam(p.Value), StringComparer.Ordinal),
            Required = parameters.Required,
            AdditionalProperties = parameters.AdditionalProperties switch
            {
                ToolParam nested => FromToolParam(nested),
                JsonSchema schema => schema,
                bool allowed => allowed,
                _ => null,
            },
        };
    }

    /// <summary>
    /// Port of <c>set_additional_properties_false</c>: a copy of the schema with <c>additionalProperties: false</c>
    /// set on it and, recursively, on <c>items</c>, every property and every <c>anyOf</c> member (Python mutates in
    /// place; records are immutable so a copy is returned).
    /// </summary>
    public static JsonSchema SetAdditionalPropertiesFalse(JsonSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return schema with
        {
            AdditionalProperties = false,
            Items = schema.Items is null ? null : SetAdditionalPropertiesFalse(schema.Items),
            Properties = schema.Properties?.ToDictionary(p => p.Key, p => SetAdditionalPropertiesFalse(p.Value), StringComparer.Ordinal),
            AnyOf = schema.AnyOf?.Select(SetAdditionalPropertiesFalse).ToArray(),
        };
    }

    public static implicit operator ToolParam(JsonSchema schema) => schema.ToToolParam();

    public static implicit operator JsonSchema(ToolParam param) => FromToolParam(param);
}

/// <summary>System.Text.Json converter writing a <see cref="JsonSchema"/> as its <see cref="JsonSchema.ToJson"/> dump and reading it with <see cref="JsonSchema.FromJson"/>.</summary>
public sealed class JsonSchemaConverter : JsonConverter<JsonSchema>
{
    public override JsonSchema Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonSchema.FromJson(JsonNode.Parse(ref reader));

    public override void Write(Utf8JsonWriter writer, JsonSchema value, JsonSerializerOptions options) =>
        value.ToJson().WriteTo(writer);
}

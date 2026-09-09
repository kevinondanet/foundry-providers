using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Context.Input;

/// <summary>A schema that does not describe a form the built-in input handlers can render (the port of a pydantic <c>ValidationError</c> on <c>ElicitationSchema.model_validate</c>).</summary>
public sealed class ElicitationSchemaException(string message) : Exception(message);

/// <summary>Port of <c>acp.schema.EnumOption</c>: a titled choice of a bounded string or multi-select property.</summary>
public sealed record EnumOption(string Const, string Title)
{
    public string? Description { get; init; }

    internal JsonObject ToJson()
    {
        var obj = new JsonObject { ["const"] = Const, ["title"] = Title };
        if (Description is not null)
        {
            obj["description"] = Description;
        }

        return obj;
    }
}

/// <summary>
/// One property of an <see cref="ElicitationSchema"/>: the subset of <c>acp.schema</c>'s <c>Elicitation*PropertySchema</c>
/// union the built-in handlers support (string, integer, number, boolean and the multi-select array).
/// </summary>
public abstract record ElicitationProperty
{
    /// <summary>The JSON Schema type name (<c>string</c>, <c>integer</c>, <c>number</c>, <c>boolean</c> or <c>array</c>).</summary>
    public abstract string Type { get; }

    public string? Title { get; init; }

    public string? Description { get; init; }

    /// <summary>The JSON Schema object (<c>model_dump(mode="json", exclude_none=True)</c>) for this property.</summary>
    public JsonObject ToJson()
    {
        var obj = new JsonObject { ["type"] = Type };
        if (Title is not null)
        {
            obj["title"] = Title;
        }

        if (Description is not null)
        {
            obj["description"] = Description;
        }

        WriteConstraints(obj);
        return obj;
    }

    private protected abstract void WriteConstraints(JsonObject obj);
}

/// <summary>Port of <c>ElicitationStringPropertySchema</c>: free-form text, or a bounded choice through <see cref="Enum"/> / <see cref="OneOf"/>.</summary>
public sealed record ElicitationStringProperty : ElicitationProperty
{
    public override string Type => "string";

    public int? MinLength { get; init; }

    public int? MaxLength { get; init; }

    /// <summary>A regular expression the whole value must match (Python's <c>re.fullmatch</c>).</summary>
    public string? Pattern { get; init; }

    public string? Format { get; init; }

    public string? Default { get; init; }

    public IReadOnlyList<string>? Enum { get; init; }

    public IReadOnlyList<EnumOption>? OneOf { get; init; }

    private protected override void WriteConstraints(JsonObject obj)
    {
        Json.Int(obj, "min_length", MinLength);
        Json.Int(obj, "max_length", MaxLength);
        Json.Str(obj, "pattern", Pattern);
        Json.Str(obj, "format", Format);
        Json.Str(obj, "default", Default);
        if (Enum is not null)
        {
            obj["enum"] = Json.Strings(Enum);
        }

        if (OneOf is not null)
        {
            obj["one_of"] = new JsonArray(OneOf.Select(option => (JsonNode?)option.ToJson()).ToArray());
        }
    }
}

/// <summary>Port of <c>ElicitationIntegerPropertySchema</c>.</summary>
public sealed record ElicitationIntegerProperty : ElicitationProperty
{
    public override string Type => "integer";

    public long? Minimum { get; init; }

    public long? Maximum { get; init; }

    public long? Default { get; init; }

    private protected override void WriteConstraints(JsonObject obj)
    {
        Json.Long(obj, "minimum", Minimum);
        Json.Long(obj, "maximum", Maximum);
        Json.Long(obj, "default", Default);
    }
}

/// <summary>Port of <c>ElicitationNumberPropertySchema</c>.</summary>
public sealed record ElicitationNumberProperty : ElicitationProperty
{
    public override string Type => "number";

    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    public double? Default { get; init; }

    private protected override void WriteConstraints(JsonObject obj)
    {
        Json.Double(obj, "minimum", Minimum);
        Json.Double(obj, "maximum", Maximum);
        Json.Double(obj, "default", Default);
    }
}

/// <summary>Port of <c>ElicitationBooleanPropertySchema</c>.</summary>
public sealed record ElicitationBooleanProperty : ElicitationProperty
{
    public override string Type => "boolean";

    public bool? Default { get; init; }

    private protected override void WriteConstraints(JsonObject obj)
    {
        if (Default is { } value)
        {
            obj["default"] = value;
        }
    }
}

/// <summary>The choices of an <see cref="ElicitationMultiSelectProperty"/>: titled (<see cref="TitledMultiSelectItems"/>) or bare strings (<see cref="StringMultiSelectItems"/>).</summary>
public abstract record MultiSelectItems
{
    internal abstract JsonObject ToJson();
}

/// <summary>Port of <c>TitledMultiSelectItems</c>: <c>items.any_of</c> as <c>{const, title}</c> options.</summary>
public sealed record TitledMultiSelectItems(IReadOnlyList<EnumOption> AnyOf) : MultiSelectItems
{
    internal override JsonObject ToJson() =>
        new() { ["any_of"] = new JsonArray(AnyOf.Select(option => (JsonNode?)option.ToJson()).ToArray()) };
}

/// <summary>Port of <c>StringMultiSelectItems</c>: <c>items.enum</c> as bare string choices.</summary>
public sealed record StringMultiSelectItems(IReadOnlyList<string> Enum) : MultiSelectItems
{
    internal override JsonObject ToJson() => new() { ["type"] = "string", ["enum"] = Json.Strings(Enum) };
}

/// <summary>Port of <c>ElicitationMultiSelectPropertySchema</c>: an array the operator fills by picking one or more of <see cref="Items"/>.</summary>
public sealed record ElicitationMultiSelectProperty(MultiSelectItems Items) : ElicitationProperty
{
    public override string Type => "array";

    public int? MinItems { get; init; }

    public int? MaxItems { get; init; }

    public IReadOnlyList<string>? Default { get; init; }

    private protected override void WriteConstraints(JsonObject obj)
    {
        obj["items"] = Items.ToJson();
        Json.Int(obj, "min_items", MinItems);
        Json.Int(obj, "max_items", MaxItems);
        if (Default is not null)
        {
            obj["default"] = Json.Strings(Default);
        }
    }
}

/// <summary>
/// Port of <c>acp.schema.ElicitationSchema</c> (the subset the built-in handlers render): the form an <c>ask_user</c>
/// question asks the operator to fill. Build it with the typed records, or <see cref="Parse"/> the JSON-Schema-shaped
/// object a model passes to the tool (<c>type</c> values are lowercased first, as <c>_normalize_schema_types</c> does).
/// Deviation: the ACP catch-all property and item types (<c>ElicitationOtherPropertySchema</c>, <c>OtherMultiSelectItems</c>)
/// are not modelled — <see cref="Parse"/> rejects them with the message <c>ask_user</c> reports for them.
/// </summary>
public sealed record ElicitationSchema
{
    public string Type => "object";

    public string? Title { get; init; }

    public string? Description { get; init; }

    /// <summary>The form fields in the order they are asked (insertion order, as a Python dict).</summary>
    public OrderedDictionary<string, ElicitationProperty> Properties { get; init; } = new(StringComparer.Ordinal);

    public IReadOnlyList<string>? Required { get; init; }

    /// <summary>Whether <paramref name="name"/> is listed in <see cref="Required"/>.</summary>
    public bool IsRequired(string name) => Required?.Contains(name, StringComparer.Ordinal) == true;

    /// <summary>The JSON Schema object (<c>model_dump(mode="json", exclude_none=True)</c>) — the value to pass as the tool's <c>schema</c> argument.</summary>
    public JsonObject ToJson()
    {
        var obj = new JsonObject { ["type"] = Type };
        if (Title is not null)
        {
            obj["title"] = Title;
        }

        if (Description is not null)
        {
            obj["description"] = Description;
        }

        var properties = new JsonObject();
        foreach (var pair in Properties)
        {
            properties[pair.Key] = pair.Value.ToJson();
        }

        obj["properties"] = properties;
        if (Required is not null)
        {
            obj["required"] = Json.Strings(Required);
        }

        return obj;
    }

    /// <summary>
    /// Port of <c>ElicitationSchema.model_validate(_normalize_schema_types(schema))</c> plus <c>ask_user</c>'s rejection
    /// of unsupported types: reads a JSON-Schema-shaped object. Unknown keys are ignored (as pydantic does); a value of
    /// the wrong kind, a property type outside string/integer/number/boolean/array, or array items without
    /// <c>any_of</c> or <c>enum</c> is an <see cref="ElicitationSchemaException"/> whose message names the offending path.
    /// Deviation: <c>items.enum</c> is accepted without the <c>"type": "string"</c> pydantic's <c>StringMultiSelectItems</c> insists on
    /// (the tool description documents the bare form).
    /// </summary>
    public static ElicitationSchema Parse(JsonNode? schema)
    {
        if (schema is not JsonObject root)
        {
            throw new ElicitationSchemaException("Input should be a valid dictionary");
        }

        var properties = new OrderedDictionary<string, ElicitationProperty>(StringComparer.Ordinal);
        if (root["properties"] is { } propertiesNode)
        {
            if (propertiesNode is not JsonObject propertiesObject)
            {
                throw new ElicitationSchemaException("properties: Input should be a valid dictionary");
            }

            foreach (var pair in propertiesObject)
            {
                properties[pair.Key] = ParseProperty(pair.Key, pair.Value);
            }
        }

        return new ElicitationSchema
        {
            Title = Json.OptionalString(root, "title", "title"),
            Description = Json.OptionalString(root, "description", "description"),
            Properties = properties,
            Required = Json.OptionalStrings(root, "required", "required"),
        };
    }

    private static ElicitationProperty ParseProperty(string name, JsonNode? node)
    {
        var path = $"properties.{name}";
        if (node is not JsonObject obj)
        {
            throw new ElicitationSchemaException($"{path}: Input should be a valid dictionary");
        }

        var type = Json.OptionalString(obj, "type", $"{path}.type")?.ToLowerInvariant()
            ?? throw new ElicitationSchemaException($"{path}.type: Field required");
        var title = Json.OptionalString(obj, "title", $"{path}.title");
        var description = Json.OptionalString(obj, "description", $"{path}.description");
        switch (type)
        {
            case "string":
                return new ElicitationStringProperty
                {
                    Title = title,
                    Description = description,
                    MinLength = Json.OptionalInt(obj, "min_length", $"{path}.min_length"),
                    MaxLength = Json.OptionalInt(obj, "max_length", $"{path}.max_length"),
                    Pattern = Json.OptionalString(obj, "pattern", $"{path}.pattern"),
                    Format = Json.OptionalString(obj, "format", $"{path}.format"),
                    Default = Json.OptionalString(obj, "default", $"{path}.default"),
                    Enum = Json.OptionalStrings(obj, "enum", $"{path}.enum"),
                    OneOf = Json.OptionalOptions(obj, "one_of", $"{path}.one_of"),
                };
            case "integer":
                return new ElicitationIntegerProperty
                {
                    Title = title,
                    Description = description,
                    Minimum = Json.OptionalLong(obj, "minimum", $"{path}.minimum"),
                    Maximum = Json.OptionalLong(obj, "maximum", $"{path}.maximum"),
                    Default = Json.OptionalLong(obj, "default", $"{path}.default"),
                };
            case "number":
                return new ElicitationNumberProperty
                {
                    Title = title,
                    Description = description,
                    Minimum = Json.OptionalDouble(obj, "minimum", $"{path}.minimum"),
                    Maximum = Json.OptionalDouble(obj, "maximum", $"{path}.maximum"),
                    Default = Json.OptionalDouble(obj, "default", $"{path}.default"),
                };
            case "boolean":
                return new ElicitationBooleanProperty
                {
                    Title = title,
                    Description = description,
                    Default = Json.OptionalBool(obj, "default", $"{path}.default"),
                };
            case "array":
                return new ElicitationMultiSelectProperty(ParseItems(name, obj["items"], $"{path}.items"))
                {
                    Title = title,
                    Description = description,
                    MinItems = Json.OptionalInt(obj, "min_items", $"{path}.min_items"),
                    MaxItems = Json.OptionalInt(obj, "max_items", $"{path}.max_items"),
                    Default = Json.OptionalStrings(obj, "default", $"{path}.default"),
                };
            default:
                throw new ElicitationSchemaException($"property '{name}' has unsupported type '{type}'");
        }
    }

    private static MultiSelectItems ParseItems(string name, JsonNode? node, string path)
    {
        if (node is not JsonObject obj)
        {
            throw new ElicitationSchemaException($"{path}: Field required");
        }

        if (obj["any_of"] is not null)
        {
            return new TitledMultiSelectItems(Json.OptionalOptions(obj, "any_of", $"{path}.any_of")!);
        }

        if (obj["enum"] is not null)
        {
            return new StringMultiSelectItems(Json.OptionalStrings(obj, "enum", $"{path}.enum")!);
        }

        var type = Json.OptionalString(obj, "type", $"{path}.type");
        throw new ElicitationSchemaException(type is null
            ? $"{path}: items must declare 'any_of' or 'enum'"
            : $"property '{name}' has unsupported items type '{type}'");
    }
}

/// <summary>JSON helpers whose failures carry the pydantic-style path of the offending value.</summary>
file static class Json
{
    public static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    public static void Str(JsonObject obj, string key, string? value)
    {
        if (value is not null)
        {
            obj[key] = value;
        }
    }

    public static void Int(JsonObject obj, string key, int? value)
    {
        if (value is { } v)
        {
            obj[key] = v;
        }
    }

    public static void Long(JsonObject obj, string key, long? value)
    {
        if (value is { } v)
        {
            obj[key] = v;
        }
    }

    public static void Double(JsonObject obj, string key, double? value)
    {
        if (value is { } v)
        {
            obj[key] = v;
        }
    }

    public static string? OptionalString(JsonObject obj, string key, string path)
    {
        var node = obj[key];
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        throw new ElicitationSchemaException($"{path}: Input should be a valid string");
    }

    public static int? OptionalInt(JsonObject obj, string key, string path)
    {
        var value = OptionalLong(obj, key, path);
        if (value is { } v && (v < int.MinValue || v > int.MaxValue))
        {
            throw new ElicitationSchemaException($"{path}: Input should be a valid integer");
        }

        return (int?)value;
    }

    public static long? OptionalLong(JsonObject obj, string key, string path)
    {
        var node = obj[key];
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            // a node may be backed by a JsonElement (parsed) or a CLR number (built with ToJson); read by kind
            var text = value.GetValueKind() switch
            {
                JsonValueKind.Number => value.ToJsonString(),
                JsonValueKind.String => value.GetValue<string>(),
                _ => null,
            };
            if (text is not null)
            {
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                {
                    return integer;
                }

                // pydantic accepts integral floats (and numeric strings) in lax mode
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && Math.Floor(number) == number && Math.Abs(number) < 9.2e18)
                {
                    return (long)number;
                }
            }
        }

        throw new ElicitationSchemaException($"{path}: Input should be a valid integer");
    }

    public static double? OptionalDouble(JsonObject obj, string key, string path)
    {
        var node = obj[key];
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            var text = value.GetValueKind() switch
            {
                JsonValueKind.Number => value.ToJsonString(),
                JsonValueKind.String => value.GetValue<string>(),
                _ => null,
            };
            if (text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return number;
            }
        }

        throw new ElicitationSchemaException($"{path}: Input should be a valid number");
    }

    public static bool? OptionalBool(JsonObject obj, string key, string path)
    {
        var node = obj[key];
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetValueKind() == JsonValueKind.True;
        }

        throw new ElicitationSchemaException($"{path}: Input should be a valid boolean");
    }

    public static IReadOnlyList<string>? OptionalStrings(JsonObject obj, string key, string path)
    {
        var node = obj[key];
        if (node is null)
        {
            return null;
        }

        if (node is not JsonArray array)
        {
            throw new ElicitationSchemaException($"{path}: Input should be a valid list");
        }

        var result = new List<string>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is JsonValue value && value.TryGetValue<string>(out var text))
            {
                result.Add(text);
            }
            else
            {
                throw new ElicitationSchemaException($"{path}.{i}: Input should be a valid string");
            }
        }

        return result;
    }

    public static IReadOnlyList<EnumOption>? OptionalOptions(JsonObject obj, string key, string path)
    {
        var node = obj[key];
        if (node is null)
        {
            return null;
        }

        if (node is not JsonArray array)
        {
            throw new ElicitationSchemaException($"{path}: Input should be a valid list");
        }

        var result = new List<EnumOption>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject option)
            {
                throw new ElicitationSchemaException($"{path}.{i}: Input should be a valid dictionary");
            }

            var constant = OptionalString(option, "const", $"{path}.{i}.const") ?? throw new ElicitationSchemaException($"{path}.{i}.const: Field required");
            var title = OptionalString(option, "title", $"{path}.{i}.title") ?? throw new ElicitationSchemaException($"{path}.{i}.title: Field required");
            result.Add(new EnumOption(constant, title) { Description = OptionalString(option, "description", $"{path}.{i}.description") });
        }

        return result;
    }
}

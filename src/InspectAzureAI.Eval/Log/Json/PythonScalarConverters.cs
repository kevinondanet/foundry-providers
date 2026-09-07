using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace InspectAzureAI.Eval.Log.Json;

/// <summary>
/// Doubles in pydantic-core's format: <c>1.0</c>, <c>0.00001</c>, <c>1e+16</c> and the bare <c>NaN</c> /
/// <c>Infinity</c> constants (<c>ser_json_inf_nan="constants"</c>). Reads the sentinel strings that
/// <see cref="PythonJsonFormat.SanitizeNonFinite"/> produces, the quoted named literals, and <c>null</c> as NaN
/// (the form this port's version-1 logs used for non-finite values).
/// </summary>
internal sealed class PythonDoubleConverter : JsonConverter<double>
{
    public override bool HandleNull => true;

    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Null => double.NaN,
        JsonTokenType.String => PythonJsonFormat.TryNonFinite(reader.GetString(), out var value) ? value : throw new JsonException($"'{reader.GetString()}' is not a number."),
        _ => reader.GetDouble(),
    };

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) => PythonJsonFormat.WriteDouble(writer, value);
}

/// <summary>Port of <c>datetime_to_iso_format_safe</c> / the <c>UtcDatetime</c> validator for every <see cref="DateTimeOffset"/> in the log.</summary>
internal sealed class IsoDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        PythonJsonFormat.ParseDateTime(reader.GetString() ?? throw new JsonException("A datetime cannot be null."));

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(PythonJsonFormat.FormatIso(value));
}

/// <summary>Pydantic's default datetime format (<c>Z</c> suffix), for the fields Python does not route through <c>datetime_to_iso_format_safe</c>.</summary>
internal sealed class PydanticDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        PythonJsonFormat.ParseDateTime(reader.GetString() ?? throw new JsonException("A datetime cannot be null."));

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(PythonJsonFormat.FormatPydantic(value));
}

/// <summary>Port of <c>EvalStats.started_at: UtcDatetimeStr | Literal[""]</c>: an unset time is written as <c>""</c>.</summary>
internal sealed class EmptyStringDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    public override bool HandleNull => true;

    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        var text = reader.GetString();
        return string.IsNullOrEmpty(text) ? null : PythonJsonFormat.ParseDateTime(text);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value is { } time ? PythonJsonFormat.FormatIso(time) : "");
}

/// <summary>
/// Routes every <see cref="JsonNode"/>-typed member through <see cref="PythonJsonFormat.WriteNode"/> so raw JSON
/// (tool arguments, model calls, info data, patches) writes NaN constants like the rest of the log.
/// </summary>
internal sealed class PythonJsonNodeConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeof(JsonNode).IsAssignableFrom(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert))!;

    private sealed class Converter<T> : JsonConverter<T> where T : JsonNode
    {
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var node = JsonNode.Parse(ref reader);
            return node switch
            {
                null => null,
                T typed => typed,
                _ => throw new JsonException($"Expected a {typeof(T).Name} but found a {node.GetType().Name}."),
            };
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => PythonJsonFormat.WriteNode(writer, value);
    }
}

/// <summary>Port of <c>int | str</c> fields (<c>EvalSpec.task_version</c>): digit-only strings are written as numbers, numbers read back as strings.</summary>
internal sealed class IntOrStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Number => reader.TryGetInt64(out var number) ? number.ToString(CultureInfo.InvariantCulture) : reader.GetDouble().ToString("R", CultureInfo.InvariantCulture),
        JsonTokenType.String => reader.GetString() ?? "",
        _ => throw new JsonException("Expected a number or a string."),
    };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
        {
            writer.WriteNumberValue(number);
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }
}

/// <summary>
/// The shared vocabulary of the hand-written converters: writers that omit <c>null</c> (pydantic's
/// <c>exclude_none=True</c>) and readers over a parsed <see cref="JsonElement"/> that treat a missing or null
/// property as absent.
/// </summary>
internal static class JsonIo
{
    public static void Str(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    public static void Int(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
    }

    public static void Long(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
    }

    public static void Dbl(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number)
        {
            writer.WritePropertyName(name);
            PythonJsonFormat.WriteDouble(writer, number);
        }
    }

    public static void Bool(Utf8JsonWriter writer, string name, bool? value)
    {
        if (value is { } flag)
        {
            writer.WriteBoolean(name, flag);
        }
    }

    public static void Time(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is { } time)
        {
            writer.WriteString(name, PythonJsonFormat.FormatIso(time));
        }
    }

    public static void TimeZ(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is { } time)
        {
            writer.WriteString(name, PythonJsonFormat.FormatPydantic(time));
        }
    }

    public static void Node(Utf8JsonWriter writer, string name, JsonNode? value, bool always = false)
    {
        if (value is null && !always)
        {
            return;
        }

        writer.WritePropertyName(name);
        PythonJsonFormat.WriteNode(writer, value);
    }

    /// <summary>Writes a raw JSON member in Python form (see <see cref="PythonJsonFormat.WriteElement"/>); skipped when null or undefined.</summary>
    public static void Element(Utf8JsonWriter writer, string name, JsonElement? value)
    {
        if (value is { } element && element.ValueKind != JsonValueKind.Undefined)
        {
            writer.WritePropertyName(name);
            PythonJsonFormat.WriteElement(writer, element);
        }
    }

    /// <summary>Serializes a member with the log options; skipped when null unless <paramref name="always"/>.</summary>
    public static void Obj<T>(Utf8JsonWriter writer, string name, T? value, JsonSerializerOptions options, bool always = false)
    {
        if (value is null && !always)
        {
            return;
        }

        writer.WritePropertyName(name);
        JsonSerializer.Serialize(writer, value, options);
    }

    public static bool Has(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;

    public static JsonElement? Prop(JsonElement element, string name) =>
        Has(element, name) ? element.GetProperty(name) : null;

    public static string? Str(JsonElement element, string name) =>
        Prop(element, name) is { } value ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw new JsonException($"'{name}' must be a string.") : null;

    public static int? Int(JsonElement element, string name) =>
        Prop(element, name) is { } value ? value.ValueKind == JsonValueKind.Number ? (int)value.GetDouble() : throw new JsonException($"'{name}' must be a number.") : null;

    public static long? Long(JsonElement element, string name) =>
        Prop(element, name) is { } value ? value.ValueKind == JsonValueKind.Number ? (long)value.GetDouble() : throw new JsonException($"'{name}' must be a number.") : null;

    public static double? Dbl(JsonElement element, string name)
    {
        if (Prop(element, name) is not { } value)
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when PythonJsonFormat.TryNonFinite(value.GetString(), out var nonFinite) => nonFinite,
            _ => throw new JsonException($"'{name}' must be a number."),
        };
    }

    public static bool? Bool(JsonElement element, string name) =>
        Prop(element, name) is { } value
            ? value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new JsonException($"'{name}' must be a boolean.")
            : null;

    public static DateTimeOffset? Time(JsonElement element, string name) =>
        Str(element, name) is { Length: > 0 } text ? PythonJsonFormat.ParseDateTime(text) : null;

    public static JsonNode? Node(JsonElement element, string name) =>
        Prop(element, name) is { } value ? JsonNode.Parse(value.GetRawText()) : null;

    public static JsonObject? Object(JsonElement element, string name) =>
        Node(element, name) is { } node ? node as JsonObject ?? throw new JsonException($"'{name}' must be an object.") : null;

    public static JsonElement? Element(JsonElement element, string name) =>
        Prop(element, name) is { } value ? value.Clone() : null;

    public static T? Get<T>(JsonElement element, string name, JsonSerializerOptions options) =>
        Prop(element, name) is { } value ? value.Deserialize<T>(options) : default;

    public static T Require<T>(JsonElement element, string name, JsonSerializerOptions options) =>
        Get<T>(element, name, options) ?? throw new JsonException($"'{name}' is required.");

    public static string RequireStr(JsonElement element, string name) =>
        Str(element, name) ?? throw new JsonException($"'{name}' is required.");
}

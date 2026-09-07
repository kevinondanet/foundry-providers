using System.Text.Json;
using System.Text.Json.Serialization;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Scorers;

namespace InspectAzureAI.Eval.Log.Json;

/// <summary>
/// Port of the <c>Value</c> union of <c>scorer/_metric.py</c> as JSON. Non-finite numbers are written as the
/// <c>NaN</c> / <c>Infinity</c> constants (see <c>design/nan-serialization.md</c>) and read back from the sentinels
/// <see cref="PythonJsonFormat.SanitizeNonFinite"/> leaves; a <c>null</c> root reads as NaN (Python's
/// <c>ScoreEvent</c> coercion for old logs, and this port's version-1 form), a null dictionary leaf stays null.
/// </summary>
internal sealed class ScoreValueConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeof(ScoreValue).IsAssignableFrom(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert))!;

    private sealed class Converter<T> : JsonConverter<T> where T : ScoreValue
    {
        public override bool HandleNull => true;

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return ScoreValue.NaN as T ?? throw new JsonException($"Score value is not a {typeof(T).Name}.");
            }

            using var document = JsonDocument.ParseValue(ref reader);
            return ReadValue(document.RootElement) as T ?? throw new JsonException($"Score value is not a {typeof(T).Name}.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => WriteValue(writer, value);
    }

    /// <summary>A score value from its JSON, mapping non-finite sentinels back to numbers.</summary>
    public static ScoreValue ReadValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => new ScoreValue.Bool(true),
        JsonValueKind.False => new ScoreValue.Bool(false),
        JsonValueKind.Number => new ScoreValue.Num(element.GetDouble()),
        JsonValueKind.String => PythonJsonFormat.IsSentinel(element.GetString()) && PythonJsonFormat.TryNonFinite(element.GetString(), out var nonFinite)
            ? new ScoreValue.Num(nonFinite)
            : new ScoreValue.Str(element.GetString()!),
        JsonValueKind.Array => new ScoreValue.List(element.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Null ? throw new JsonException("A score list cannot contain null.") : ReadValue(item))
            .ToArray()),
        JsonValueKind.Object => new ScoreValue.Dict(element.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.ValueKind == JsonValueKind.Null ? null : ReadValue(property.Value), StringComparer.Ordinal)),
        _ => throw new JsonException($"Unsupported score value {element.ValueKind}."),
    };

    /// <summary>Writes a score value in Python form (non-finite numbers as bare constants).</summary>
    public static void WriteValue(Utf8JsonWriter writer, ScoreValue? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case ScoreValue.Str s:
                writer.WriteStringValue(s.Value);
                break;
            case ScoreValue.Num n:
                PythonJsonFormat.WriteDouble(writer, n.Value);
                break;
            case ScoreValue.Bool b:
                writer.WriteBooleanValue(b.Value);
                break;
            case ScoreValue.List l:
                writer.WriteStartArray();
                foreach (var item in l.Items)
                {
                    WriteValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            case ScoreValue.Dict d:
                writer.WriteStartObject();
                foreach (var pair in d.Items)
                {
                    writer.WritePropertyName(pair.Key);
                    WriteValue(writer, pair.Value);
                }

                writer.WriteEndObject();
                break;
            default:
                throw new JsonException($"Unknown score value {value.GetType().Name}.");
        }
    }
}

/// <summary>Port of <c>scorer/_metric.py</c> <c>Score</c> as JSON: <c>value</c>, <c>answer</c>, <c>explanation</c>, <c>reason</c>, <c>metadata</c>, <c>history</c>.</summary>
internal sealed class ScoreConverter : JsonConverter<Score>
{
    public override Score Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;
        var value = element.TryGetProperty("value", out var valueElement)
            ? valueElement.Deserialize<ScoreValue>(options) ?? ScoreValue.NaN
            : throw new JsonException("A score requires 'value'.");
        return new Score(value)
        {
            Answer = GetString(element, "answer"),
            Explanation = GetString(element, "explanation"),
            Reason = GetString(element, "reason"),
            Metadata = element.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
                ? metadata.Deserialize<Dictionary<string, object?>>(options)
                : null,
            History = JsonIo.Get<List<ScoreEdit>>(element, "history", options) ?? [],
        };
    }

    public override void Write(Utf8JsonWriter writer, Score value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        WriteFields(writer, value, options);
        writer.WriteEndObject();
    }

    /// <summary>Writes the members of a score into an open object (shared with <see cref="EvalSampleScoreConverter"/>).</summary>
    public static void WriteFields(Utf8JsonWriter writer, Score value, JsonSerializerOptions options)
    {
        writer.WritePropertyName("value");
        JsonSerializer.Serialize(writer, value.Value, options);
        if (value.Answer is { } answer)
        {
            writer.WriteString("answer", answer);
        }

        if (value.Explanation is { } explanation)
        {
            writer.WriteString("explanation", explanation);
        }

        if (value.Reason is { } reason)
        {
            writer.WriteString("reason", reason);
        }

        if (value.Metadata is { } metadata)
        {
            writer.WritePropertyName("metadata");
            JsonSerializer.Serialize(writer, metadata, options);
        }

        writer.WritePropertyName("history");
        JsonSerializer.Serialize(writer, value.History, options);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

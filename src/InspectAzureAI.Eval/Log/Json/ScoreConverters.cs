using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InspectAzureAI.Eval.Scorers;

namespace InspectAzureAI.Eval.Log.Json;

/// <summary>
/// Port of the <c>Value</c> union of <c>scorer/_metric.py</c> as JSON via <see cref="ScoreValue.ToJson"/>;
/// the unscored NaN sentinel is written as <c>null</c> (no JSON constant exists) and reads back as NaN.
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
            var value = reader.TokenType == JsonTokenType.Null ? ScoreValue.NaN : ScoreValue.FromJson(JsonNode.Parse(ref reader));
            return value as T ?? throw new JsonException($"Score value is not a {typeof(T).Name}.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (value.IsNaN)
            {
                writer.WriteNullValue();
                return;
            }

            var node = value.ToJson();
            if (node is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                node.WriteTo(writer);
            }
        }
    }
}

/// <summary>Port of <c>scorer/_metric.py</c> <c>Score</c> as JSON: <c>value</c>, <c>answer</c>, <c>explanation</c>, <c>reason</c>, <c>metadata</c>.</summary>
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
        };
    }

    public override void Write(Utf8JsonWriter writer, Score value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
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

        writer.WriteEndObject();
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

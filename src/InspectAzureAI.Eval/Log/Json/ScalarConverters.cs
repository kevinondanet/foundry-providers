using System.Text.Json;
using System.Text.Json.Serialization;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Log.Json;

/// <summary>Port of the Python <c>StopReason</c> literal wire names ("stop", "max_tokens", ...).</summary>
internal sealed class StopReasonConverter : JsonConverter<StopReason>
{
    public override StopReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        // Python's ChatCompletionChoice validator migrates the legacy "length" value
        if (text == "length")
        {
            return StopReason.MaxTokens;
        }

        foreach (var reason in Enum.GetValues<StopReason>())
        {
            if (reason.ToWire() == text)
            {
                return reason;
            }
        }

        throw new JsonException($"Unknown stop reason '{text}'.");
    }

    public override void Write(Utf8JsonWriter writer, StopReason value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWire());
}

/// <summary>Port of the Python <c>ToolChoice</c> union: a preset string or a <c>{"name": ...}</c> object.</summary>
internal sealed class ToolChoiceConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeof(ToolChoice).IsAssignableFrom(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert))!;

    private sealed class Converter<T> : JsonConverter<T> where T : ToolChoice
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            ToolChoice choice;
            if (reader.TokenType == JsonTokenType.String)
            {
                var preset = reader.GetString();
                choice = preset switch
                {
                    "auto" => ToolChoice.Auto,
                    "any" => ToolChoice.Any,
                    "none" => ToolChoice.None,
                    _ => throw new JsonException($"Unknown tool choice '{preset}'."),
                };
            }
            else
            {
                using var document = JsonDocument.ParseValue(ref reader);
                choice = document.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                    ? new ToolFunction(name.GetString()!)
                    : throw new JsonException("A tool choice object requires a 'name'.");
            }

            return choice as T ?? throw new JsonException($"Tool choice is not a {typeof(T).Name}.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (value is ToolFunction function)
            {
                writer.WriteStartObject();
                writer.WriteString("name", function.Name);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteStringValue(value.ToString());
            }
        }
    }
}

/// <summary>Port of the <c>str | list[str]</c> target: a single value is written as a string, several as a list.</summary>
internal sealed class TargetConverter : JsonConverter<Target>
{
    public override Target Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new Target(reader.GetString()!);
        }

        var values = JsonSerializer.Deserialize<List<string>>(ref reader, options) ?? throw new JsonException("A target cannot be null.");
        return new Target(values);
    }

    public override void Write(Utf8JsonWriter writer, Target value, JsonSerializerOptions options)
    {
        if (value.Count == 1)
        {
            writer.WriteStringValue(value[0]);
        }
        else
        {
            JsonSerializer.Serialize(writer, value.Values, options);
        }
    }
}

/// <summary>Port of the <c>str | list[ChatMessage]</c> input.</summary>
internal sealed class SampleInputConverter : JsonConverter<SampleInput>
{
    public override SampleInput Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString()!;
        }

        var messages = JsonSerializer.Deserialize<List<ChatMessage>>(ref reader, options) ?? throw new JsonException("An input cannot be null.");
        return messages;
    }

    public override void Write(Utf8JsonWriter writer, SampleInput value, JsonSerializerOptions options)
    {
        if (value.IsText)
        {
            writer.WriteStringValue(value.Text ?? "");
        }
        else
        {
            JsonSerializer.Serialize(writer, value.Messages, options);
        }
    }
}

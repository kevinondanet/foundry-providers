using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Log.Json;

/// <summary>Port of <c>model/_model_call.py</c> <c>ModelCall</c> as JSON: <c>request</c>, <c>response</c>, <c>error</c>, <c>time</c>.</summary>
internal sealed class ModelCallConverter : JsonConverter<ModelCall>
{
    public override ModelCall Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("A model call must be an object.");
        var request = node["request"] as JsonObject ?? new JsonObject();
        var call = ModelCall.Create(request);
        var time = node["time"] is JsonValue timeValue && timeValue.TryGetValue<double>(out var t) ? t : (double?)null;
        var response = node["response"];
        if (node["error"] is JsonValue errorValue && errorValue.TryGetValue<bool>(out var error) && error)
        {
            call.SetError(response, time);
        }
        else if (response is not null || time is not null)
        {
            call.SetResponse(response, time);
        }

        return call;
    }

    public override void Write(Utf8JsonWriter writer, ModelCall value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("request");
        value.Request.WriteTo(writer);
        if (value.Response is { } response)
        {
            writer.WritePropertyName("response");
            response.WriteTo(writer);
        }

        if (value.Error is { } error)
        {
            writer.WriteBoolean("error", error);
        }

        if (value.Time is { } time)
        {
            writer.WriteNumber("time", time);
        }

        writer.WriteEndObject();
    }
}

/// <summary>
/// Port of <c>model/_model_output.py</c> <c>ModelOutput</c> as JSON (<c>model</c>, <c>choices</c>, <c>usage</c>,
/// <c>time</c>, <c>metadata</c>, <c>error</c>). Hand-written because the record's computed <c>Message</c> /
/// <c>StopReason</c> throw on an empty output; an explicitly set <c>Completion</c> that differs from the first
/// choice's text is preserved under <c>completion</c>.
/// </summary>
internal sealed class ModelOutputConverter : JsonConverter<ModelOutput>
{
    public override ModelOutput Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;
        var output = new ModelOutput
        {
            Model = Get<string>(element, "model", options) ?? "",
            Choices = Get<List<ChatCompletionChoice>>(element, "choices", options) ?? [],
            Usage = Get<ModelUsage>(element, "usage", options),
            Time = element.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Number ? time.GetDouble() : null,
            Metadata = Get<Dictionary<string, object?>>(element, "metadata", options),
            Error = Get<string>(element, "error", options),
            Fallback = Get<ModelFallback>(element, "fallback", options),
        };
        return Get<string>(element, "completion", options) is { } completion ? output with { Completion = completion } : output;
    }

    public override void Write(Utf8JsonWriter writer, ModelOutput value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("model", value.Model);
        writer.WritePropertyName("choices");
        JsonSerializer.Serialize(writer, value.Choices, options);
        if (value.Usage is { } usage)
        {
            writer.WritePropertyName("usage");
            JsonSerializer.Serialize(writer, usage, options);
        }

        if (value.Time is { } time)
        {
            writer.WriteNumber("time", time);
        }

        if (value.Metadata is { } metadata)
        {
            writer.WritePropertyName("metadata");
            JsonSerializer.Serialize(writer, metadata, options);
        }

        if (value.Error is { } error)
        {
            writer.WriteString("error", error);
        }

        if (value.Fallback is { } fallback)
        {
            writer.WritePropertyName("fallback");
            JsonSerializer.Serialize(writer, fallback, options);
        }

        var derived = value.Choices.Count > 0 ? value.Choices[0].Message.Text : "";
        if (value.Completion != derived)
        {
            writer.WriteString("completion", value.Completion);
        }

        writer.WriteEndObject();
    }

    private static TValue? Get<TValue>(JsonElement element, string name, JsonSerializerOptions options) where TValue : class =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.Deserialize<TValue>(options) : null;
}

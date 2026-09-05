using System.Text.Json;
using System.Text.Json.Serialization;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Log.Json;

/// <summary>
/// Port of the <c>Content</c> union of <c>_util/content.py</c> as JSON: <c>{"type": "text" | "reasoning" |
/// "image" | "audio" | "video", ...}</c> with Python's field names.
/// </summary>
internal sealed class ContentConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeof(Content).IsAssignableFrom(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert))!;

    public static Content ReadContent(JsonElement element)
    {
        var type = element.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        return type switch
        {
            "text" => new ContentText(GetString(element, "text") ?? "") { Refusal = GetBool(element, "refusal") },
            "reasoning" => new ContentReasoning(GetString(element, "reasoning") ?? "", GetString(element, "signature"), GetBool(element, "redacted") ?? false),
            "image" => new ContentImage(GetString(element, "image") ?? "", GetString(element, "detail") ?? "auto"),
            "audio" => new ContentAudio(GetString(element, "audio") ?? "", GetString(element, "format") ?? ""),
            "video" => new ContentVideo(GetString(element, "video") ?? "", GetString(element, "format") ?? ""),
            "document" => new ContentDocument(GetString(element, "document") ?? "", GetString(element, "filename") ?? "", GetString(element, "mime_type") ?? "")
            {
                Citations = GetBool(element, "citations") ?? false,
            },
            _ => throw new JsonException($"Unsupported content type '{type}'."),
        };
    }

    public static void WriteContent(Utf8JsonWriter writer, Content content)
    {
        writer.WriteStartObject();
        writer.WriteString("type", content.Type);
        switch (content)
        {
            case ContentText text:
                writer.WriteString("text", text.Text);
                if (text.Refusal is { } refusal)
                {
                    writer.WriteBoolean("refusal", refusal);
                }

                break;
            case ContentReasoning reasoning:
                writer.WriteString("reasoning", reasoning.Reasoning);
                if (reasoning.Signature is { } signature)
                {
                    writer.WriteString("signature", signature);
                }

                writer.WriteBoolean("redacted", reasoning.Redacted);
                break;
            case ContentImage image:
                writer.WriteString("image", image.Image);
                writer.WriteString("detail", image.Detail);
                break;
            case ContentAudio audio:
                writer.WriteString("audio", audio.Audio);
                writer.WriteString("format", audio.Format);
                break;
            case ContentVideo video:
                writer.WriteString("video", video.Video);
                writer.WriteString("format", video.Format);
                break;
            case ContentDocument document:
                writer.WriteString("document", document.Document);
                writer.WriteString("filename", document.Filename);
                writer.WriteString("mime_type", document.MimeType);
                writer.WriteBoolean("citations", document.Citations);
                break;
            default:
                throw new JsonException($"Unsupported content {content.GetType().Name}.");
        }

        writer.WriteEndObject();
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private sealed class Converter<T> : JsonConverter<T> where T : Content
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return ReadContent(document.RootElement) as T ?? throw new JsonException($"Content is not a {typeof(T).Name}.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => WriteContent(writer, value);
    }
}

/// <summary>Port of the <c>str | list[Content]</c> message content.</summary>
internal sealed class MessageContentConverter : JsonConverter<MessageContent>
{
    public override MessageContent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return MessageContent.FromString(reader.GetString()!);
        }

        var items = JsonSerializer.Deserialize<List<Content>>(ref reader, options) ?? throw new JsonException("Message content cannot be null.");
        return MessageContent.FromItems(items);
    }

    public override void Write(Utf8JsonWriter writer, MessageContent value, JsonSerializerOptions options)
    {
        if (value.IsString)
        {
            writer.WriteStringValue(value.Text!);
            return;
        }

        writer.WriteStartArray();
        foreach (var item in value.Items!)
        {
            ContentConverterFactory.WriteContent(writer, item);
        }

        writer.WriteEndArray();
    }
}

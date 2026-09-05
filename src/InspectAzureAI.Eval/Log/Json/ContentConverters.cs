using System.Text.Json;
using System.Text.Json.Serialization;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Log.Json;

/// <summary>
/// Port of the <c>Content</c> union of <c>_util/content.py</c> as JSON: <c>{"type": "text" | "reasoning" |
/// "image" | "audio" | "video" | "tool_use", ...}</c> with Python's field names.
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
            "text" => new ContentText(GetString(element, "text") ?? "") { Refusal = GetBool(element, "refusal"), Citations = ReadCitations(element) },
            "reasoning" => new ContentReasoning(GetString(element, "reasoning") ?? "", GetString(element, "signature"), GetBool(element, "redacted") ?? false),
            "image" => new ContentImage(GetString(element, "image") ?? "", GetString(element, "detail") ?? "auto"),
            "audio" => new ContentAudio(GetString(element, "audio") ?? "", GetString(element, "format") ?? ""),
            "video" => new ContentVideo(GetString(element, "video") ?? "", GetString(element, "format") ?? ""),
            "tool_use" => new ContentToolUse(
                GetString(element, "tool_type") ?? throw new JsonException("A tool_use content item requires a 'tool_type'."),
                GetString(element, "id") ?? throw new JsonException("A tool_use content item requires an 'id'."),
                GetString(element, "name") ?? throw new JsonException("A tool_use content item requires a 'name'."),
                GetString(element, "arguments") ?? throw new JsonException("A tool_use content item requires 'arguments'."),
                GetString(element, "result") ?? throw new JsonException("A tool_use content item requires a 'result'."))
            { Context = GetString(element, "context"), Error = GetString(element, "error") },
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

                if (text.Citations is { } citations)
                {
                    WriteCitations(writer, citations);
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
            case ContentToolUse toolUse:
                writer.WriteString("tool_type", toolUse.ToolType);
                writer.WriteString("id", toolUse.Id);
                writer.WriteString("name", toolUse.Name);
                if (toolUse.Context is { } context)
                {
                    writer.WriteString("context", context);
                }

                writer.WriteString("arguments", toolUse.Arguments);
                writer.WriteString("result", toolUse.Result);
                if (toolUse.Error is { } error)
                {
                    writer.WriteString("error", error);
                }

                break;
            default:
                throw new JsonException($"Unsupported content {content.GetType().Name}.");
        }

        writer.WriteEndObject();
    }

    /// <summary>Port of the <c>Citation</c> union of <c>_util/citation.py</c>: <c>{"type": "content" | "document" | "url", cited_text, title, internal, ...}</c>.</summary>
    private static void WriteCitations(Utf8JsonWriter writer, IReadOnlyList<Citation> citations)
    {
        writer.WriteStartArray("citations");
        foreach (var citation in citations)
        {
            writer.WriteStartObject();
            writer.WriteString("type", citation.Type);
            if (citation.CitedText is { } citedText)
            {
                writer.WriteString("cited_text", citedText);
            }
            else if (citation.CitedRange is { } range)
            {
                writer.WriteStartArray("cited_text");
                writer.WriteNumberValue(range.Start);
                writer.WriteNumberValue(range.End);
                writer.WriteEndArray();
            }

            if (citation.Title is { } title)
            {
                writer.WriteString("title", title);
            }

            if (citation.Internal is { } internalPayload)
            {
                writer.WritePropertyName("internal");
                internalPayload.WriteTo(writer);
            }

            switch (citation)
            {
                case UrlCitation url:
                    writer.WriteString("url", url.Url);
                    break;
                case DocumentCitation { Range: { } documentRange }:
                    writer.WriteStartObject("range");
                    writer.WriteString("type", documentRange.Type);
                    writer.WriteNumber("start_index", documentRange.StartIndex);
                    writer.WriteNumber("end_index", documentRange.EndIndex);
                    writer.WriteEndObject();
                    break;
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static IReadOnlyList<Citation>? ReadCitations(JsonElement element)
    {
        if (!element.TryGetProperty("citations", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var citations = new List<Citation>();
        foreach (var item in array.EnumerateArray())
        {
            var type = GetString(item, "type");
            Citation citation = type switch
            {
                "url" => new UrlCitation(GetString(item, "url") ?? throw new JsonException("A url citation requires a 'url'.")),
                "content" => new ContentCitation(),
                "document" => new DocumentCitation { Range = ReadDocumentRange(item) },
                _ => throw new JsonException($"Unsupported citation type '{type}'."),
            };
            string? citedText = null;
            CitedRange? citedRange = null;
            if (item.TryGetProperty("cited_text", out var cited))
            {
                if (cited.ValueKind == JsonValueKind.String)
                {
                    citedText = cited.GetString();
                }
                else if (cited.ValueKind == JsonValueKind.Array && cited.GetArrayLength() == 2)
                {
                    citedRange = new CitedRange(cited[0].GetInt32(), cited[1].GetInt32());
                }
                else if (cited.ValueKind != JsonValueKind.Null)
                {
                    throw new JsonException("A citation's 'cited_text' must be a string or a [start, end] pair.");
                }
            }

            citations.Add(citation with
            {
                CitedText = citedText,
                CitedRange = citedRange,
                Title = GetString(item, "title"),
                Internal = item.TryGetProperty("internal", out var payload) && payload.ValueKind == JsonValueKind.Object
                    ? System.Text.Json.Nodes.JsonObject.Create(payload.Clone())   // the element's document is disposed once the converter returns
                    : null,
            });
        }

        return citations;
    }

    private static DocumentRange? ReadDocumentRange(JsonElement item) =>
        item.TryGetProperty("range", out var range) && range.ValueKind == JsonValueKind.Object
            ? new DocumentRange(GetString(range, "type") ?? "", range.GetProperty("start_index").GetInt32(), range.GetProperty("end_index").GetInt32())
            : null;

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

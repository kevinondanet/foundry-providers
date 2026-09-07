using System.Text.Json;
using System.Text.Json.Serialization;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Log.Json;

/// <summary>
/// Port of the <c>ChatMessage</c> union of <c>model/_chat_message.py</c> as JSON: <c>id</c>, <c>content</c>
/// (string or content list), <c>source</c>, <c>metadata</c>, <c>role</c>, then the role-specific fields
/// (<c>tool_calls</c>/<c>model</c>, <c>tool_call_id</c>/<c>function</c>/<c>error</c>).
/// </summary>
internal sealed class ChatMessageConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeof(ChatMessage).IsAssignableFrom(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert))!;

    private sealed class Converter<T> : JsonConverter<T> where T : ChatMessage
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var element = document.RootElement;
            var role = element.TryGetProperty("role", out var roleElement) ? roleElement.GetString() : null;
            var id = element.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
            var content = element.TryGetProperty("content", out var contentElement)
                ? contentElement.Deserialize<MessageContent>(options) ?? throw new JsonException("Message content cannot be null.")
                : throw new JsonException("A chat message requires 'content'.");
            var source = Get<string>(element, "source", options);
            var metadata = Get<Dictionary<string, object?>>(element, "metadata", options);

            ChatMessage message = role switch
            {
                "system" => new ChatMessageSystem { Id = id, Content = content, Source = source, Metadata = metadata },
                "user" => new ChatMessageUser
                {
                    Id = id,
                    Content = content,
                    Source = source,
                    Metadata = metadata,
                    ToolCallId = Get<List<string>>(element, "tool_call_id", options),
                },
                "assistant" => new ChatMessageAssistant
                {
                    Id = id,
                    Content = content,
                    Source = source,
                    Metadata = metadata,
                    ToolCalls = Get<List<ToolCall>>(element, "tool_calls", options),
                    Model = Get<string>(element, "model", options),
                },
                "tool" => new ChatMessageTool
                {
                    Id = id,
                    Content = content,
                    Source = source,
                    Metadata = metadata,
                    ToolCallId = Get<string>(element, "tool_call_id", options),
                    Function = Get<string>(element, "function", options),
                    Error = Get<ToolCallError>(element, "error", options),
                },
                _ => throw new JsonException($"Unknown chat message role '{role}'."),
            };
            return message as T ?? throw new JsonException($"Chat message role '{role}' is not a {typeof(T).Name}.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            if (value.Id is { } id)
            {
                writer.WriteString("id", id);
            }

            writer.WritePropertyName("content");
            JsonSerializer.Serialize(writer, value.Content, options);
            if (value.Source is { } source)
            {
                writer.WriteString("source", source);
            }

            if (value.Metadata is { } metadata)
            {
                writer.WritePropertyName("metadata");
                JsonSerializer.Serialize(writer, metadata, options);
            }

            writer.WriteString("role", value.Role);
            switch (value)
            {
                case ChatMessageUser user when user.ToolCallId is { } ids:
                    writer.WritePropertyName("tool_call_id");
                    JsonSerializer.Serialize(writer, ids, options);
                    break;
                case ChatMessageAssistant assistant:
                    if (assistant.ToolCalls is { } calls)
                    {
                        writer.WritePropertyName("tool_calls");
                        JsonSerializer.Serialize(writer, calls, options);
                    }

                    if (assistant.Model is { } model)
                    {
                        writer.WriteString("model", model);
                    }

                    break;
                case ChatMessageTool tool:
                    if (tool.ToolCallId is { } callId)
                    {
                        writer.WriteString("tool_call_id", callId);
                    }

                    if (tool.Function is { } function)
                    {
                        writer.WriteString("function", function);
                    }

                    if (tool.Error is { } error)
                    {
                        writer.WritePropertyName("error");
                        JsonSerializer.Serialize(writer, error, options);
                    }

                    break;
            }

            writer.WriteEndObject();
        }

        private static TValue? Get<TValue>(JsonElement element, string name, JsonSerializerOptions options) where TValue : class =>
            element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.Deserialize<TValue>(options) : null;
    }
}

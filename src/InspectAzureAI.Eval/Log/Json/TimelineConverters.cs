using System.Text.Json;
using System.Text.Json.Serialization;

namespace InspectAzureAI.Eval.Log.Json;

/// <summary>
/// Port of the <c>TimelineContentItem</c> union as JSON: <c>{"type": "event", "event": "&lt;uuid&gt;"}</c> or a
/// <c>TimelineSpan</c> object in Python's field order with null fields omitted (<c>exclude_none</c>); an item
/// without <c>type</c> is an event, as Python's discriminator function defaults.
/// </summary>
internal sealed class TimelineNodeConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeof(TimelineNode).IsAssignableFrom(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert))!;

    private sealed class Converter<T> : JsonConverter<T> where T : TimelineNode
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var e = document.RootElement;
            var type = JsonIo.Str(e, "type") ?? "event";
            TimelineNode node = type switch
            {
                "event" => new TimelineEvent(JsonIo.Prop(e, "event") is { ValueKind: JsonValueKind.String } uuid
                    ? uuid.GetString()!
                    : throw new JsonException("A timeline event must reference an event uuid (this port does not accept inline event objects).")),
                "span" => new TimelineSpan(JsonIo.RequireStr(e, "id"), JsonIo.RequireStr(e, "name"))
                {
                    SpanType = JsonIo.Str(e, "span_type"),
                    Content = JsonIo.Get<List<TimelineNode>>(e, "content", options) ?? [],
                    Branches = JsonIo.Get<List<TimelineSpan>>(e, "branches", options) ?? [],
                    BranchedFrom = JsonIo.Str(e, "branched_from"),
                    Description = JsonIo.Str(e, "description"),
                    Utility = JsonIo.Bool(e, "utility") ?? false,
                    ToolInvoked = JsonIo.Bool(e, "tool_invoked") ?? false,
                    AgentResult = JsonIo.Str(e, "agent_result"),
                    Outline = JsonIo.Get<Outline>(e, "outline", options),
                },
                _ => throw new JsonException($"Unknown timeline item type '{type}'."),
            };
            return node as T ?? throw new JsonException($"Timeline item '{type}' is not a {typeof(T).Name}.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("type", value.Type);
            switch (value)
            {
                case TimelineEvent timelineEvent:
                    writer.WriteString("event", timelineEvent.Event);
                    break;
                case TimelineSpan span:
                    writer.WriteString("id", span.Id);
                    writer.WriteString("name", span.Name);
                    JsonIo.Str(writer, "span_type", span.SpanType);
                    JsonIo.Obj(writer, "content", span.Content, options, always: true);
                    JsonIo.Obj(writer, "branches", span.Branches, options, always: true);
                    JsonIo.Str(writer, "branched_from", span.BranchedFrom);
                    JsonIo.Str(writer, "description", span.Description);
                    writer.WriteBoolean("utility", span.Utility);
                    writer.WriteBoolean("tool_invoked", span.ToolInvoked);
                    JsonIo.Str(writer, "agent_result", span.AgentResult);
                    JsonIo.Obj(writer, "outline", span.Outline, options);
                    break;
                default:
                    throw new JsonException($"Unsupported timeline item {value.GetType().Name}.");
            }

            writer.WriteEndObject();
        }
    }
}

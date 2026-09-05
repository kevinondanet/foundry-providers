using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Log.Json;

/// <summary>
/// Port of the <c>DiscriminatedEvent</c> union of <c>event/_event.py</c> as JSON: every event carries
/// <c>event</c>, <c>timestamp</c> and <c>span_id</c> followed by the fields of its Python model. Written
/// by hand per event type so no reflection touches the abstract base or the computed members.
/// </summary>
internal sealed class TranscriptEventConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeof(TranscriptEvent).IsAssignableFrom(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert))!;

    private sealed class Converter<T> : JsonConverter<T> where T : TranscriptEvent
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var e = document.RootElement;
            var kind = Get<string>(e, "event", options);
            TranscriptEvent result = kind switch
            {
                "model" => new ModelEvent
                {
                    Model = Get<string>(e, "model", options) ?? "",
                    Input = Get<List<ChatMessage>>(e, "input", options) ?? [],
                    Tools = Get<List<ToolInfo>>(e, "tools", options) ?? [],
                    ToolChoice = Get<ToolChoice>(e, "tool_choice", options) ?? ToolChoice.Auto,
                    Config = Get<GenerateConfig>(e, "config", options) ?? new GenerateConfig(),
                    Output = Get<ModelOutput>(e, "output", options) ?? new ModelOutput(),
                    Call = Get<ModelCall>(e, "call", options),
                    Retries = GetInt(e, "retries"),
                    Error = Get<string>(e, "error", options),
                    Cache = Get<string>(e, "cache", options) switch
                    {
                        null => null,
                        "read" => CacheMode.Read,
                        "write" => CacheMode.Write,
                        var other => throw new JsonException($"Unknown model event cache mode '{other}'."),
                    },
                    Completed = Get<DateTimeOffset?>(e, "completed", options),
                    WorkingTime = GetDouble(e, "working_time"),
                },
                "tool" => new ToolEvent(
                    Get<string>(e, "id", options) ?? "",
                    Get<string>(e, "function", options) ?? "",
                    Get<JsonObject>(e, "arguments", options) ?? new JsonObject(),
                    Get<string>(e, "result", options),
                    Get<ToolCallError>(e, "error", options),
                    Get<List<int>>(e, "truncated", options) is [var raw, var shown] ? new ToolTruncation(raw, shown) : null,
                    GetDouble(e, "working_time") is { } seconds ? TimeSpan.FromSeconds(seconds) : null),
                "sandbox" => new SandboxEvent(
                    Get<string>(e, "action", options) ?? "",
                    Get<JsonObject>(e, "input", options) ?? new JsonObject(),
                    Get<JsonObject>(e, "result", options)),
                "score" => new ScoreEvent(
                    Get<Score>(e, "score", options) ?? throw new JsonException("A score event requires 'score'."),
                    Get<Target>(e, "target", options),
                    Get<bool?>(e, "intermediate", options) ?? false),
                "info" => new InfoEvent(Get<string>(e, "source", options), e.TryGetProperty("data", out var data) ? JsonNode.Parse(data.GetRawText()) : null),
                "error" => ReadError(e, options),
                "span_begin" => new SpanBeginEvent(
                    Get<string>(e, "id", options) ?? "",
                    Get<string>(e, "name", options) ?? "",
                    Get<string>(e, "type", options) ?? "span",
                    Get<string>(e, "parent_id", options)),
                "span_end" => new SpanEndEvent(Get<string>(e, "id", options) ?? ""),
                "step" => new StepEvent(
                    Get<string>(e, "name", options) ?? "",
                    Get<string>(e, "type", options) ?? "",
                    Get<string>(e, "action", options) ?? ""),
                _ => throw new JsonException($"Unknown transcript event '{kind}'."),
            };

            result = result with
            {
                Timestamp = Get<DateTimeOffset?>(e, "timestamp", options) ?? result.Timestamp,
                SpanId = Get<string>(e, "span_id", options),
            };
            return result as T ?? throw new JsonException($"Event '{kind}' is not a {typeof(T).Name}.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("event", value.Event);
            writer.WriteString("timestamp", value.Timestamp);
            if (value.SpanId is { } spanId)
            {
                writer.WriteString("span_id", spanId);
            }

            switch (value)
            {
                case ModelEvent model:
                    writer.WriteString("model", model.Model);
                    Put(writer, "input", model.Input, options);
                    Put(writer, "tools", model.Tools, options);
                    Put(writer, "tool_choice", model.ToolChoice, options);
                    Put(writer, "config", model.Config, options);
                    Put(writer, "output", model.Output, options);
                    Put(writer, "call", model.Call, options);
                    if (model.Retries is { } retries)
                    {
                        writer.WriteNumber("retries", retries);
                    }

                    if (model.Error is { } error)
                    {
                        writer.WriteString("error", error);
                    }

                    if (model.Cache is { } cache)
                    {
                        writer.WriteString("cache", cache.ToWire());
                    }

                    if (model.Completed is { } completed)
                    {
                        writer.WriteString("completed", completed);
                    }

                    if (model.WorkingTime is { } workingTime)
                    {
                        writer.WriteNumber("working_time", workingTime);
                    }

                    break;
                case ToolEvent tool:
                    writer.WriteString("id", tool.Id);
                    writer.WriteString("function", tool.Function);
                    writer.WritePropertyName("arguments");
                    tool.Arguments.WriteTo(writer);
                    if (tool.Result is { } result)
                    {
                        writer.WriteString("result", result);
                    }

                    Put(writer, "error", tool.Error, options);
                    if (tool.Truncated is { } truncated)
                    {
                        // Python stores the pair as a two-element list
                        writer.WritePropertyName("truncated");
                        writer.WriteStartArray();
                        writer.WriteNumberValue(truncated.Raw);
                        writer.WriteNumberValue(truncated.Shown);
                        writer.WriteEndArray();
                    }

                    if (tool.Working is { } working)
                    {
                        writer.WriteNumber("working_time", working.TotalSeconds);
                    }

                    break;
                case SandboxEvent sandbox:
                    writer.WriteString("action", sandbox.Action);
                    writer.WritePropertyName("input");
                    sandbox.Input.WriteTo(writer);
                    if (sandbox.Result is { } sandboxResult)
                    {
                        writer.WritePropertyName("result");
                        sandboxResult.WriteTo(writer);
                    }

                    break;
                case ScoreEvent score:
                    Put(writer, "score", score.Score, options);
                    Put(writer, "target", score.Target, options);
                    writer.WriteBoolean("intermediate", score.Intermediate);
                    break;
                case InfoEvent info:
                    if (info.Source is { } source)
                    {
                        writer.WriteString("source", source);
                    }

                    writer.WritePropertyName("data");
                    if (info.Data is { } data)
                    {
                        data.WriteTo(writer);
                    }
                    else
                    {
                        writer.WriteNullValue();
                    }

                    break;
                case ErrorEvent errorEvent:
                    // Python nests an EvalError under "error"
                    writer.WritePropertyName("error");
                    writer.WriteStartObject();
                    writer.WriteString("message", errorEvent.Message);
                    writer.WriteString("traceback", errorEvent.Traceback ?? "");
                    writer.WriteEndObject();
                    break;
                case SpanBeginEvent span:
                    writer.WriteString("id", span.Id);
                    if (span.ParentId is { } parentId)
                    {
                        writer.WriteString("parent_id", parentId);
                    }

                    writer.WriteString("type", span.Type);
                    writer.WriteString("name", span.Name);
                    break;
                case SpanEndEvent spanEnd:
                    writer.WriteString("id", spanEnd.Id);
                    break;
                case StepEvent step:
                    writer.WriteString("action", step.Action);
                    writer.WriteString("type", step.Type);
                    writer.WriteString("name", step.Name);
                    break;
                default:
                    throw new JsonException($"Unsupported transcript event {value.GetType().Name}.");
            }

            writer.WriteEndObject();
        }

        private static ErrorEvent ReadError(JsonElement e, JsonSerializerOptions options)
        {
            if (e.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var traceback = Get<string>(error, "traceback", options);
                return new ErrorEvent(Get<string>(error, "message", options) ?? "", string.IsNullOrEmpty(traceback) ? null : traceback);
            }

            return new ErrorEvent(Get<string>(e, "message", options) ?? "", Get<string>(e, "traceback", options));
        }

        private static void Put<TValue>(Utf8JsonWriter writer, string name, TValue? value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                return;
            }

            writer.WritePropertyName(name);
            JsonSerializer.Serialize(writer, value, options);
        }

        private static TValue? Get<TValue>(JsonElement element, string name, JsonSerializerOptions options) =>
            element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.Deserialize<TValue>(options) : default;

        private static int? GetInt(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

        private static double? GetDouble(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    }
}

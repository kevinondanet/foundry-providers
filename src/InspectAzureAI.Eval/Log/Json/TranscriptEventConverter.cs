using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Log.Json;

/// <summary>
/// Port of the <c>DiscriminatedEvent</c> union of <c>event/_event.py</c> as JSON. Every event starts with the
/// <c>BaseEvent</c> fields in Python's order (<c>uuid</c>, <c>span_id</c>, <c>timestamp</c>, <c>working_start</c>,
/// <c>metadata</c>, <c>pending</c>), then <c>event</c>, then the fields of its own model; a null field is
/// omitted (<c>exclude_none</c>). <c>CheckpointEvent</c> is the exception: its flattened <c>Checkpoint</c> payload
/// precedes the base fields, as pydantic orders inherited fields. Written by hand per event type so the format
/// never depends on reflection over the abstract base or the computed members.
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
            var kind = JsonIo.Str(e, "event");
            TranscriptEvent result = kind switch
            {
                "sample_init" => new SampleInitEvent(JsonIo.Require<Sample>(e, "sample", options), JsonIo.Node(e, "state")),
                "sample_limit" => new SampleLimitEvent(JsonIo.RequireStr(e, "type"), JsonIo.RequireStr(e, "message"), JsonIo.Dbl(e, "limit")),
                "sandbox" => new SandboxEvent(
                    JsonIo.RequireStr(e, "action"),
                    JsonIo.Str(e, "cmd"),
                    JsonIo.Object(e, "options"),
                    JsonIo.Str(e, "file"),
                    JsonIo.Str(e, "input"),
                    JsonIo.Int(e, "result"),
                    JsonIo.Str(e, "output"),
                    JsonIo.Time(e, "completed")),
                "state" => new StateEvent(JsonIo.Element(e, "changes") ?? throw new JsonException("A state event requires 'changes'.")),
                "store" => new StoreEvent(JsonIo.Element(e, "changes") ?? throw new JsonException("A store event requires 'changes'.")),
                "model" => ReadModel(e, options),
                "tool" => ReadTool(e, options),
                "anchor" => new AnchorEvent(JsonIo.RequireStr(e, "anchor_id")) { Source = JsonIo.Str(e, "source") },
                "approval" => new ApprovalEvent(
                    JsonIo.RequireStr(e, "message"),
                    JsonIo.Require<ToolCall>(e, "call", options),
                    JsonIo.RequireStr(e, "approver"),
                    JsonIo.RequireStr(e, "decision"))
                {
                    View = JsonIo.Get<ToolCallView>(e, "view", options),
                    Modified = JsonIo.Get<ToolCall>(e, "modified", options),
                    Explanation = JsonIo.Str(e, "explanation"),
                },
                "branch" => new BranchEvent(JsonIo.Str(e, "from_anchor") ?? JsonIo.Str(e, "from_message") ?? ""),
                "checkpoint" => ReadCheckpoint(e, options),
                "compaction" => new CompactionEvent
                {
                    Type = JsonIo.Str(e, "type") ?? "summary",
                    Role = JsonIo.Str(e, "role"),
                    TokensBefore = JsonIo.Int(e, "tokens_before"),
                    TokensAfter = JsonIo.Int(e, "tokens_after"),
                    Source = JsonIo.Str(e, "source"),
                },
                "input" => new InputEvent(JsonIo.RequireStr(e, "input"), JsonIo.RequireStr(e, "input_ansi"))
                {
                    Message = JsonIo.Str(e, "message"),
                    Fields = JsonIo.Get<List<InputField>>(e, "fields", options),
                    Outcome = JsonIo.Str(e, "outcome"),
                    Content = JsonIo.Get<Dictionary<string, object?>>(e, "content", options),
                },
                "interrupt" => new InterruptEvent(JsonIo.RequireStr(e, "source"), JsonIo.RequireStr(e, "interrupted"))
                {
                    InterruptedToolCallId = JsonIo.Str(e, "interrupted_tool_call_id"),
                    InterruptedModelEventId = JsonIo.Str(e, "interrupted_model_event_id"),
                },
                "score" => new ScoreEvent(
                    JsonIo.Require<Score>(e, "score", options),
                    JsonIo.Get<Target>(e, "target", options),
                    JsonIo.Bool(e, "intermediate") ?? false)
                {
                    Scorer = JsonIo.Str(e, "scorer"),
                    ScorerArgs = JsonIo.Get<Dictionary<string, object?>>(e, "scorer_args", options),
                    ModelUsage = JsonIo.Get<Dictionary<string, ModelUsage>>(e, "model_usage", options),
                    RoleUsage = JsonIo.Get<Dictionary<string, ModelUsage>>(e, "role_usage", options),
                },
                "score_edit" => new ScoreEditEvent(JsonIo.RequireStr(e, "score_name"), JsonIo.Require<ScoreEdit>(e, "edit", options)),
                "error" => ReadError(e),
                "logger" => new LoggerEvent(JsonIo.Require<LoggingMessage>(e, "message", options)),
                "info" => new InfoEvent(JsonIo.Str(e, "source"), JsonIo.Node(e, "data")),
                "span_begin" => new SpanBeginEvent(
                    JsonIo.RequireStr(e, "id"),
                    JsonIo.RequireStr(e, "name"),
                    JsonIo.Str(e, "type") ?? "span",
                    JsonIo.Str(e, "parent_id")),
                "span_end" => new SpanEndEvent(JsonIo.RequireStr(e, "id")),
                "step" => new StepEvent(JsonIo.RequireStr(e, "name"), JsonIo.Str(e, "type") ?? "", JsonIo.RequireStr(e, "action")),
                "subtask" => new SubtaskEvent(JsonIo.RequireStr(e, "name"), JsonIo.Prop(e, "input") is { ValueKind: JsonValueKind.Object } input ? (JsonObject)JsonNode.Parse(input.GetRawText())! : new JsonObject())
                {
                    Type = JsonIo.Str(e, "type"),
                    Result = JsonIo.Node(e, "result"),
                    Events = JsonIo.Get<List<TranscriptEvent>>(e, "events", options) ?? [],
                    Completed = JsonIo.Time(e, "completed"),
                    WorkingTime = JsonIo.Dbl(e, "working_time"),
                },
                _ => throw new JsonException($"Unknown transcript event '{kind}'."),
            };

            result = result with
            {
                Uuid = JsonIo.Str(e, "uuid"),
                SpanId = JsonIo.Str(e, "span_id"),
                Timestamp = JsonIo.Time(e, "timestamp") ?? result.Timestamp,
                WorkingStart = JsonIo.Dbl(e, "working_start") ?? 0,
                Metadata = JsonIo.Get<Dictionary<string, object?>>(e, "metadata", options),
                Pending = JsonIo.Bool(e, "pending"),
            };
            return result as T ?? throw new JsonException($"Event '{kind}' is not a {typeof(T).Name}.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            if (value is CheckpointEvent checkpoint)
            {
                WriteCheckpointPayload(writer, checkpoint, options);
            }

            JsonIo.Str(writer, "uuid", value.Uuid);
            JsonIo.Str(writer, "span_id", value.SpanId);
            writer.WriteString("timestamp", PythonJsonFormat.FormatIso(value.Timestamp));
            JsonIo.Dbl(writer, "working_start", value.WorkingStart);
            JsonIo.Obj(writer, "metadata", value.Metadata, options);
            JsonIo.Bool(writer, "pending", value.Pending);
            writer.WriteString("event", value.Event);

            switch (value)
            {
                case SampleInitEvent init:
                    JsonIo.Obj(writer, "sample", init.Sample, options, always: true);
                    JsonIo.Node(writer, "state", init.State);
                    break;
                case SampleLimitEvent limit:
                    writer.WriteString("type", limit.Type);
                    writer.WriteString("message", limit.Message);
                    JsonIo.Dbl(writer, "limit", limit.Limit);
                    break;
                case SandboxEvent sandbox:
                    writer.WriteString("action", sandbox.Action);
                    JsonIo.Str(writer, "cmd", sandbox.Cmd);
                    JsonIo.Node(writer, "options", sandbox.Options);
                    JsonIo.Str(writer, "file", sandbox.File);
                    JsonIo.Str(writer, "input", sandbox.Input);
                    JsonIo.Int(writer, "result", sandbox.Result);
                    JsonIo.Str(writer, "output", sandbox.Output);
                    JsonIo.Time(writer, "completed", sandbox.Completed);
                    break;
                case StateEvent state:
                    JsonIo.Element(writer, "changes", state.Changes);
                    break;
                case StoreEvent store:
                    JsonIo.Element(writer, "changes", store.Changes);
                    break;
                case ModelEvent model:
                    WriteModel(writer, model, options);
                    break;
                case ToolEvent tool:
                    WriteTool(writer, tool, options);
                    break;
                case AnchorEvent anchor:
                    writer.WriteString("anchor_id", anchor.AnchorId);
                    JsonIo.Str(writer, "source", anchor.Source);
                    break;
                case ApprovalEvent approval:
                    writer.WriteString("message", approval.Message);
                    JsonIo.Obj(writer, "call", approval.Call, options, always: true);
                    JsonIo.Obj(writer, "view", approval.View, options);
                    writer.WriteString("approver", approval.Approver);
                    writer.WriteString("decision", approval.Decision);
                    JsonIo.Obj(writer, "modified", approval.Modified, options);
                    JsonIo.Str(writer, "explanation", approval.Explanation);
                    break;
                case BranchEvent branch:
                    writer.WriteString("from_anchor", branch.FromAnchor);
                    break;
                case CheckpointEvent:
                    break;
                case CompactionEvent compaction:
                    writer.WriteString("type", compaction.Type);
                    JsonIo.Str(writer, "role", compaction.Role);
                    JsonIo.Int(writer, "tokens_before", compaction.TokensBefore);
                    JsonIo.Int(writer, "tokens_after", compaction.TokensAfter);
                    JsonIo.Str(writer, "source", compaction.Source);
                    break;
                case InputEvent input:
                    writer.WriteString("input", input.Input);
                    writer.WriteString("input_ansi", input.InputAnsi);
                    JsonIo.Str(writer, "message", input.Message);
                    JsonIo.Obj(writer, "fields", input.Fields, options);
                    JsonIo.Str(writer, "outcome", input.Outcome);
                    JsonIo.Obj(writer, "content", input.Content, options);
                    break;
                case InterruptEvent interrupt:
                    writer.WriteString("source", interrupt.Source);
                    writer.WriteString("interrupted", interrupt.Interrupted);
                    JsonIo.Str(writer, "interrupted_tool_call_id", interrupt.InterruptedToolCallId);
                    JsonIo.Str(writer, "interrupted_model_event_id", interrupt.InterruptedModelEventId);
                    break;
                case ScoreEvent score:
                    JsonIo.Obj(writer, "score", score.Score, options, always: true);
                    JsonIo.Obj(writer, "target", score.Target, options);
                    writer.WriteBoolean("intermediate", score.Intermediate);
                    JsonIo.Str(writer, "scorer", score.Scorer);
                    JsonIo.Obj(writer, "scorer_args", score.ScorerArgs, options);
                    JsonIo.Obj(writer, "model_usage", score.ModelUsage, options);
                    JsonIo.Obj(writer, "role_usage", score.RoleUsage, options);
                    break;
                case ScoreEditEvent scoreEdit:
                    writer.WriteString("score_name", scoreEdit.ScoreName);
                    JsonIo.Obj(writer, "edit", scoreEdit.Edit, options, always: true);
                    break;
                case ErrorEvent error:
                    JsonIo.Obj(writer, "error", error.Error, options, always: true);
                    break;
                case LoggerEvent logger:
                    JsonIo.Obj(writer, "message", logger.Message, options, always: true);
                    break;
                case InfoEvent info:
                    JsonIo.Str(writer, "source", info.Source);
                    // always written: Python's reader requires `data`, and its own writer drops a None (unreadable by Python)
                    JsonIo.Node(writer, "data", info.Data, always: true);
                    break;
                case SpanBeginEvent span:
                    writer.WriteString("id", span.Id);
                    JsonIo.Str(writer, "parent_id", span.ParentId);
                    JsonIo.Str(writer, "type", span.Type);
                    writer.WriteString("name", span.Name);
                    break;
                case SpanEndEvent spanEnd:
                    writer.WriteString("id", spanEnd.Id);
                    break;
                case StepEvent step:
                    writer.WriteString("action", step.Action);
                    JsonIo.Str(writer, "type", string.IsNullOrEmpty(step.Type) ? null : step.Type);
                    writer.WriteString("name", step.Name);
                    break;
                case SubtaskEvent subtask:
                    writer.WriteString("name", subtask.Name);
                    JsonIo.Str(writer, "type", subtask.Type);
                    JsonIo.Node(writer, "input", subtask.Input, always: true);
                    JsonIo.Node(writer, "result", subtask.Result);
                    JsonIo.Obj(writer, "events", subtask.Events, options, always: true);
                    JsonIo.Time(writer, "completed", subtask.Completed);
                    JsonIo.Dbl(writer, "working_time", subtask.WorkingTime);
                    break;
                default:
                    throw new JsonException($"Unsupported transcript event {value.GetType().Name}.");
            }

            writer.WriteEndObject();
        }

        private static ModelEvent ReadModel(JsonElement e, JsonSerializerOptions options) => new()
        {
            Model = JsonIo.Str(e, "model") ?? "",
            Role = JsonIo.Str(e, "role"),
            Input = JsonIo.Get<List<ChatMessage>>(e, "input", options) ?? [],
            InputRefs = JsonIo.Get<List<MessageRange>>(e, "input_refs", options),
            Tools = JsonIo.Get<List<ToolInfo>>(e, "tools", options) ?? [],
            ToolChoice = JsonIo.Get<ToolChoice>(e, "tool_choice", options) ?? ToolChoice.Auto,
            Config = JsonIo.Get<GenerateConfig>(e, "config", options) ?? new GenerateConfig(),
            Output = JsonIo.Get<ModelOutput>(e, "output", options) ?? new ModelOutput(),
            Retries = JsonIo.Int(e, "retries"),
            Error = JsonIo.Str(e, "error"),
            Traceback = JsonIo.Str(e, "traceback"),
            TracebackAnsi = JsonIo.Str(e, "traceback_ansi"),
            Cache = JsonIo.Str(e, "cache") switch
            {
                null => null,
                "read" => CacheMode.Read,
                "write" => CacheMode.Write,
                var other => throw new JsonException($"Unknown model event cache mode '{other}'."),
            },
            Call = JsonIo.Get<ModelCall>(e, "call", options),
            Completed = JsonIo.Time(e, "completed"),
            WorkingTime = JsonIo.Dbl(e, "working_time"),
        };

        private static void WriteModel(Utf8JsonWriter writer, ModelEvent model, JsonSerializerOptions options)
        {
            writer.WriteString("model", model.Model);
            JsonIo.Str(writer, "role", model.Role);
            JsonIo.Obj(writer, "input", model.Input, options, always: true);
            JsonIo.Obj(writer, "input_refs", model.InputRefs, options);
            JsonIo.Obj(writer, "tools", model.Tools, options, always: true);
            JsonIo.Obj(writer, "tool_choice", model.ToolChoice, options, always: true);
            JsonIo.Obj(writer, "config", model.Config, options, always: true);
            JsonIo.Obj(writer, "output", model.Output, options, always: true);
            JsonIo.Int(writer, "retries", model.Retries);
            JsonIo.Str(writer, "error", model.Error);
            JsonIo.Str(writer, "traceback", model.Traceback);
            JsonIo.Str(writer, "traceback_ansi", model.TracebackAnsi);
            JsonIo.Str(writer, "cache", model.Cache?.ToWire());
            JsonIo.Obj(writer, "call", model.Call, options);
            JsonIo.Time(writer, "completed", model.Completed);
            JsonIo.Dbl(writer, "working_time", model.WorkingTime);
        }

        private static ToolEvent ReadTool(JsonElement e, JsonSerializerOptions options)
        {
            string? result = null;
            List<Content>? resultContent = null;
            if (JsonIo.Prop(e, "result") is { } resultElement)
            {
                switch (resultElement.ValueKind)
                {
                    case JsonValueKind.String:
                        result = resultElement.GetString();
                        break;
                    case JsonValueKind.Array:
                        resultContent = resultElement.Deserialize<List<Content>>(options);
                        break;
                    case JsonValueKind.Object:
                        resultContent = [resultElement.Deserialize<Content>(options) ?? throw new JsonException("A tool result content cannot be null.")];
                        break;
                    default:
                        // a numeric or boolean ToolResult is carried as its JSON text
                        result = resultElement.GetRawText();
                        break;
                }
            }

            return new ToolEvent(
                JsonIo.RequireStr(e, "id"),
                JsonIo.RequireStr(e, "function"),
                JsonIo.Object(e, "arguments") ?? new JsonObject(),
                result,
                JsonIo.Get<ToolCallError>(e, "error", options),
                JsonIo.Get<List<int>>(e, "truncated", options) is [var raw, var shown] ? new ToolTruncation(raw, shown) : null,
                JsonIo.Dbl(e, "working_time") is { } seconds ? TimeSpan.FromSeconds(seconds) : null)
            {
                Type = JsonIo.Str(e, "type") ?? "function",
                View = JsonIo.Get<ToolCallContent>(e, "view", options),
                ResultContent = resultContent,
                Events = JsonIo.Get<List<TranscriptEvent>>(e, "events", options) ?? [],
                Completed = JsonIo.Time(e, "completed"),
                Agent = JsonIo.Str(e, "agent"),
                AgentSpanId = JsonIo.Str(e, "agent_span_id"),
                Failed = JsonIo.Bool(e, "failed"),
                MessageId = JsonIo.Str(e, "message_id"),
            };
        }

        private static void WriteTool(Utf8JsonWriter writer, ToolEvent tool, JsonSerializerOptions options)
        {
            writer.WriteString("type", tool.Type);
            writer.WriteString("id", tool.Id);
            writer.WriteString("function", tool.Function);
            JsonIo.Node(writer, "arguments", tool.Arguments, always: true);
            JsonIo.Obj(writer, "view", tool.View, options);
            if (tool.ResultContent is { } content)
            {
                JsonIo.Obj(writer, "result", content, options, always: true);
            }
            else
            {
                writer.WriteString("result", tool.Result ?? "");
            }

            if (tool.Truncated is { } truncated)
            {
                writer.WritePropertyName("truncated");
                writer.WriteStartArray();
                writer.WriteNumberValue(truncated.Raw);
                writer.WriteNumberValue(truncated.Shown);
                writer.WriteEndArray();
            }

            JsonIo.Obj(writer, "error", tool.Error, options);
            JsonIo.Obj(writer, "events", tool.Events, options, always: true);
            JsonIo.Time(writer, "completed", tool.Completed);
            JsonIo.Dbl(writer, "working_time", tool.WorkingTime);
            JsonIo.Str(writer, "agent", tool.Agent);
            JsonIo.Str(writer, "agent_span_id", tool.AgentSpanId);
            JsonIo.Bool(writer, "failed", tool.Failed);
            JsonIo.Str(writer, "message_id", tool.MessageId);
        }

        private static readonly HashSet<string> CheckpointKnownKeys = new(StringComparer.Ordinal)
        {
            "checkpoint_id", "trigger", "trigger_metadata", "turn", "created_at", "duration_ms", "size_bytes", "host", "sandboxes",
            "uuid", "span_id", "timestamp", "working_start", "metadata", "pending", "event",
        };

        private static CheckpointEvent ReadCheckpoint(JsonElement e, JsonSerializerOptions options)
        {
            JsonObject? extra = null;
            foreach (var property in e.EnumerateObject())
            {
                if (!CheckpointKnownKeys.Contains(property.Name))
                {
                    extra ??= new JsonObject();
                    extra[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                }
            }

            return new CheckpointEvent(
                JsonIo.Int(e, "checkpoint_id") ?? throw new JsonException("'checkpoint_id' is required."),
                JsonIo.RequireStr(e, "trigger"),
                JsonIo.Int(e, "turn") ?? throw new JsonException("'turn' is required."),
                JsonIo.Time(e, "created_at") ?? throw new JsonException("'created_at' is required."),
                JsonIo.Int(e, "duration_ms") ?? throw new JsonException("'duration_ms' is required."),
                JsonIo.Long(e, "size_bytes") ?? throw new JsonException("'size_bytes' is required."),
                JsonIo.Require<SnapshotDetails>(e, "host", options))
            {
                TriggerMetadata = JsonIo.Object(e, "trigger_metadata"),
                Sandboxes = JsonIo.Get<Dictionary<string, SnapshotDetails>>(e, "sandboxes", options) ?? new Dictionary<string, SnapshotDetails>(StringComparer.Ordinal),
                Extra = extra,
            };
        }

        private static void WriteCheckpointPayload(Utf8JsonWriter writer, CheckpointEvent checkpoint, JsonSerializerOptions options)
        {
            writer.WriteNumber("checkpoint_id", checkpoint.CheckpointId);
            writer.WriteString("trigger", checkpoint.Trigger);
            JsonIo.Node(writer, "trigger_metadata", checkpoint.TriggerMetadata);
            writer.WriteNumber("turn", checkpoint.Turn);
            JsonIo.TimeZ(writer, "created_at", checkpoint.CreatedAt);
            writer.WriteNumber("duration_ms", checkpoint.DurationMs);
            writer.WriteNumber("size_bytes", checkpoint.SizeBytes);
            JsonIo.Obj(writer, "host", checkpoint.Host, options, always: true);
            JsonIo.Obj(writer, "sandboxes", checkpoint.Sandboxes, options, always: true);
            if (checkpoint.Extra is { } extra)
            {
                foreach (var pair in extra)
                {
                    JsonIo.Node(writer, pair.Key, pair.Value, always: true);
                }
            }
        }

        private static ErrorEvent ReadError(JsonElement e)
        {
            if (JsonIo.Prop(e, "error") is { ValueKind: JsonValueKind.Object } error)
            {
                var traceback = JsonIo.Str(error, "traceback");
                var ansi = JsonIo.Str(error, "traceback_ansi");
                return new ErrorEvent(JsonIo.Str(error, "message") ?? "", string.IsNullOrEmpty(traceback) ? null : traceback)
                {
                    TracebackAnsi = string.IsNullOrEmpty(ansi) ? null : ansi,
                };
            }

            return new ErrorEvent(JsonIo.Str(e, "message") ?? "", JsonIo.Str(e, "traceback"));
        }
    }
}

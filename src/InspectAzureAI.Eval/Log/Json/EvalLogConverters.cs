using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Log.Json;

/// <summary>
/// Port of <c>EvalSpec</c> as JSON, hand-written for Python's field order, the <c>task_version: int | str</c> and
/// <c>model_roles</c> unions, and <c>migrate_values</c> (the legacy <c>[type, config]</c> sandbox form and the
/// <c>*_args_passed</c> defaults).
/// </summary>
internal sealed class EvalSpecConverter : JsonConverter<EvalSpec>
{
    public override EvalSpec Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var e = document.RootElement;
        var taskArgs = JsonIo.Get<Dictionary<string, object?>>(e, "task_args", options) ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        var solverArgs = JsonIo.Get<Dictionary<string, object?>>(e, "solver_args", options);
        return new EvalSpec
        {
            EvalSetId = JsonIo.Str(e, "eval_set_id"),
            EvalId = JsonIo.Str(e, "eval_id") is { Length: > 0 } evalId ? evalId : Provider.Util.ShortUuid.Generate(),
            RunId = JsonIo.Str(e, "run_id") ?? "",
            Created = JsonIo.Time(e, "created") ?? throw new JsonException("'created' is required."),
            Task = JsonIo.RequireStr(e, "task"),
            TaskId = JsonIo.Str(e, "task_id") ?? "",
            TaskVersion = JsonIo.Prop(e, "task_version") is { } version ? version.Deserialize<string>(TaskVersionOptions) ?? "0" : "0",
            TaskFile = JsonIo.Str(e, "task_file"),
            TaskDisplayName = JsonIo.Str(e, "task_display_name"),
            TaskRegistryName = JsonIo.Str(e, "task_registry_name"),
            TaskAttribs = JsonIo.Get<Dictionary<string, object?>>(e, "task_attribs", options) ?? new Dictionary<string, object?>(StringComparer.Ordinal),
            TaskArgs = taskArgs,
            TaskArgsPassed = JsonIo.Get<Dictionary<string, object?>>(e, "task_args_passed", options) ?? taskArgs,
            Solver = JsonIo.Str(e, "solver"),
            SolverArgs = solverArgs,
            SolverArgsPassed = JsonIo.Get<Dictionary<string, object?>>(e, "solver_args_passed", options) ?? solverArgs,
            Tags = JsonIo.Get<List<string>>(e, "tags", options),
            Dataset = JsonIo.Require<EvalDataset>(e, "dataset", options),
            Sandbox = JsonIo.Get<SandboxSpec>(e, "sandbox", options),
            Model = JsonIo.RequireStr(e, "model"),
            ModelGenerateConfig = JsonIo.Get<GenerateConfig>(e, "model_generate_config", options) ?? new GenerateConfig(),
            ModelBaseUrl = JsonIo.Str(e, "model_base_url"),
            ModelArgs = JsonIo.Get<Dictionary<string, object?>>(e, "model_args", options) ?? new Dictionary<string, object?>(StringComparer.Ordinal),
            ModelRoles = ReadModelRoles(e, options),
            Config = JsonIo.Get<EvalConfig>(e, "config", options) ?? new EvalConfig(),
            Revision = JsonIo.Get<EvalRevision>(e, "revision", options),
            Packages = JsonIo.Get<Dictionary<string, string>>(e, "packages", options) ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Metadata = JsonIo.Get<Dictionary<string, object?>>(e, "metadata", options),
            Viewer = JsonIo.Object(e, "viewer"),
            Scorers = JsonIo.Get<List<EvalScorer>>(e, "scorers", options),
            Metrics = JsonIo.Node(e, "metrics"),
            HeadlineMetric = JsonIo.Get<HeadlineMetric>(e, "headline_metric", options),
        };
    }

    public override void Write(Utf8JsonWriter writer, EvalSpec value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        JsonIo.Str(writer, "eval_set_id", value.EvalSetId);
        writer.WriteString("eval_id", value.EvalId);
        writer.WriteString("run_id", value.RunId);
        writer.WriteString("created", PythonJsonFormat.FormatIso(value.Created));
        writer.WriteString("task", value.Task);
        writer.WriteString("task_id", value.TaskId);
        writer.WritePropertyName("task_version");
        JsonSerializer.Serialize(writer, value.TaskVersion, TaskVersionOptions);
        JsonIo.Str(writer, "task_file", value.TaskFile);
        JsonIo.Str(writer, "task_display_name", value.TaskDisplayName);
        JsonIo.Str(writer, "task_registry_name", value.TaskRegistryName);
        JsonIo.Obj(writer, "task_attribs", value.TaskAttribs, options, always: true);
        JsonIo.Obj(writer, "task_args", value.TaskArgs, options, always: true);
        JsonIo.Obj(writer, "task_args_passed", value.TaskArgsPassed ?? value.TaskArgs, options, always: true);
        JsonIo.Str(writer, "solver", value.Solver);
        JsonIo.Obj(writer, "solver_args", value.SolverArgs, options);
        JsonIo.Obj(writer, "solver_args_passed", value.SolverArgsPassed ?? value.SolverArgs, options);
        JsonIo.Obj(writer, "tags", value.Tags, options);
        JsonIo.Obj(writer, "dataset", value.Dataset, options, always: true);
        JsonIo.Obj(writer, "sandbox", value.Sandbox, options);
        writer.WriteString("model", value.Model);
        JsonIo.Obj(writer, "model_generate_config", value.ModelGenerateConfig, options, always: true);
        JsonIo.Str(writer, "model_base_url", value.ModelBaseUrl);
        JsonIo.Obj(writer, "model_args", value.ModelArgs, options, always: true);
        WriteModelRoles(writer, value.ModelRoles, options);
        JsonIo.Obj(writer, "config", value.Config, options, always: true);
        JsonIo.Obj(writer, "revision", value.Revision, options);
        JsonIo.Obj(writer, "packages", value.Packages, options, always: true);
        JsonIo.Obj(writer, "metadata", value.Metadata, options);
        JsonIo.Node(writer, "viewer", value.Viewer);
        JsonIo.Obj(writer, "scorers", value.Scorers, options);
        JsonIo.Node(writer, "metrics", value.Metrics);
        JsonIo.Obj(writer, "headline_metric", value.HeadlineMetric, options);
        writer.WriteEndObject();
    }

    private static readonly JsonSerializerOptions TaskVersionOptions = new() { Converters = { new IntOrStringConverter() } };

    private static Dictionary<string, IReadOnlyList<ModelConfig>>? ReadModelRoles(JsonElement e, JsonSerializerOptions options)
    {
        if (JsonIo.Prop(e, "model_roles") is not { } roles)
        {
            return null;
        }

        var result = new Dictionary<string, IReadOnlyList<ModelConfig>>(StringComparer.Ordinal);
        foreach (var role in roles.EnumerateObject())
        {
            result[role.Name] = role.Value.ValueKind == JsonValueKind.Array
                ? role.Value.Deserialize<List<ModelConfig>>(options) ?? []
                : [role.Value.Deserialize<ModelConfig>(options) ?? throw new JsonException($"Model role '{role.Name}' is invalid.")];
        }

        return result;
    }

    private static void WriteModelRoles(Utf8JsonWriter writer, IReadOnlyDictionary<string, IReadOnlyList<ModelConfig>>? roles, JsonSerializerOptions options)
    {
        if (roles is null)
        {
            return;
        }

        writer.WritePropertyName("model_roles");
        writer.WriteStartObject();
        foreach (var pair in roles)
        {
            writer.WritePropertyName(pair.Key);
            if (pair.Value.Count == 1)
            {
                JsonSerializer.Serialize(writer, pair.Value[0], options);
            }
            else
            {
                JsonSerializer.Serialize(writer, pair.Value, options);
            }
        }

        writer.WriteEndObject();
    }
}

/// <summary>Port of <c>EvalConfig</c> as JSON, hand-written for Python's field order and its union-typed options.</summary>
internal sealed class EvalConfigConverter : JsonConverter<EvalConfig>
{
    public override EvalConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var e = document.RootElement;
        var limit = JsonIo.Prop(e, "limit");
        var shuffle = JsonIo.Prop(e, "sample_shuffle");
        var failOnError = JsonIo.Prop(e, "fail_on_error");
        var sampleId = JsonIo.Prop(e, "sample_id");
        return new EvalConfig
        {
            Limit = limit is { ValueKind: JsonValueKind.Number } limitNumber ? limitNumber.GetInt32() : null,
            LimitRange = limit is { ValueKind: JsonValueKind.Array } limitRange && limitRange.GetArrayLength() == 2
                ? new SampleRange(limitRange[0].GetInt32(), limitRange[1].GetInt32())
                : null,
            SampleId = sampleId is { } ids
                ? ids.ValueKind == JsonValueKind.Array ? ids.Deserialize<List<object>>(options) : [PlainJson.ToObject(ids) ?? throw new JsonException("'sample_id' cannot be null.")]
                : null,
            SampleShuffle = shuffle is { ValueKind: JsonValueKind.True or JsonValueKind.False } shuffleFlag ? shuffleFlag.GetBoolean() : null,
            SampleShuffleSeed = shuffle is { ValueKind: JsonValueKind.Number } shuffleSeed ? shuffleSeed.GetInt32() : null,
            Epochs = JsonIo.Int(e, "epochs"),
            EpochsReducer = JsonIo.Get<List<string>>(e, "epochs_reducer", options),
            Approval = JsonIo.Object(e, "approval"),
            Notification = JsonIo.Prop(e, "notification") is { } notification ? PlainJson.ToObject(notification) : null,
            FailOnError = failOnError is { ValueKind: JsonValueKind.True or JsonValueKind.False } failFlag ? failFlag.GetBoolean() : null,
            FailOnErrorThreshold = failOnError is { ValueKind: JsonValueKind.Number } failThreshold ? failThreshold.GetDouble() : null,
            ContinueOnFail = JsonIo.Bool(e, "continue_on_fail"),
            RetryOnError = JsonIo.Int(e, "retry_on_error"),
            ScoreOnError = JsonIo.Bool(e, "score_on_error"),
            // Python's convert_max_messages_to_message_limit validator: a truthy legacy max_messages wins
            MessageLimit = JsonIo.Int(e, "max_messages") is { } maxMessages and not 0 ? maxMessages : JsonIo.Int(e, "message_limit"),
            TokenLimit = JsonIo.Int(e, "token_limit"),
            TokenLimitType = JsonIo.Str(e, "token_limit_type"),
            TurnLimit = JsonIo.Int(e, "turn_limit"),
            TimeLimit = JsonIo.Int(e, "time_limit"),
            WorkingLimit = JsonIo.Int(e, "working_limit"),
            CostLimit = JsonIo.Dbl(e, "cost_limit"),
            MaxSamples = JsonIo.Int(e, "max_samples"),
            MaxDatasetMemory = JsonIo.Int(e, "max_dataset_memory"),
            MaxTasks = JsonIo.Int(e, "max_tasks"),
            MaxSubprocesses = JsonIo.Int(e, "max_subprocesses"),
            MaxSandboxes = JsonIo.Int(e, "max_sandboxes"),
            SandboxCleanup = JsonIo.Bool(e, "sandbox_cleanup"),
            SandboxPrebuilt = JsonIo.Bool(e, "sandbox_prebuilt"),
            LogSamples = JsonIo.Bool(e, "log_samples"),
            LogRealtime = JsonIo.Bool(e, "log_realtime"),
            LogImages = JsonIo.Bool(e, "log_images"),
            LogModelApi = JsonIo.Bool(e, "log_model_api"),
            LogBuffer = JsonIo.Int(e, "log_buffer"),
            LogShared = JsonIo.Int(e, "log_shared"),
            ScoreDisplay = JsonIo.Bool(e, "score_display"),
            AcpServer = JsonIo.Prop(e, "acp_server") is { } acp ? PlainJson.ToObject(acp) : null,
        };
    }

    public override void Write(Utf8JsonWriter writer, EvalConfig value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.LimitRange is { } range)
        {
            writer.WritePropertyName("limit");
            writer.WriteStartArray();
            writer.WriteNumberValue(range.Start);
            writer.WriteNumberValue(range.End);
            writer.WriteEndArray();
        }
        else
        {
            JsonIo.Int(writer, "limit", value.Limit);
        }

        JsonIo.Obj(writer, "sample_id", value.SampleId, options);
        if (value.SampleShuffleSeed is { } seed)
        {
            writer.WriteNumber("sample_shuffle", seed);
        }
        else
        {
            JsonIo.Bool(writer, "sample_shuffle", value.SampleShuffle);
        }

        JsonIo.Int(writer, "epochs", value.Epochs);
        JsonIo.Obj(writer, "epochs_reducer", value.EpochsReducer, options);
        JsonIo.Node(writer, "approval", value.Approval);
        JsonIo.Obj(writer, "notification", value.Notification, options);
        if (value.FailOnErrorThreshold is { } threshold)
        {
            JsonIo.Dbl(writer, "fail_on_error", threshold);
        }
        else
        {
            JsonIo.Bool(writer, "fail_on_error", value.FailOnError);
        }

        JsonIo.Bool(writer, "continue_on_fail", value.ContinueOnFail);
        JsonIo.Int(writer, "retry_on_error", value.RetryOnError);
        JsonIo.Bool(writer, "score_on_error", value.ScoreOnError);
        JsonIo.Int(writer, "message_limit", value.MessageLimit);
        JsonIo.Int(writer, "token_limit", value.TokenLimit);
        JsonIo.Str(writer, "token_limit_type", value.TokenLimitType);
        JsonIo.Int(writer, "turn_limit", value.TurnLimit);
        JsonIo.Int(writer, "time_limit", value.TimeLimit);
        JsonIo.Int(writer, "working_limit", value.WorkingLimit);
        JsonIo.Dbl(writer, "cost_limit", value.CostLimit);
        JsonIo.Int(writer, "max_samples", value.MaxSamples);
        JsonIo.Int(writer, "max_dataset_memory", value.MaxDatasetMemory);
        JsonIo.Int(writer, "max_tasks", value.MaxTasks);
        JsonIo.Int(writer, "max_subprocesses", value.MaxSubprocesses);
        JsonIo.Int(writer, "max_sandboxes", value.MaxSandboxes);
        JsonIo.Bool(writer, "sandbox_cleanup", value.SandboxCleanup);
        JsonIo.Bool(writer, "sandbox_prebuilt", value.SandboxPrebuilt);
        JsonIo.Bool(writer, "log_samples", value.LogSamples);
        JsonIo.Bool(writer, "log_realtime", value.LogRealtime);
        JsonIo.Bool(writer, "log_images", value.LogImages);
        JsonIo.Bool(writer, "log_model_api", value.LogModelApi);
        JsonIo.Int(writer, "log_buffer", value.LogBuffer);
        JsonIo.Int(writer, "log_shared", value.LogShared);
        JsonIo.Bool(writer, "score_display", value.ScoreDisplay);
        JsonIo.Obj(writer, "acp_server", value.AcpServer, options);
        writer.WriteEndObject();
    }
}

/// <summary>
/// Port of <c>SandboxEnvironmentSpec</c> as JSON: <c>{"type", "config"}</c> plus the legacy <c>[type, config]</c>
/// list form. A structured (object) config is not representable by <see cref="SandboxSpec"/> and is rejected.
/// </summary>
internal sealed class SandboxSpecConverter : JsonConverter<SandboxSpec>
{
    public override SandboxSpec Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var e = document.RootElement;
        if (e.ValueKind == JsonValueKind.String)
        {
            return new SandboxSpec(e.GetString()!);
        }

        if (e.ValueKind == JsonValueKind.Array)
        {
            var items = e.EnumerateArray().ToList();
            return new SandboxSpec(items[0].GetString() ?? throw new JsonException("A sandbox spec requires a type."), items.Count > 1 ? ConfigText(items[1]) : null);
        }

        return new SandboxSpec(JsonIo.RequireStr(e, "type"), JsonIo.Prop(e, "config") is { } config ? ConfigText(config) : null);
    }

    public override void Write(Utf8JsonWriter writer, SandboxSpec value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value.Type);
        JsonIo.Str(writer, "config", value.Config);
        writer.WriteEndObject();
    }

    private static string? ConfigText(JsonElement config) => config.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => config.GetString(),
        _ => throw new JsonException("Structured sandbox configs (an object rather than a path) are not supported by this port."),
    };
}

/// <summary>Port of <c>dataset/_dataset.py</c> <c>Sample</c> as JSON in Python's field order (the payload of <c>SampleInitEvent</c>).</summary>
internal sealed class SampleConverter : JsonConverter<Sample>
{
    public override Sample Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var e = document.RootElement;
        return new Sample(JsonIo.Require<SampleInput>(e, "input", options))
        {
            Choices = JsonIo.Get<List<string>>(e, "choices", options),
            Target = JsonIo.Get<Target>(e, "target", options) ?? Target.Empty,
            Id = JsonIo.Prop(e, "id") is { } id ? PlainJson.ToObject(id) : null,
            Metadata = JsonIo.Get<Dictionary<string, object?>>(e, "metadata", options),
            Sandbox = JsonIo.Get<SandboxSpec>(e, "sandbox", options),
            Files = JsonIo.Get<Dictionary<string, string>>(e, "files", options),
            Setup = JsonIo.Str(e, "setup"),
        };
    }

    public override void Write(Utf8JsonWriter writer, Sample value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        JsonIo.Obj(writer, "input", value.Input, options, always: true);
        JsonIo.Obj(writer, "choices", value.Choices, options);
        JsonIo.Obj(writer, "target", value.Target, options, always: true);
        JsonIo.Obj(writer, "id", value.Id, options);
        JsonIo.Obj(writer, "metadata", value.Metadata, options);
        JsonIo.Obj(writer, "sandbox", value.Sandbox, options);
        JsonIo.Obj(writer, "files", value.Files, options);
        JsonIo.Str(writer, "setup", value.Setup);
        writer.WriteEndObject();
    }
}

/// <summary>Port of the <c>LogEditType</c> union: <c>TagsEdit</c> / <c>MetadataEdit</c> discriminated by <c>type</c>.</summary>
internal sealed class LogEditConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeof(LogEdit).IsAssignableFrom(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert))!;

    private sealed class Converter<T> : JsonConverter<T> where T : LogEdit
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var e = document.RootElement;
            var type = JsonIo.Str(e, "type");
            LogEdit edit = type switch
            {
                "tags" => new TagsEdit
                {
                    TagsAdd = JsonIo.Get<List<string>>(e, "tags_add", options) ?? [],
                    TagsRemove = JsonIo.Get<List<string>>(e, "tags_remove", options) ?? [],
                },
                "metadata" => new MetadataEdit
                {
                    MetadataSet = JsonIo.Get<Dictionary<string, object?>>(e, "metadata_set", options) ?? new Dictionary<string, object?>(StringComparer.Ordinal),
                    MetadataRemove = JsonIo.Get<List<string>>(e, "metadata_remove", options) ?? [],
                },
                _ => throw new JsonException($"Unknown log edit type '{type}'."),
            };
            return edit as T ?? throw new JsonException($"Log edit '{type}' is not a {typeof(T).Name}.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("type", value.Type);
            switch (value)
            {
                case TagsEdit tags:
                    JsonIo.Obj(writer, "tags_add", tags.TagsAdd, options, always: true);
                    JsonIo.Obj(writer, "tags_remove", tags.TagsRemove, options, always: true);
                    break;
                case MetadataEdit metadata:
                    JsonIo.Obj(writer, "metadata_set", metadata.MetadataSet, options, always: true);
                    JsonIo.Obj(writer, "metadata_remove", metadata.MetadataRemove, options, always: true);
                    break;
                default:
                    throw new JsonException($"Unsupported log edit {value.GetType().Name}.");
            }

            writer.WriteEndObject();
        }
    }
}

/// <summary>
/// Port of <c>ScoreEdit</c> as JSON: unchanged fields are the <c>"UNCHANGED"</c> sentinel, a cleared field is
/// omitted (Python's <c>exclude_none</c>, which reads back as unchanged), otherwise the value.
/// </summary>
internal sealed class ScoreEditConverter : JsonConverter<ScoreEdit>
{
    private const string Unchanged = "UNCHANGED";

    public override ScoreEdit Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var e = document.RootElement;
        return new ScoreEdit
        {
            Value = IsUnchanged(e, "value") ? Edited<ScoreValue>.Unchanged : Edited<ScoreValue>.Set(JsonIo.Get<ScoreValue>(e, "value", options)),
            Answer = IsUnchanged(e, "answer") ? Edited<string>.Unchanged : Edited<string>.Set(JsonIo.Str(e, "answer")),
            Explanation = IsUnchanged(e, "explanation") ? Edited<string>.Unchanged : Edited<string>.Set(JsonIo.Str(e, "explanation")),
            Reason = IsUnchanged(e, "reason") ? Edited<string>.Unchanged : Edited<string>.Set(JsonIo.Str(e, "reason")),
            Metadata = IsUnchanged(e, "metadata")
                ? Edited<IReadOnlyDictionary<string, object?>>.Unchanged
                : Edited<IReadOnlyDictionary<string, object?>>.Set(JsonIo.Get<Dictionary<string, object?>>(e, "metadata", options)),
            Provenance = JsonIo.Get<ProvenanceData>(e, "provenance", options),
        };
    }

    public override void Write(Utf8JsonWriter writer, ScoreEdit value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        WriteField(writer, "value", value.Value, options);
        WriteField(writer, "answer", value.Answer, options);
        WriteField(writer, "explanation", value.Explanation, options);
        WriteField(writer, "reason", value.Reason, options);
        WriteField(writer, "metadata", value.Metadata, options);
        JsonIo.Obj(writer, "provenance", value.Provenance, options);
        writer.WriteEndObject();
    }

    private static bool IsUnchanged(JsonElement e, string name) =>
        JsonIo.Prop(e, name) is not { } value || (value.ValueKind == JsonValueKind.String && value.GetString() == Unchanged);

    private static void WriteField<T>(Utf8JsonWriter writer, string name, Edited<T> field, JsonSerializerOptions options)
    {
        if (!field.IsSet)
        {
            writer.WriteString(name, Unchanged);
        }
        else if (field.Value is not null)
        {
            writer.WritePropertyName(name);
            JsonSerializer.Serialize(writer, field.Value, options);
        }
    }
}

/// <summary>Port of <c>LoggingMessage</c> as JSON, including the legacy level migration (<c>tools</c> / <c>sandbox</c> read as <c>trace</c>).</summary>
internal sealed class LoggingMessageConverter : JsonConverter<LoggingMessage>
{
    public override LoggingMessage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var e = document.RootElement;
        var level = JsonIo.RequireStr(e, "level");
        return new LoggingMessage(level is "tools" or "sandbox" ? "trace" : level, JsonIo.RequireStr(e, "message"), JsonIo.Dbl(e, "created") ?? throw new JsonException("'created' is required."))
        {
            Name = JsonIo.Str(e, "name"),
            Filename = JsonIo.Str(e, "filename") ?? "unknown",
            Module = JsonIo.Str(e, "module") ?? "unknown",
            Lineno = JsonIo.Int(e, "lineno") ?? 0,
        };
    }

    public override void Write(Utf8JsonWriter writer, LoggingMessage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        JsonIo.Str(writer, "name", value.Name);
        writer.WriteString("level", value.Level);
        writer.WriteString("message", value.Message);
        JsonIo.Dbl(writer, "created", value.Created);
        writer.WriteString("filename", value.Filename);
        writer.WriteString("module", value.Module);
        writer.WriteNumber("lineno", value.Lineno);
        writer.WriteEndObject();
    }
}

/// <summary>Port of <c>EvalSampleScore</c> as JSON: the <see cref="Score"/> fields followed by <c>sample_id</c>.</summary>
internal sealed class EvalSampleScoreConverter : JsonConverter<EvalSampleScore>
{
    public override EvalSampleScore Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var e = document.RootElement;
        return new EvalSampleScore(e.Deserialize<Score>(options) ?? throw new JsonException("A sample score cannot be null."))
        {
            SampleId = JsonIo.Prop(e, "sample_id") is { } id ? PlainJson.ToObject(id) : null,
        };
    }

    public override void Write(Utf8JsonWriter writer, EvalSampleScore value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        ScoreConverter.WriteFields(writer, value.Score, options);
        JsonIo.Obj(writer, "sample_id", value.SampleId, options);
        writer.WriteEndObject();
    }
}

/// <summary>Port of <c>EvalPlanStep</c> as JSON: <c>params_passed</c> is always written (Python's <c>read_params</c> defaults it to <c>params</c>).</summary>
internal sealed class EvalPlanStepConverter : JsonConverter<EvalPlanStep>
{
    public override EvalPlanStep Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var e = document.RootElement;
        var parameters = JsonIo.Get<Dictionary<string, object?>>(e, "params", options) ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        return new EvalPlanStep(JsonIo.RequireStr(e, "solver"))
        {
            Params = parameters,
            ParamsPassed = JsonIo.Get<Dictionary<string, object?>>(e, "params_passed", options) ?? parameters,
        };
    }

    public override void Write(Utf8JsonWriter writer, EvalPlanStep value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("solver", value.Solver);
        JsonIo.Obj(writer, "params", value.Params, options, always: true);
        JsonIo.Obj(writer, "params_passed", value.ParamsPassed ?? value.Params, options, always: true);
        writer.WriteEndObject();
    }
}

/// <summary>
/// Port of <c>ToolInfo</c> / <c>ToolParams</c> / <c>ToolParam</c> as JSON: written with <see cref="ToolInfo.ToJson"/>
/// (Python's <c>model_dump(exclude_none=True)</c>, a scalar <c>type</c> for single-typed parameters) and read
/// back accepting both the scalar and list <c>type</c> forms.
/// </summary>
internal sealed class ToolInfoConverter : JsonConverter<ToolInfo>
{
    public override ToolInfo Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var e = document.RootElement;
        var parameters = JsonIo.Prop(e, "parameters") is { } schema ? ReadParams(schema) : new ToolParams();
        return new ToolInfo(JsonIo.RequireStr(e, "name"), JsonIo.Str(e, "description") ?? "")
        {
            Parameters = parameters,
            Options = JsonIo.Object(e, "options"),
        };
    }

    public override void Write(Utf8JsonWriter writer, ToolInfo value, JsonSerializerOptions options) =>
        PythonJsonFormat.WriteNode(writer, value.ToJson());

    private static ToolParams ReadParams(JsonElement e) => new()
    {
        Properties = ReadProperties(e) ?? new Dictionary<string, ToolParam>(StringComparer.Ordinal),
        Required = JsonIo.Get<List<string>>(e, "required", PlainOptions) ?? [],
        AdditionalProperties = ReadAdditionalProperties(e),
    };

    private static readonly JsonSerializerOptions PlainOptions = new();

    public static ToolParam ReadParam(JsonElement e) => new()
    {
        Type = JsonIo.Prop(e, "type") is { } type
            ? type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(item => item.GetString() ?? "").ToList() : [type.GetString() ?? ""]
            : null,
        Format = JsonIo.Str(e, "format"),
        Description = JsonIo.Str(e, "description"),
        Default = JsonIo.Node(e, "default"),
        Enum = JsonIo.Prop(e, "enum") is { ValueKind: JsonValueKind.Array } values ? values.EnumerateArray().Select(item => JsonNode.Parse(item.GetRawText())).ToList() : null,
        Items = JsonIo.Prop(e, "items") is { ValueKind: JsonValueKind.Object } items ? ReadParam(items) : null,
        Properties = ReadProperties(e),
        AdditionalProperties = ReadAdditionalProperties(e),
        AnyOf = JsonIo.Prop(e, "anyOf") is { ValueKind: JsonValueKind.Array } anyOf ? anyOf.EnumerateArray().Select(ReadParam).ToList() : null,
        Required = JsonIo.Get<List<string>>(e, "required", PlainOptions),
        Pattern = JsonIo.Str(e, "pattern"),
        MinLength = JsonIo.Int(e, "minLength"),
        MaxLength = JsonIo.Int(e, "maxLength"),
        Minimum = JsonIo.Dbl(e, "minimum"),
        Maximum = JsonIo.Dbl(e, "maximum"),
        Examples = JsonIo.Prop(e, "examples") is { ValueKind: JsonValueKind.Array } examples ? examples.EnumerateArray().Select(item => JsonNode.Parse(item.GetRawText())).ToList() : null,
    };

    private static Dictionary<string, ToolParam>? ReadProperties(JsonElement e) =>
        JsonIo.Prop(e, "properties") is { ValueKind: JsonValueKind.Object } properties
            ? properties.EnumerateObject().ToDictionary(property => property.Name, property => ReadParam(property.Value), StringComparer.Ordinal)
            : null;

    private static object? ReadAdditionalProperties(JsonElement e) => JsonIo.Prop(e, "additionalProperties") switch
    {
        null => null,
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.Object } schema => ReadParam(schema),
        _ => throw new JsonException("'additionalProperties' must be a boolean or a schema."),
    };
}

/// <summary>Port of the <c>(start, end_exclusive)</c> tuples of <c>ModelEvent.input_refs</c>.</summary>
internal sealed class MessageRangeConverter : JsonConverter<MessageRange>
{
    public override MessageRange Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var pair = JsonSerializer.Deserialize<int[]>(ref reader, options);
        return pair is [var start, var end] ? new MessageRange(start, end) : throw new JsonException("A message range is a two-element array.");
    }

    public override void Write(Utf8JsonWriter writer, MessageRange value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.Start);
        writer.WriteNumberValue(value.End);
        writer.WriteEndArray();
    }
}

/// <summary>Port of the <c>(start, end_exclusive)</c> tuples of <c>ModelCall.call_refs</c>.</summary>
internal sealed class CallRefConverter : JsonConverter<CallRef>
{
    public override CallRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var pair = JsonSerializer.Deserialize<int[]>(ref reader, options);
        return pair is [var start, var end] ? new CallRef(start, end) : throw new JsonException("A call ref is a two-element array.");
    }

    public override void Write(Utf8JsonWriter writer, CallRef value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.Start);
        writer.WriteNumberValue(value.End);
        writer.WriteEndArray();
    }
}

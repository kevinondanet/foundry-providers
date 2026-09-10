using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Runner.EvalSet;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>_eval/evalset.py</c> <c>EvalSetArgsInTaskIdentifier</c>: the eval-set level arguments that take part
/// in a task's identity. <see cref="Config"/> is the eval-level generate config (in this port the config of the
/// eval model, over which the runner layers the task's); a null limit keeps the task's own value.
/// </summary>
public sealed record EvalSetArgsInTaskIdentifier
{
    public GenerateConfig Config { get; init; } = new();

    public int? MessageLimit { get; init; }

    public int? TokenLimit { get; init; }

    public int? TurnLimit { get; init; }

    /// <summary>Time limit in whole seconds (Python's <c>time_limit: int</c>).</summary>
    public int? TimeLimit { get; init; }

    /// <summary>Working limit in whole seconds.</summary>
    public int? WorkingLimit { get; init; }

    public double? CostLimit { get; init; }

    /// <summary>The identity arguments an <see cref="EvalOptions"/> carries: its model's config and its limits.</summary>
    public static EvalSetArgsInTaskIdentifier FromOptions(EvalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new EvalSetArgsInTaskIdentifier
        {
            Config = options.Model?.Config ?? new(),
            MessageLimit = options.MessageLimit,
            TokenLimit = options.TokenLimit,
            TurnLimit = options.TurnLimit,
            TimeLimit = (int?)options.TimeLimit?.TotalSeconds,
            WorkingLimit = (int?)options.WorkingLimit?.TotalSeconds,
            CostLimit = options.CostLimit,
        };
    }
}

/// <summary>
/// Port of <c>_eval/evalset.py</c> <c>task_identifier</c>: the identifier that pairs a task with its log files across
/// the runs of an eval set. It has the form <c>{task_name}#{args_hash}/{model}/{additional_hash}</c> (Python
/// prefixes <c>{task_file}@</c> for file-based tasks; this port has no task files, and a log's <c>task_file</c> is
/// honoured when present). The additional hash is the SHA-256 of the concatenated pydantic-style JSON of the plan
/// (name, steps, generate config), the model's own generate config, the model roles and the remaining fields
/// (model args, version, limits), each excluding the runtime knobs of <see cref="GenerateConfigFieldsToExclude"/>.
/// The same value is computed from a task before it runs (<see cref="Compute(EvalTask, Model, ModelRoles?, EvalSetArgsInTaskIdentifier)"/>)
/// and from the log it produced (<see cref="Compute(EvalLog)"/>). The JSON canonicalisation reproduces
/// <c>pydantic_core.to_json</c> (Python field order, <c>exclude_none</c> for models, 2-space indent), so identifiers
/// agree with Python's for the inputs both sides can express (the runner's plan, no model args, no task file).
/// </summary>
public static class TaskIdentifier
{
    /// <summary>Port of <c>TASK_IDENTIFIER_VERSION</c>: bumped only when computed identifiers change.</summary>
    public const int Version = 3;

    /// <summary>Port of <c>GENERATE_CONFIG_FIELDS_TO_EXCLUDE</c>: runtime/transport knobs that never affect model output.</summary>
    public static IReadOnlySet<string> GenerateConfigFieldsToExclude { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "max_retries",
        "timeout",
        "attempt_timeout",
        "stream_idle_timeout",
        "max_connections",
        "adaptive_connections",
        "batch",
        "cache",
        "cache_prompt",
    };

    /// <summary>The identifier of <paramref name="task"/> run against <paramref name="model"/> with the given (merged) roles and eval-set arguments.</summary>
    public static string Compute(EvalTask task, Model model, ModelRoles? modelRoles, EvalSetArgsInTaskIdentifier args)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(args);
        // the runner's plan config: the model's config with the task's layered over it (Eval.EvalModel)
        var plan = Eval.ResolvePlan(task, args.Config.Merge(task.Config));
        var fields = new AdditionalHashFields(
            ModelArgs: model.Api.ModelArgsForLog,
            Version: task.Version,
            MessageLimit: args.MessageLimit ?? task.MessageLimit,
            TokenLimit: TokenLimitHashValue(args.TokenLimit ?? task.TokenLimit, null),
            TurnLimit: args.TurnLimit ?? task.TurnLimit,
            TimeLimit: args.TimeLimit ?? (int?)task.TimeLimit?.TotalSeconds,
            WorkingLimit: args.WorkingLimit ?? (int?)task.WorkingLimit?.TotalSeconds,
            CostLimit: args.CostLimit ?? task.CostLimit);
        return Compute(
            taskFile: "",
            taskName: task.Name,
            taskArgs: task.TaskArgs ?? new Dictionary<string, object?>(StringComparer.Ordinal),
            model: ModelIdentity.ForLog(model.Api),
            modelGenerateConfig: model.Config,
            modelRoles: ModelRolesConfig.ToConfig(modelRoles),
            plan: plan,
            fields: fields);
    }

    /// <summary>The identifier of the task a log was produced by (the log carries every resolved input).</summary>
    public static string Compute(EvalLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var spec = log.Eval;
        var fields = new AdditionalHashFields(
            ModelArgs: ModelArgumentSanitizer.ForLog(spec.ModelArgs),
            Version: spec.TaskVersion,
            MessageLimit: spec.Config.MessageLimit,
            TokenLimit: TokenLimitHashValue(spec.Config.TokenLimit, spec.Config.TokenLimitType),
            TurnLimit: spec.Config.TurnLimit,
            TimeLimit: spec.Config.TimeLimit,
            WorkingLimit: spec.Config.WorkingLimit,
            CostLimit: spec.Config.CostLimit);
        return Compute(
            taskFile: spec.TaskFile ?? "",
            taskName: spec.Task,
            taskArgs: spec.TaskArgsPassed ?? spec.TaskArgs,
            model: spec.Model,
            modelGenerateConfig: spec.ModelGenerateConfig,
            modelRoles: spec.ModelRoles,
            plan: log.Plan,
            fields: fields);
    }

    /// <summary>Port of <c>task_args_hash</c>: the SHA-256 of the compact JSON of the args as passed.</summary>
    public static string TaskArgsHash(IReadOnlyDictionary<string, object?> taskArgs)
    {
        ArgumentNullException.ThrowIfNull(taskArgs);
        var node = JsonSerializer.SerializeToNode(taskArgs, EvalLogWriter.Options) ?? new JsonObject();
        return Sha256Hex(PydanticJson.Compact(node));
    }

    /// <summary>Port of <c>token_limit_hash_value</c>: a bare count for all-token limits, <c>"{type}:{tokens}"</c> otherwise.</summary>
    internal static object? TokenLimitHashValue(int? tokens, string? type) =>
        tokens is { } count && type is { } metering && metering != "all" ? $"{metering}:{count}" : tokens;

    private static string Compute(
        string taskFile,
        string taskName,
        IReadOnlyDictionary<string, object?> taskArgs,
        string model,
        GenerateConfig modelGenerateConfig,
        IReadOnlyDictionary<string, IReadOnlyList<ModelConfig>>? modelRoles,
        EvalPlan plan,
        AdditionalHashFields fields)
    {
        var argsHash = TaskArgsHash(taskArgs);
        var input = new StringBuilder();
        input.Append(PydanticJson.Pretty(PlanNode(plan)));
        input.Append(PydanticJson.Pretty(PydanticJson.GenerateConfigNode(modelGenerateConfig, GenerateConfigFieldsToExclude)));
        if (modelRoles is { Count: > 0 })
        {
            input.Append(PydanticJson.Pretty(ModelRolesNode(modelRoles)));
        }

        input.Append(PydanticJson.Pretty(fields.ToNode()));
        var additionalHash = Sha256Hex(input.ToString());
        return taskFile.Length > 0
            ? $"{taskFile}@{taskName}#{argsHash}/{model}/{additionalHash}"
            : $"{taskName}#{argsHash}/{model}/{additionalHash}";
    }

    /// <summary>
    /// The plan as Python hashes it: <c>finish</c> dropped and each step reduced to its solver and
    /// <c>params_passed</c> (older logs have no resolved <c>params</c>, so the passed args are the comparison basis).
    /// </summary>
    private static JsonObject PlanNode(EvalPlan plan)
    {
        var steps = new JsonArray();
        foreach (var step in plan.Steps)
        {
            steps.Add(new JsonObject
            {
                ["solver"] = step.Solver,
                ["params_passed"] = JsonSerializer.SerializeToNode(step.ParamsPassed ?? step.Params, EvalLogWriter.Options) ?? new JsonObject(),
            });
        }

        return new JsonObject
        {
            ["name"] = plan.Name,
            ["steps"] = steps,
            ["config"] = PydanticJson.GenerateConfigNode(plan.Config, GenerateConfigFieldsToExclude),
        };
    }

    /// <summary>The roles as Python hashes them: <c>base_url</c> excluded (several providers fill it from the environment) along with the runtime config fields.</summary>
    private static JsonObject ModelRolesNode(IReadOnlyDictionary<string, IReadOnlyList<ModelConfig>> roles)
    {
        var node = new JsonObject();
        foreach (var (role, models) in roles)
        {
            if (models.Count == 1)
            {
                node[role] = ModelConfigNode(models[0]);
            }
            else
            {
                var list = new JsonArray();
                foreach (var config in models)
                {
                    list.Add(ModelConfigNode(config));
                }

                node[role] = list;
            }
        }

        return node;

        static JsonObject ModelConfigNode(ModelConfig config) => new()
        {
            ["model"] = config.Model,
            ["config"] = PydanticJson.GenerateConfigNode(config.Config, GenerateConfigFieldsToExclude),
            ["args"] = JsonSerializer.SerializeToNode(ModelArgumentSanitizer.ForLog(config.Args), EvalLogWriter.Options) ?? new JsonObject(),
        };
    }

    private static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Port of the <c>AdditionalHashFields</c> dataclass; being a dataclass, Python keeps its null fields (unlike its pydantic models).</summary>
    private sealed record AdditionalHashFields(
        IReadOnlyDictionary<string, object?> ModelArgs,
        string Version,
        int? MessageLimit,
        object? TokenLimit,
        int? TurnLimit,
        int? TimeLimit,
        int? WorkingLimit,
        double? CostLimit)
    {
        public JsonObject ToNode() => new()
        {
            ["model_args"] = JsonSerializer.SerializeToNode(ModelArgs, EvalLogWriter.Options) ?? new JsonObject(),
            ["version"] = VersionNode(Version),
            ["message_limit"] = MessageLimit,
            ["token_limit"] = TokenLimit switch
            {
                int tokens => JsonValue.Create(tokens),
                string metered => JsonValue.Create(metered),
                _ => null,
            },
            ["turn_limit"] = TurnLimit,
            ["time_limit"] = TimeLimit,
            ["working_limit"] = WorkingLimit,
            ["cost_limit"] = CostLimit,
        };

        /// <summary>Python's <c>version: int | str</c>: a digit-only version is the number it names.</summary>
        private static JsonNode VersionNode(string version) =>
            version.Length > 0 && version.All(char.IsAsciiDigit) && long.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? JsonValue.Create(number)
                : JsonValue.Create(version);
    }
}

/// <summary>
/// Port of <c>model_roles_to_model_roles_config</c> / <c>model_roles_config_to_model_roles</c>: the resolved roles
/// as the <see cref="ModelConfig"/> records the log carries, and back again for a retry.
/// </summary>
public static class ModelRolesConfig
{
    /// <summary>
    /// The roles a log recorded as the <c>model_roles</c> value <see cref="EvalOptions.ModelRoles"/> takes: each
    /// role's models created by <paramref name="modelFactory"/> (default <c>Models.Create</c>) with the
    /// recorded generate config, base URL and sanitized model arguments; null for null roles. A custom name-only factory owns its endpoint and arguments.
    /// </summary>
    public static IReadOnlyDictionary<string, object>? FromConfig(IReadOnlyDictionary<string, IReadOnlyList<ModelConfig>>? roles, Func<string, Model>? modelFactory = null)
    {
        if (roles is null)
        {
            return null;
        }

        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (role, configs) in roles)
        {
            var models = configs.Select(config => modelFactory is null ? Models.Create(config.Model, config.Config, config.BaseUrl, modelArgs: config.Args) : modelFactory(config.Model).WithConfig(config.Config)).ToList();
            result[role] = models.Count == 1 ? models[0] : models;
        }

        return result;
    }

    /// <summary>Each role's models as <see cref="ModelConfig"/> (qualified name, config, resolved base URL and sanitized args); null for null roles.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ModelConfig>>? ToConfig(ModelRoles? roles)
    {
        if (roles is null)
        {
            return null;
        }

        var result = new Dictionary<string, IReadOnlyList<ModelConfig>>(StringComparer.Ordinal);
        foreach (var (role, models) in roles)
        {
            result[role] = models.Select(model => new ModelConfig(ModelIdentity.ForLog(model.Api)) { Config = model.Config, BaseUrl = model.Api.BaseUrl, Args = model.Api.ModelArgsForLog }).ToArray();
        }

        return result;
    }
}

/// <summary>
/// Reproduces the JSON <c>pydantic_core.to_json</c> writes for the identifier inputs: model fields in Python's
/// declaration order with <c>None</c> fields dropped, 2-space pretty printing (every container element on its own
/// line, <c>{}</c> / <c>[]</c> when empty), and floats in pydantic's shortest form (<c>2.0</c>, <c>1e+16</c>,
/// <c>1.5e-7</c>). Strings are escaped as serde does: quotes, backslashes and control characters only.
/// </summary>
internal static class PydanticJson
{
    /// <summary>Python's <c>GenerateConfig</c> field order, which the hash input must follow.</summary>
    private static readonly string[] GenerateConfigFieldOrder =
    [
        "max_retries",
        "timeout",
        "attempt_timeout",
        "stream_idle_timeout",
        "max_connections",
        "adaptive_connections",
        "system_message",
        "max_tokens",
        "top_p",
        "temperature",
        "stop_seqs",
        "best_of",
        "frequency_penalty",
        "presence_penalty",
        "logit_bias",
        "seed",
        "top_k",
        "num_choices",
        "logprobs",
        "top_logprobs",
        "prompt_logprobs",
        "parallel_tool_calls",
        "internal_tools",
        "max_tool_output",
        "cache_prompt",
        "fallback_models",
        "verbosity",
        "effort",
        "reasoning_effort",
        "reasoning_mode",
        "reasoning_tokens",
        "reasoning_summary",
        "reasoning_history",
        "response_schema",
        "extra_headers",
        "extra_body",
        "modalities",
        "cache",
        "batch",
    ];

    /// <summary>A generate config as pydantic dumps it (Python field order, nulls dropped) minus <paramref name="exclude"/>; unknown fields follow in serializer order.</summary>
    public static JsonObject GenerateConfigNode(GenerateConfig config, IReadOnlySet<string> exclude)
    {
        var serialized = JsonSerializer.SerializeToNode(config, EvalLogWriter.Options) as JsonObject ?? new JsonObject();
        var ordered = new JsonObject();
        foreach (var field in GenerateConfigFieldOrder)
        {
            if (!exclude.Contains(field) && serialized[field] is { } value)
            {
                ordered[field] = value.DeepClone();
            }
        }

        foreach (var (name, value) in serialized)
        {
            if (value is not null && !exclude.Contains(name) && !ordered.ContainsKey(name))
            {
                ordered[name] = value.DeepClone();
            }
        }

        return ordered;
    }

    /// <summary>Port of <c>to_json(indent=2)</c>.</summary>
    public static string Pretty(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(builder, node, 0, pretty: true);
        return builder.ToString();
    }

    /// <summary>Port of <c>to_json()</c> without indentation: no whitespace at all.</summary>
    public static string Compact(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(builder, node, 0, pretty: false);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, JsonNode? node, int depth, bool pretty)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject obj:
                if (obj.Count == 0)
                {
                    builder.Append("{}");
                    break;
                }

                builder.Append('{');
                var first = true;
                foreach (var (name, value) in obj)
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    NewLine(builder, depth + 1, pretty);
                    WriteString(builder, name);
                    builder.Append(pretty ? ": " : ":");
                    Write(builder, value, depth + 1, pretty);
                }

                NewLine(builder, depth, pretty);
                builder.Append('}');
                break;
            case JsonArray array:
                if (array.Count == 0)
                {
                    builder.Append("[]");
                    break;
                }

                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    NewLine(builder, depth + 1, pretty);
                    Write(builder, array[i], depth + 1, pretty);
                }

                NewLine(builder, depth, pretty);
                builder.Append(']');
                break;
            case JsonValue value:
                WriteValue(builder, value);
                break;
            default:
                throw new NotSupportedException($"Unsupported JSON node type {node.GetType().Name}.");
        }
    }

    private static void NewLine(StringBuilder builder, int depth, bool pretty)
    {
        if (pretty)
        {
            builder.Append('\n').Append(' ', depth * 2);
        }
    }

    private static void WriteValue(StringBuilder builder, JsonValue value)
    {
        if (value.TryGetValue<string>(out var text))
        {
            WriteString(builder, text);
        }
        else if (value.TryGetValue<bool>(out var flag))
        {
            builder.Append(flag ? "true" : "false");
        }
        else if (value.TryGetValue<int>(out var integer))
        {
            builder.Append(integer.ToString(CultureInfo.InvariantCulture));
        }
        else if (value.TryGetValue<long>(out var longInteger))
        {
            builder.Append(longInteger.ToString(CultureInfo.InvariantCulture));
        }
        else if (value.TryGetValue<double>(out var number))
        {
            builder.Append(FormatDouble(number));
        }
        else if (value.TryGetValue<JsonElement>(out var element))
        {
            WriteElement(builder, element);
        }
        else
        {
            builder.Append(value.ToJsonString());
        }
    }

    private static void WriteElement(StringBuilder builder, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                WriteString(builder, element.GetString()!);
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
                builder.Append("null");
                break;
            case JsonValueKind.Number:
                var raw = element.GetRawText();
                builder.Append(raw.Contains('.', StringComparison.Ordinal) || raw.Contains('e', StringComparison.OrdinalIgnoreCase) ? FormatDouble(element.GetDouble()) : raw);
                break;
            default:
                builder.Append(element.GetRawText());
                break;
        }
    }

    /// <summary>
    /// pydantic's float form: the shortest round-trip digits, always with a fraction (<c>2.0</c>), decimal notation
    /// up to <c>1e16</c>, and an unpadded exponent beyond (<c>1e+16</c>, <c>1.5e-7</c>).
    /// </summary>
    internal static string FormatDouble(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Non-finite numbers cannot be hashed.");
        }

        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var exponentAt = text.IndexOf('E');
        if (exponentAt < 0)
        {
            return text.Contains('.', StringComparison.Ordinal) ? text : text + ".0";
        }

        var mantissa = text[..exponentAt];
        var exponent = int.Parse(text[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        if (Math.Abs(value) < 1e16 && exponent > 0)
        {
            return Expand(mantissa, exponent);
        }

        return $"{mantissa}e{(exponent < 0 ? "-" : "+")}{Math.Abs(exponent)}";
    }

    /// <summary>Shifts the decimal point of <paramref name="mantissa"/> right by <paramref name="exponent"/> places (values .NET prints in exponent form below pydantic's threshold).</summary>
    private static string Expand(string mantissa, int exponent)
    {
        var negative = mantissa.StartsWith('-');
        var digits = negative ? mantissa[1..] : mantissa;
        var point = digits.IndexOf('.');
        var integerPart = point < 0 ? digits : digits[..point];
        var fraction = point < 0 ? "" : digits[(point + 1)..];
        var all = integerPart + fraction;
        var newPoint = integerPart.Length + exponent;
        var result = newPoint >= all.Length
            ? all + new string('0', newPoint - all.Length) + ".0"
            : all[..newPoint] + "." + all[newPoint..];
        return negative ? "-" + result : result;
    }

    private static void WriteString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                default:
                    if (c < ' ')
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }
}

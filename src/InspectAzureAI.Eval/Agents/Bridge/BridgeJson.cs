using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Agents.Bridge;

/// <summary>
/// Port of <c>agent/_bridge/_errors.py</c> <c>BridgePolicyError</c>: a bridged request the bridge cannot serve
/// (malformed or unsupported client input). The sandbox bridge answers it with a 400 in the client's dialect.
/// The OpenAI dialects also report <see cref="Param"/> and <see cref="Code"/>, as the proxy's missing-parameter
/// errors do (<c>proxy.py:660-671</c>).
/// </summary>
public sealed class BridgeRequestException(string message) : Exception(message)
{
    /// <summary>The request parameter at fault (the OpenAI error body's <c>param</c>), when known.</summary>
    public string? Param { get; init; }

    /// <summary>A machine-readable error code (the OpenAI error body's <c>code</c>), such as <c>missing_required_parameter</c>.</summary>
    public string? Code { get; init; }
}

/// <summary>
/// Port of <c>validate_bridge_media</c> (<c>agent/_bridge/util.py:753-797</c>) with remote media disallowed. A
/// bridged image, document, audio or video must be an inline <c>data:</c> URI. Any other reference could make the
/// host fetch a URL or read a host file for the sandboxed agent, so it is rejected rather than dereferenced.
/// </summary>
public static class BridgeMedia
{
    /// <summary>Throws <see cref="BridgeRequestException"/> naming the first media item that is not an inline <c>data:</c> URI.</summary>
    public static void RequireInline(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
        {
            var message = messages[messageIndex];
            if (message.Content.IsString)
            {
                continue;
            }

            var items = message.Content.Items!;
            for (var contentIndex = 0; contentIndex < items.Count; contentIndex++)
            {
                var content = items[contentIndex];
                var uri = content switch
                {
                    ContentImage image => image.Image,
                    ContentDocument document => document.Document,
                    ContentAudio audio => audio.Audio,
                    ContentVideo video => video.Video,
                    _ => null,
                };
                if (uri is null || uri.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                throw new BridgeRequestException(
                    $"Bridged {content.Type} content at message index {messageIndex}, content index {contentIndex} must be an inline 'data:' URI; "
                    + "the agent bridge will not dereference a non-inline reference.");
            }
        }
    }
}

/// <summary>A parsed bridged request: what <c>AgentBridge.GenerateAsync</c> needs plus whether the client asked for a stream.</summary>
public sealed record BridgeRequest(
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ToolInfo> Tools,
    ToolChoice ToolChoice,
    GenerateConfig Config,
    bool Stream);

/// <summary>
/// JSON helpers shared by the bridge dialects: <c>client_request_object</c> (a field that must be an object),
/// <c>ToolParams.model_validate(schema)</c> (a JSON schema into <see cref="ToolParams"/>) and tolerant scalar reads.
/// </summary>
internal static class BridgeJson
{
    /// <summary>Port of <c>client_request_object</c>: null passes through, anything but an object is a client error naming the field.</summary>
    public static JsonObject? RequestObject(JsonNode? value, string field)
    {
        if (value is null)
        {
            return null;
        }

        return value as JsonObject
            ?? throw new BridgeRequestException($"invalid request field in bridged request ({field}: expected an object, got {Describe(value)})");
    }

    public static JsonArray RequireArray(JsonNode? value, string field) =>
        value as JsonArray ?? throw new BridgeRequestException($"invalid request field in bridged request ({field}: expected an array, got {Describe(value)})");

    public static string? GetString(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node) && node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    public static int? GetInt(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var i))
        {
            return i;
        }

        return value.TryGetValue<double>(out var d) && Math.Abs(d - Math.Round(d)) < double.Epsilon ? (int)d : null;
    }

    public static double? GetDouble(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node) && node is JsonValue value && value.TryGetValue<double>(out var d) ? d : null;

    public static bool? GetBool(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node) && node is JsonValue value && value.TryGetValue<bool>(out var b) ? b : null;

    public static IReadOnlyList<string>? GetStringList(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonArray array)
        {
            return null;
        }

        var items = array.Select(item => item?.ToString()).OfType<string>().ToArray();
        return items.Length > 0 ? items : null;
    }

    /// <summary>Port of <c>ToolParams.model_validate(input_schema)</c>: the object schema of a client tool.</summary>
    public static ToolParams ToolParamsFromSchema(JsonObject? schema)
    {
        if (schema is null)
        {
            return new ToolParams();
        }

        var properties = new Dictionary<string, ToolParam>(StringComparer.Ordinal);
        if (schema["properties"] is JsonObject props)
        {
            foreach (var (name, node) in props)
            {
                properties[name] = node is JsonObject param ? ToolParamFromSchema(param) : new ToolParam();
            }
        }

        return new ToolParams
        {
            Properties = properties,
            Required = GetStringList(schema, "required") ?? [],
            AdditionalProperties = AdditionalPropertiesFromSchema(schema, false),
        };
    }

    /// <summary>Port of <c>JSONSchema.model_validate</c>: one parameter's schema (unmodelled keywords are dropped, as in Python).</summary>
    public static ToolParam ToolParamFromSchema(JsonObject schema)
    {
        IReadOnlyList<string>? type = schema["type"] switch
        {
            JsonArray types => types.Select(t => t?.ToString()).OfType<string>().ToArray(),
            JsonValue single when single.TryGetValue<string>(out var s) => [s],
            _ => null,
        };

        Dictionary<string, ToolParam>? properties = null;
        if (schema["properties"] is JsonObject props)
        {
            properties = new Dictionary<string, ToolParam>(StringComparer.Ordinal);
            foreach (var (name, node) in props)
            {
                properties[name] = node is JsonObject param ? ToolParamFromSchema(param) : new ToolParam();
            }
        }

        return new ToolParam
        {
            Type = type,
            Format = GetString(schema, "format"),
            Description = GetString(schema, "description"),
            Default = schema["default"]?.DeepClone(),
            Enum = schema["enum"] is JsonArray enumValues ? enumValues.Select(v => v?.DeepClone()).ToArray() : null,
            Items = schema["items"] is JsonObject items ? ToolParamFromSchema(items) : null,
            Properties = properties,
            AdditionalProperties = AdditionalPropertiesFromSchema(schema, null),
            AnyOf = schema["anyOf"] is JsonArray anyOf ? anyOf.OfType<JsonObject>().Select(ToolParamFromSchema).ToArray() : null,
            Required = GetStringList(schema, "required"),
            Pattern = GetString(schema, "pattern"),
            MinLength = GetInt(schema, "minLength"),
            MaxLength = GetInt(schema, "maxLength"),
            Minimum = GetDouble(schema, "minimum"),
            Maximum = GetDouble(schema, "maximum"),
            Examples = schema["examples"] is JsonArray examples ? examples.Select(v => v?.DeepClone()).ToArray() : null,
        };
    }

    private static object? AdditionalPropertiesFromSchema(JsonObject schema, object? @default) => schema["additionalProperties"] switch
    {
        JsonObject nested => ToolParamFromSchema(nested),
        JsonValue value when value.TryGetValue<bool>(out var allowed) => allowed,
        _ => @default,
    };

    /// <summary>Python's <c>type(value).__name__</c> for a client value, as used in request-field errors.</summary>
    internal static string Describe(JsonNode? node) => node switch
    {
        null => "null",
        JsonArray => "list",
        JsonObject => "dict",
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.String => "str",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "bool",
            _ => "value",
        },
        _ => node.GetType().Name,
    };
}

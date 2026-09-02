using System.Text.Json.Nodes;

namespace InspectAzureAI.Provider.Core;

/// <summary>
/// JSON schema for a tool parameter (port of <c>JSONSchema</c> in <c>src/inspect_ai/util/_json.py</c>,
/// aliased as <c>ToolParam</c>). Every field defaults to null and <see cref="ToJson"/> mirrors
/// pydantic <c>model_dump(exclude_none=True)</c> including field order.
/// </summary>
public sealed record ToolParam
{
    /// <summary>JSON type, or list of JSON types (<c>type: JSONType | list[JSONType]</c>).</summary>
    public IReadOnlyList<string>? Type { get; init; }

    public string? Format { get; init; }

    public string? Description { get; init; }

    public JsonNode? Default { get; init; }

    public IReadOnlyList<JsonNode?>? Enum { get; init; }

    public ToolParam? Items { get; init; }

    public IReadOnlyDictionary<string, ToolParam>? Properties { get; init; }

    /// <summary>A nested <see cref="ToolParam"/>, a bool, or null.</summary>
    public object? AdditionalProperties { get; init; }

    public IReadOnlyList<ToolParam>? AnyOf { get; init; }

    public IReadOnlyList<string>? Required { get; init; }

    public string? Pattern { get; init; }

    public int? MinLength { get; init; }

    public int? MaxLength { get; init; }

    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    public IReadOnlyList<JsonNode?>? Examples { get; init; }

    /// <summary>Convenience constructor for a single-typed parameter.</summary>
    public static ToolParam Of(string type, string? description = null) =>
        new() { Type = [type], Description = description };

    /// <summary>Port of <c>model_dump(exclude_none=True)</c> for this schema.</summary>
    public JsonObject ToJson()
    {
        var obj = new JsonObject();
        if (Type is not null)
        {
            obj["type"] = Type.Count == 1 ? JsonValue.Create(Type[0]) : new JsonArray(Type.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
        }

        if (Format is not null)
        {
            obj["format"] = Format;
        }

        if (Description is not null)
        {
            obj["description"] = Description;
        }

        if (Default is not null)
        {
            obj["default"] = Default.DeepClone();
        }

        if (Enum is not null)
        {
            obj["enum"] = new JsonArray(Enum.Select(e => e?.DeepClone()).ToArray());
        }

        if (Items is not null)
        {
            obj["items"] = Items.ToJson();
        }

        if (Properties is not null)
        {
            var props = new JsonObject();
            foreach (var (name, schema) in Properties)
            {
                props[name] = schema.ToJson();
            }

            obj["properties"] = props;
        }

        switch (AdditionalProperties)
        {
            case ToolParam schema:
                obj["additionalProperties"] = schema.ToJson();
                break;
            case bool allowed:
                obj["additionalProperties"] = allowed;
                break;
        }

        if (AnyOf is not null)
        {
            obj["anyOf"] = new JsonArray(AnyOf.Select(s => (JsonNode?)s.ToJson()).ToArray());
        }

        if (Required is not null)
        {
            obj["required"] = new JsonArray(Required.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
        }

        if (Pattern is not null)
        {
            obj["pattern"] = Pattern;
        }

        if (MinLength is not null)
        {
            obj["minLength"] = MinLength;
        }

        if (MaxLength is not null)
        {
            obj["maxLength"] = MaxLength;
        }

        if (Minimum is not null)
        {
            obj["minimum"] = Minimum;
        }

        if (Maximum is not null)
        {
            obj["maximum"] = Maximum;
        }

        if (Examples is not null)
        {
            obj["examples"] = new JsonArray(Examples.Select(e => e?.DeepClone()).ToArray());
        }

        return obj;
    }
}

/// <summary>
/// Tool parameters object schema (port of <c>ToolParams</c> in
/// <c>src/inspect_ai/tool/_tool_params.py</c>): always <c>type: object</c> with
/// <c>additionalProperties: false</c> by default.
/// </summary>
public sealed record ToolParams
{
    public string Type => "object";

    public IReadOnlyDictionary<string, ToolParam> Properties { get; init; } = new Dictionary<string, ToolParam>();

    public IReadOnlyList<string> Required { get; init; } = [];

    /// <summary>A nested <see cref="ToolParam"/>, a bool (default false), or null.</summary>
    public object? AdditionalProperties { get; init; } = false;

    /// <summary>Port of <c>model_dump(exclude_none=True)</c> for the parameters object.</summary>
    public JsonObject ToJson()
    {
        var props = new JsonObject();
        foreach (var (name, schema) in Properties)
        {
            props[name] = schema.ToJson();
        }

        var obj = new JsonObject
        {
            ["type"] = Type,
            ["properties"] = props,
            ["required"] = new JsonArray(Required.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
        };
        switch (AdditionalProperties)
        {
            case ToolParam schema:
                obj["additionalProperties"] = schema.ToJson();
                break;
            case bool allowed:
                obj["additionalProperties"] = allowed;
                break;
        }

        return obj;
    }
}

/// <summary>Tool description passed to the model (port of <c>ToolInfo</c>, <c>src/inspect_ai/tool/_tool_info.py</c>).</summary>
public sealed record ToolInfo(string Name, string Description)
{
    public ToolParams Parameters { get; init; } = new();

    /// <summary>Provider-specific options (never sent by the azureai provider).</summary>
    public JsonObject? Options { get; init; }

    /// <summary>Port of <c>json_schema_dump(tool)</c>: the full ToolInfo as JSON with None fields dropped.</summary>
    public JsonObject ToJson()
    {
        var obj = new JsonObject
        {
            ["name"] = Name,
            ["description"] = Description,
            ["parameters"] = Parameters.ToJson(),
        };
        if (Options is not null)
        {
            obj["options"] = Options.DeepClone();
        }

        return obj;
    }
}

/// <summary>A tool call requested by the model (port of <c>ToolCall</c>, <c>src/inspect_ai/tool/_tool_call.py</c>).</summary>
public sealed record ToolCall(string Id, string Function, JsonObject Arguments)
{
    /// <summary>Error which occurred parsing tool call arguments (reported back to the model).</summary>
    public string? ParseError { get; init; }

    /// <summary>Call type: <c>function</c> or <c>custom</c>.</summary>
    public string Type { get; init; } = "function";
}

/// <summary>Error raised by a tool call (port of <c>ToolCallError</c>, <c>_tool_call.py</c>).</summary>
public sealed record ToolCallError(string Type, string Message);

/// <summary>
/// Tool choice union (port of <c>ToolChoice = Literal["auto","any","none"] | ToolFunction</c> in
/// <c>src/inspect_ai/tool/_tool_choice.py</c>). Use <see cref="Auto"/>, <see cref="Any"/>,
/// <see cref="None"/> or a <see cref="ToolFunction"/>.
/// </summary>
public abstract record ToolChoice
{
    public static readonly ToolChoice Auto = new ToolChoicePreset("auto");

    public static readonly ToolChoice Any = new ToolChoicePreset("any");

    public static readonly ToolChoice None = new ToolChoicePreset("none");

    private sealed record ToolChoicePreset(string Value) : ToolChoice
    {
        public override string ToString() => Value;
    }
}

/// <summary>Force the model to call a specific function (port of <c>ToolFunction</c>).</summary>
public sealed record ToolFunction(string Name) : ToolChoice;

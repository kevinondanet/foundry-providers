using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Tools;

namespace InspectAzureAI.Provider.OpenAI;

/// <summary>
/// Tool and tool-choice parameters for the Responses API (port of <c>_tool_param_for_tool_info</c>,
/// <c>openai_responses_tool_choice</c> and the <c>text.format</c> block of
/// <c>src/inspect_ai/model/_openai_responses.py</c>). Function tools are flat (<c>name</c> and
/// <c>parameters</c> at the top level, no <c>function</c> wrapper) and never strict (default parameter
/// values do not work in strict mode). The built-in tools (web search, computer use, remote MCP, code
/// interpreter) are not ported on this route.
/// </summary>
public static class ResponsesTools
{
    /// <summary>Inspect's builtin <c>python</c> tool name is reserved by the Responses API; it travels as <c>python_exec</c>.</summary>
    public const string PythonAlias = "python_exec";

    /// <summary>The <c>tools</c> array: one flat function tool per <see cref="ToolInfo"/>.</summary>
    public static JsonArray ToolParams(IReadOnlyList<ToolInfo> tools) =>
        new(tools.Select(t => (JsonNode?)ToolParam(t)).ToArray());

    /// <summary>One function tool, its schema stripped of the extended validation fields (as the chat route does).</summary>
    public static JsonObject ToolParam(ToolInfo tool) => new()
    {
        ["type"] = "function",
        ["name"] = Alias(tool.Name),
        ["description"] = tool.Description,
        ["parameters"] = JsonSchemaDump.Dump(tool.Parameters.ToJson(), JsonSchemaDump.JsonSchemaExtendedFields),
        ["strict"] = false,
    };

    /// <summary>
    /// <c>none</c>, <c>required</c> (Inspect's <c>any</c>) or a named function; null for <c>auto</c>, the API
    /// default, which is not sent.
    /// </summary>
    public static JsonNode? ToolChoiceParam(ToolChoice toolChoice) => toolChoice switch
    {
        ToolFunction function => new JsonObject { ["type"] = "function", ["name"] = Alias(function.Name) },
        _ when toolChoice.ToString() == "any" => JsonValue.Create("required"),
        _ when toolChoice.ToString() == "none" => JsonValue.Create("none"),
        _ => null,
    };

    /// <summary>The wire name of a tool (<c>python</c> becomes <see cref="PythonAlias"/>).</summary>
    public static string Alias(string name) => name == "python" ? PythonAlias : name;

    /// <summary>The Inspect name of a tool the model called (the inverse of <see cref="Alias"/>).</summary>
    public static string Unalias(string name) => name == PythonAlias ? "python" : name;

    /// <summary>
    /// <c>text.format</c> for a response schema: the flat <c>json_schema</c> form of the Responses API (name,
    /// schema, description and strict at the top level rather than under <c>json_schema</c>).
    /// </summary>
    public static JsonObject TextFormat(ResponseSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var format = new JsonObject
        {
            ["type"] = "json_schema",
            ["name"] = schema.Name,
            ["schema"] = JsonSchemaDump.Dump(schema.JsonSchema.ToJson(), JsonSchemaDump.JsonSchemaExtendedFields),
        };
        if (schema.Description is not null)
        {
            format["description"] = schema.Description;
        }

        if (schema.Strict is not null)
        {
            format["strict"] = schema.Strict;
        }

        return format;
    }
}

using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Tools;

namespace InspectAzureAI.Provider.OpenAI;

/// <summary>
/// Tool and tool-choice parameters for the Responses API. Ports <c>_tool_param_for_tool_info</c>,
/// <c>openai_responses_tools</c> (verbatim and namespace regrouping, lines 567-622),
/// <c>openai_responses_tool_choice</c> and the <c>text.format</c> block of
/// <c>src/inspect_ai/model/_openai_responses.py</c>. Function tools are flat (<c>name</c> and <c>parameters</c> at
/// the top level, no <c>function</c> wrapper) and never strict, because default parameter values do not work in
/// strict mode. A tool the agent bridge converted from a scaffold keeps its original <c>function</c> param under
/// <see cref="VerbatimOption"/> and is resent byte-for-byte. A tool flattened from a <c>namespace</c> carries
/// <see cref="NamespaceOption"/> and is regrouped into a <c>namespace</c> tool. OpenAI rejects reserved tools that
/// arrive flat or with a drifted schema. The built-in tools (web search, computer use, remote MCP, code interpreter)
/// and native <c>custom</c> tools are not ported on this route.
/// </summary>
public static class ResponsesTools
{
    /// <summary>Inspect's builtin <c>python</c> tool name is reserved by the Responses API; it travels as <c>python_exec</c>.</summary>
    public const string PythonAlias = "python_exec";

    /// <summary>
    /// <see cref="ToolInfo.Options"/> key holding the scaffold's original Responses tool param (Python
    /// <c>RESPONSES_VERBATIM</c>). A <c>function</c> param stored there is resent unchanged. A <c>custom</c> param is not,
    /// because this provider cannot parse <c>custom_tool_call</c> output items; the tool goes out as a function instead.
    /// </summary>
    public const string VerbatimOption = "__responses_verbatim__";

    /// <summary>
    /// <see cref="ToolInfo.Options"/> key holding the <c>[name, description]</c> JSON array of the <c>namespace</c> tool
    /// a tool was flattened from (Python <c>RESPONSES_NAMESPACE</c>, a tuple that becomes a list after a JSON round-trip).
    /// </summary>
    public const string NamespaceOption = "__responses_namespace__";

    /// <summary>
    /// The <c>tools</c> array, in order: one param per untagged tool, then one <c>namespace</c> tool per distinct
    /// <c>[name, description]</c> tag in first-seen order, holding its tools' params. A verbatim <c>function</c> param
    /// is resent unchanged, with no <c>python</c> alias and no schema dump. Every other tool is a flat function tool.
    /// A list with neither option gives exactly one flat function per tool.
    /// </summary>
    public static JsonArray ToolParams(IReadOnlyList<ToolInfo> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var result = new JsonArray();
        var groups = new List<(string Name, string Description, JsonArray Tools)>();
        foreach (var tool in tools)
        {
            var param = tool.Options?[VerbatimOption] is JsonObject verbatim && IsFunctionParam(verbatim)
                ? verbatim.DeepClone().AsObject()
                : ToolParam(tool);
            if (NamespaceTag(tool) is { } tag)
            {
                var index = groups.FindIndex(g => g.Name == tag.Name && g.Description == tag.Description);
                if (index < 0)
                {
                    groups.Add((tag.Name, tag.Description, new JsonArray()));
                    index = groups.Count - 1;
                }

                groups[index].Tools.Add(param);
            }
            else
            {
                result.Add(param);
            }
        }

        foreach (var (name, description, groupTools) in groups)
        {
            result.Add(new JsonObject
            {
                ["type"] = "namespace",
                ["name"] = name,
                ["description"] = description,
                ["tools"] = groupTools,
            });
        }

        return result;
    }

    /// <summary>
    /// Tool name to namespace name, for every tool tagged with <see cref="NamespaceOption"/> (the last tag wins).
    /// Replayed <c>function_call</c> items take their <c>namespace</c> from this map.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Namespaces(IReadOnlyList<ToolInfo> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var namespaces = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (NamespaceTag(tool) is { } tag)
            {
                namespaces[tool.Name] = tag.Name;
            }
        }

        return namespaces;
    }

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

    private static bool IsFunctionParam(JsonObject param) =>
        param["type"] is JsonValue type && type.TryGetValue<string>(out var value) && value == "function";

    /// <summary>The <c>[name, description]</c> namespace tag of a tool: a JSON array of exactly two strings, else null.</summary>
    private static (string Name, string Description)? NamespaceTag(ToolInfo tool) =>
        tool.Options?[NamespaceOption] is JsonArray { Count: 2 } tag
        && tag[0] is JsonValue nameValue && nameValue.TryGetValue<string>(out var name)
        && tag[1] is JsonValue descriptionValue && descriptionValue.TryGetValue<string>(out var description)
            ? (name, description)
            : null;
}

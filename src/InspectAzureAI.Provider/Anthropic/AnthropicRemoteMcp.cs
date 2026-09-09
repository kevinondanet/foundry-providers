using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Provider.Anthropic;

/// <summary>
/// Provider-side execution of remote MCP servers on the Anthropic Messages route (port of the remote MCP handling
/// of <c>model/_providers/anthropic.py</c>: <c>partition_tools</c>, <c>mcp_server_param</c>, the <c>mcp_servers</c>
/// request wiring and the <c>mcp_tool_use</c> / <c>mcp_tool_result</c> content handling). A server created with
/// <c>execution="remote"</c> reaches the provider as a marker tool named <c>mcp_server_{name}</c> whose
/// <c>options</c> are the <c>MCPServerConfigHTTP</c> dump (<c>tool/_mcp/_remote.py</c>). The provider strips the
/// markers out of <c>tools</c>, sends them as <c>mcp_servers</c> under the <see cref="Beta"/> header, and folds
/// each response <c>mcp_tool_use</c> / <c>mcp_tool_result</c> pair into a <see cref="ContentToolUse"/> of type
/// <c>mcp_call</c>, replayed as the same two blocks on later turns (the way web search results are).
/// Deviation: Python replays the exact blocks it recorded in the assistant message's <c>internal</c> field and only
/// reconstructs foreign results; this port has no such field and always reconstructs, which for MCP carries the
/// same fields. Deviation: the Messages API has since added the <c>mcp_toolset</c> tool entry and the
/// <c>mcp-client-2025-11-20</c> beta; the Python shape ported here (<c>tool_configuration</c> on the server
/// definition) is the deprecated-but-accepted form, and the <c>anthropic_beta</c> model arg can add the newer beta.
/// </summary>
public static class AnthropicRemoteMcp
{
    /// <summary>The beta Python appends when the request carries MCP servers.</summary>
    public const string Beta = "mcp-client-2025-04-04";

    /// <summary>Name prefix of the marker tool (<c>mcp_server_tool</c> in <c>tool/_mcp/_remote.py</c>).</summary>
    public const string ToolPrefix = "mcp_server_";

    /// <summary>The <see cref="ContentToolUse.ToolType"/> of a provider-executed MCP call.</summary>
    public const string ToolType = "mcp_call";

    /// <summary>Python's message for an <c>mcp_tool_result</c> whose <c>mcp_tool_use</c> was not seen.</summary>
    public const string OrphanResultError = "MCPToolResultBlock without previous MCPToolUseBlock";

    /// <summary>
    /// Port of <c>is_mcp_server_tool</c>: whether a tool description is a remote MCP server marker (the name starts
    /// with <see cref="ToolPrefix"/> and the tool carries options). The Eval layer's <c>McpServerRemote</c> applies
    /// the same rule; it is repeated here because the provider cannot depend on that project.
    /// </summary>
    public static bool IsMcpServerTool(ToolInfo tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return tool.Name.StartsWith(ToolPrefix, StringComparison.Ordinal) && tool.Options is not null;
    }

    /// <summary>
    /// Port of <c>partition_tools</c>: the tools that go on the wire as function tools, and the server configs
    /// (the marker tools' options, validated as <c>MCPServerConfigHTTP</c>) that go in <c>mcp_servers</c>.
    /// </summary>
    public static (IReadOnlyList<ToolInfo> Tools, IReadOnlyList<JsonObject> McpServers) PartitionTools(IReadOnlyList<ToolInfo> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var standard = new List<ToolInfo>();
        var servers = new List<JsonObject>();
        foreach (var tool in tools)
        {
            if (IsMcpServerTool(tool))
            {
                servers.Add(ValidateConfig(tool.Options!));
            }
            else
            {
                standard.Add(tool);
            }
        }

        return (standard, servers);
    }

    /// <summary>
    /// Port of <c>MCPServerConfigHTTP.model_validate</c> for the marker options: <c>type</c> must be <c>http</c> or
    /// <c>sse</c>, <c>name</c> and <c>url</c> non-empty strings, <c>tools</c> <c>"all"</c> or a list of strings
    /// (absent counts as all), <c>headers</c> an object or null. The validated object is returned as given.
    /// </summary>
    public static JsonObject ValidateConfig(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var type = config["type"]?.ToString();
        if (type is not ("http" or "sse"))
        {
            throw new ArgumentException($"MCP server config 'type' must be 'http' or 'sse', not '{type ?? "null"}'.", nameof(config));
        }

        if (config["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name) || name.Length == 0)
        {
            throw new ArgumentException("MCP server config requires a non-empty 'name'.", nameof(config));
        }

        if (config["url"] is not JsonValue urlValue || !urlValue.TryGetValue<string>(out var url) || url.Length == 0)
        {
            throw new ArgumentException("MCP server config requires a non-empty 'url'.", nameof(config));
        }

        switch (config["tools"])
        {
            case null:
            case JsonValue all when all.TryGetValue<string>(out var s) && s == "all":
            case JsonArray list when list.All(item => item is JsonValue v && v.TryGetValue<string>(out _)):
                break;
            default:
                throw new ArgumentException("MCP server config 'tools' must be \"all\" or a list of tool names.", nameof(config));
        }

        if (config["headers"] is not (null or JsonObject))
        {
            throw new ArgumentException("MCP server config 'headers' must be an object or null.", nameof(config));
        }

        return config;
    }

    /// <summary>
    /// Port of <c>MCPServerConfigHTTP.authorization_token</c>: the <c>Authorization</c> header with a
    /// <c>Bearer </c> prefix (any case) removed, or null when the config has no such header.
    /// </summary>
    public static string? AuthorizationToken(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config["headers"] is not JsonObject headers || headers["Authorization"] is not JsonValue value)
        {
            return null;
        }

        var authorization = value.ToString();
        return authorization.StartsWith("BEARER ", StringComparison.OrdinalIgnoreCase) ? authorization[7..] : authorization;
    }

    /// <summary>
    /// Port of <c>mcp_server_param</c>: the <c>mcp_servers</c> entry for a server config, <c>{name, type: "url", url,
    /// authorization_token, tool_configuration: {enabled: true, allowed_tools}}</c>; <c>authorization_token</c> is
    /// left out when the config has none and <c>tool_configuration</c> when the config allows all tools (Python
    /// passes None for both, which the SDK would serialise as null).
    /// </summary>
    public static JsonObject McpServerParam(JsonObject config)
    {
        var validated = ValidateConfig(config);
        var param = new JsonObject
        {
            ["name"] = validated["name"]!.ToString(),
            ["type"] = "url",
            ["url"] = validated["url"]!.ToString(),
        };
        if (AuthorizationToken(validated) is { } token)
        {
            param["authorization_token"] = token;
        }

        if (validated["tools"] is JsonArray allowed)
        {
            param["tool_configuration"] = new JsonObject
            {
                ["enabled"] = true,
                ["allowed_tools"] = new JsonArray(allowed.Select(t => (JsonNode?)JsonValue.Create(t!.ToString())).ToArray()),
            };
        }

        return param;
    }

    /// <summary>
    /// An <c>mcp_tool_use</c> block and its <c>mcp_tool_result</c> as one <see cref="ContentToolUse"/> (port of the
    /// <c>mcp_tool_result</c> branch of <c>model_output_from_message</c>): id from the result's <c>tool_use_id</c>,
    /// the use's <c>name</c> and <c>server_name</c> (as <see cref="ContentToolUse.Context"/>), the input as
    /// <c>to_json_str_safe</c> JSON (indent 2, nulls dropped), the result content verbatim when it is a string or as
    /// the same JSON of its blocks otherwise, and the error <c>"error"</c> when the result <c>is_error</c>.
    /// </summary>
    public static ContentToolUse ToContentToolUse(JsonObject mcpToolUse, JsonObject mcpToolResult)
    {
        ArgumentNullException.ThrowIfNull(mcpToolUse);
        ArgumentNullException.ThrowIfNull(mcpToolResult);
        var content = mcpToolResult["content"];
        var result = content is JsonValue text && text.TryGetValue<string>(out var s)
            ? s
            : JsonSafe(content is JsonArray blocks ? blocks : new JsonArray());
        var isError = mcpToolResult["is_error"] is JsonValue flag && flag.TryGetValue<bool>(out var b) && b;
        return new ContentToolUse(
            ToolType,
            mcpToolResult["tool_use_id"]?.ToString() ?? "",
            mcpToolUse["name"]?.ToString() ?? "",
            JsonSafe(mcpToolUse["input"] ?? new JsonObject()),
            result)
        {
            Context = mcpToolUse["server_name"]?.ToString(),
            Error = isError ? "error" : null,
        };
    }

    /// <summary>
    /// The blocks that replay an <c>mcp_call</c> on a later turn (port of the <c>mcp_call</c> branch of the
    /// <c>ContentToolUse</c> reconstruction in <c>anthropic.py</c>): <c>mcp_tool_use</c> with the parsed arguments,
    /// name and <c>server_name</c> (the context, or empty), and <c>mcp_tool_result</c> whose content is the result
    /// parsed as a string or a list of text blocks when it is valid JSON of that shape, otherwise the raw result
    /// text (a result from another system), with <c>is_error</c> when the content carries an error.
    /// </summary>
    public static IReadOnlyList<JsonObject> ReplayBlocks(ContentToolUse content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.ToolType != ToolType)
        {
            throw new ArgumentException($"Expected an '{ToolType}' tool use, got '{content.ToolType}'.", nameof(content));
        }

        JsonNode input;
        try
        {
            input = PythonJson.Loads(content.Arguments) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            input = new JsonObject();
        }

        return
        [
            new JsonObject
            {
                ["id"] = content.Id,
                ["input"] = input,
                ["name"] = content.Name,
                ["server_name"] = content.Context ?? "",
                ["type"] = "mcp_tool_use",
            },
            new JsonObject
            {
                ["tool_use_id"] = content.Id,
                ["type"] = "mcp_tool_result",
                ["content"] = ResultContent(content.Result),
                ["is_error"] = content.Error is { Length: > 0 },
            },
        ];
    }

    /// <summary>Port of <c>beta_text_block_param_adapter.validate_json</c> with the raw-string fallback.</summary>
    private static JsonNode ResultContent(string result)
    {
        try
        {
            switch (PythonJson.Loads(result))
            {
                case JsonValue value when value.TryGetValue<string>(out var text):
                    return JsonValue.Create(text)!;
                case JsonArray blocks when blocks.All(IsTextBlockParam):
                    return blocks.DeepClone();
            }
        }
        catch (JsonException)
        {
        }

        return JsonValue.Create(result)!;
    }

    private static bool IsTextBlockParam(JsonNode? node) =>
        node is JsonObject block
        && block["type"]?.ToString() == "text"
        && block["text"] is JsonValue text && text.TryGetValue<string>(out _);

    /// <summary>Port of <c>to_json_str_safe</c>: <c>to_json(indent=2, exclude_none=True)</c>.</summary>
    private static string JsonSafe(JsonNode? node) => PythonJson.Dumps(WithoutNulls(node), indent: 2);

    private static JsonNode? WithoutNulls(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Where(kv => kv.Value is not null).Select(kv => KeyValuePair.Create(kv.Key, WithoutNulls(kv.Value)))),
        JsonArray array => new JsonArray(array.Select(WithoutNulls).ToArray()),
        _ => node?.DeepClone(),
    };
}

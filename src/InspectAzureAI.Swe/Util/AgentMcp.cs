using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Swe.Util;

/// <summary>
/// MCP server configuration rendering shared by the CLI agents: the Claude Code <c>--mcp-config</c> JSON and
/// allow rules (port of inspect_swe <c>_claude_code/claude_code.py</c> <c>resolve_mcp_servers</c>,
/// <c>resolve_allowed_mcp_tools</c> and <c>resolve_mcp_server_allowed_tools</c>) and the Codex CLI
/// <c>[mcp_servers.&lt;name&gt;]</c> table (<c>_codex_cli/codex_cli.py</c>, rendered for Codex's real schema).
/// </summary>
public static class AgentMcp
{
    /// <summary>
    /// Port of <c>model_dump(exclude={"name","tools"}, exclude_none=True)</c> in pydantic field order: stdio gives
    /// <c>type</c>, <c>command</c>, <c>args</c> (always present), then <c>cwd</c> and <c>env</c> when set; http and sse
    /// give <c>type</c>, <c>url</c>, then <c>headers</c> when set.
    /// </summary>
    public static JsonObject ServerJson(McpServerConfig server)
    {
        ArgumentNullException.ThrowIfNull(server);
        switch (server)
        {
            case McpServerConfigStdio stdio:
            {
                var json = new JsonObject
                {
                    ["type"] = stdio.Type,
                    ["command"] = stdio.Command,
                    ["args"] = new JsonArray(stdio.Args.Select(arg => (JsonNode?)JsonValue.Create(arg)).ToArray()),
                };
                if (stdio.Cwd is not null)
                {
                    json["cwd"] = stdio.Cwd;
                }

                if (stdio.Env is not null)
                {
                    json["env"] = StringObject(stdio.Env);
                }

                return json;
            }

            case McpServerConfigHttp http:
            {
                var json = new JsonObject { ["type"] = http.Type, ["url"] = http.Url };
                if (http.Headers is not null)
                {
                    json["headers"] = StringObject(http.Headers);
                }

                return json;
            }

            default:
                throw new ArgumentException($"Unsupported MCP server config type {server.GetType().Name}.", nameof(server));
        }
    }

    /// <summary>
    /// The Claude Code MCP configuration <c>{"mcpServers": {name: ServerJson}}</c>. Servers are keyed by name, so a
    /// later server with the same name replaces the earlier one in its original position (Python dict semantics).
    /// </summary>
    public static JsonObject ClaudeMcpConfig(IEnumerable<McpServerConfig> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        var byName = new JsonObject();
        foreach (var server in servers)
        {
            byName[server.Name] = ServerJson(server);
        }

        return new JsonObject { ["mcpServers"] = byName };
    }

    /// <summary>Port of <c>resolve_mcp_server_allowed_tools</c>: <c>mcp__{name}__*</c> for a server exposing all tools, otherwise <c>mcp__{name}__{tool}</c> per listed tool.</summary>
    public static IReadOnlyList<string> ServerAllowedTools(IEnumerable<McpServerConfig> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        var allowed = new List<string>();
        foreach (var server in servers)
        {
            if (server.Tools is null)
            {
                allowed.Add($"mcp__{server.Name}__*");
            }
            else
            {
                allowed.AddRange(server.Tools.Select(tool => $"mcp__{server.Name}__{tool}"));
            }
        }

        return allowed;
    }

    /// <summary>Port of <c>resolve_allowed_mcp_tools</c>: the static servers' rules only when <paramref name="allowlistStatic"/>, then the bridged servers' rules, always.</summary>
    public static IReadOnlyList<string> AllowedMcpTools(IReadOnlyList<McpServerConfig> staticServers, IReadOnlyList<McpServerConfig> bridgedServers, bool allowlistStatic)
    {
        ArgumentNullException.ThrowIfNull(staticServers);
        ArgumentNullException.ThrowIfNull(bridgedServers);
        var allowed = new List<string>();
        if (allowlistStatic)
        {
            allowed.AddRange(ServerAllowedTools(staticServers));
        }

        allowed.AddRange(ServerAllowedTools(bridgedServers));
        return allowed;
    }

    /// <summary>
    /// The body of a Codex <c>[mcp_servers.&lt;name&gt;]</c> table for <see cref="Toml"/> (deviation D-S3: Python dumps
    /// the pydantic model, whose nested maps its TOML writer drops and whose <c>headers</c> Codex ignores). Stdio
    /// gives <c>command</c>, <c>args</c>, <c>cwd</c> when set and <c>env</c> as an inline table when set; http gives
    /// <c>url</c>, then <c>http_headers</c> as an inline table when any headers remain, then
    /// <c>bearer_token_env_var</c> when <paramref name="bearerTokenEnvVar"/> is set, in which case an
    /// <c>Authorization</c> header (any case) is left out so the token never lands on disk. There is no <c>type</c>
    /// key; Codex has no SSE transport, so an <c>sse</c> server warns once and is written as streamable HTTP.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, object?>> CodexServerTable(McpServerConfig server, string? bearerTokenEnvVar = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        var table = new List<KeyValuePair<string, object?>>();
        switch (server)
        {
            case McpServerConfigStdio stdio:
                table.Add(Entry("command", stdio.Command));
                table.Add(Entry("args", stdio.Args.ToList()));
                if (stdio.Cwd is not null)
                {
                    table.Add(Entry("cwd", stdio.Cwd));
                }

                if (stdio.Env is not null)
                {
                    table.Add(Entry("env", InlineTable(stdio.Env)));
                }

                break;

            case McpServerConfigHttp http:
                if (http.Type == "sse")
                {
                    ProviderLogger.WarnOnce($"Codex CLI has no SSE transport: MCP server '{http.Name}' is written as a streamable HTTP server.");
                }

                table.Add(Entry("url", http.Url));
                var headers = (http.Headers ?? new Dictionary<string, string>())
                    .Where(header => bearerTokenEnvVar is null || !string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (headers.Count > 0)
                {
                    table.Add(Entry("http_headers", InlineTable(headers)));
                }

                if (bearerTokenEnvVar is not null)
                {
                    table.Add(Entry("bearer_token_env_var", bearerTokenEnvVar));
                }

                break;

            default:
                throw new ArgumentException($"Unsupported MCP server config type {server.GetType().Name}.", nameof(server));
        }

        return table;
    }

    private static KeyValuePair<string, object?> Entry(string key, object? value) => KeyValuePair.Create(key, value);

    private static List<KeyValuePair<string, object?>> InlineTable(IEnumerable<KeyValuePair<string, string>> values) =>
        values.Select(kv => KeyValuePair.Create(kv.Key, (object?)kv.Value)).ToList();

    private static JsonObject StringObject(IReadOnlyDictionary<string, string> values) =>
        new(values.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value))));
}

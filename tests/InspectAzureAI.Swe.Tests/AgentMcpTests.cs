using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Provider.Util;
using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.Swe.Tests;

/// <summary>
/// <see cref="AgentMcp"/>: the four cases of inspect_swe <c>tests/test_claude_code_mcp_allowed_tools.py</c>, the
/// <c>model_dump</c> shapes of the Claude Code MCP config, and the Codex <c>[mcp_servers.&lt;name&gt;]</c> table.
/// </summary>
public class AgentMcpTests
{
    private static McpServerConfigHttp Http(string name, IReadOnlyList<string>? tools = null) =>
        new("http", name, $"http://{name}.test/mcp") { Tools = tools };

    [Fact]
    public void default_registers_and_allowlists_explicit_mcp_tools()
    {
        var server = Http("taiga-mcp", ["list_files", "read_file"]);

        var config = AgentMcp.ClaudeMcpConfig([server]);

        Assert.NotNull(config["mcpServers"]!["taiga-mcp"]);
        Assert.Equal(["mcp__taiga-mcp__list_files", "mcp__taiga-mcp__read_file"], AgentMcp.ServerAllowedTools([server]));
    }

    [Fact]
    public void disabling_static_mcp_allowlist_keeps_servers_registered()
    {
        var server = Http("taiga-mcp", ["list_files", "read_file"]);

        var config = AgentMcp.ClaudeMcpConfig([server]);
        var allowed = AgentMcp.AllowedMcpTools([server], [], allowlistStatic: false);

        Assert.NotNull(config["mcpServers"]!["taiga-mcp"]);
        Assert.Empty(allowed);
    }

    [Fact]
    public void disabling_static_mcp_allowlist_preserves_bridged_allowlist()
    {
        var staticServer = Http("caller-tools", ["lookup"]);
        var bridgedServer = Http("inspect-tools", ["submit"]);

        var allowed = AgentMcp.AllowedMcpTools([staticServer], [bridgedServer], allowlistStatic: false);

        Assert.Equal(["mcp__inspect-tools__submit"], allowed);
    }

    [Fact]
    public void all_tools_wildcard_uses_double_underscore_before_glob()
    {
        var server = Http("taiga-mcp");

        Assert.NotNull(AgentMcp.ClaudeMcpConfig([server])["mcpServers"]!["taiga-mcp"]);
        Assert.Equal(["mcp__taiga-mcp__*"], AgentMcp.ServerAllowedTools([server]));
    }

    [Fact]
    public void allowlisted_static_rules_precede_bridged_rules()
    {
        var allowed = AgentMcp.AllowedMcpTools([Http("caller-tools", ["lookup"]), Http("docs")], [Http("secrets")], allowlistStatic: true);

        Assert.Equal(["mcp__caller-tools__lookup", "mcp__docs__*", "mcp__secrets__*"], allowed);
    }

    [Fact]
    public void server_json_follows_pydantic_field_order_and_drops_nulls()
    {
        var minimalStdio = new McpServerConfigStdio("fs", "npx");
        var fullStdio = new McpServerConfigStdio("fs", "npx")
        {
            Args = ["-y", "@mcp/fs"],
            Cwd = "/w",
            Env = new Dictionary<string, string> { ["TOKEN"] = "t" },
            Tools = ["read"],
        };
        var sse = new McpServerConfigHttp("sse", "events", "http://events.test/sse", new Dictionary<string, string> { ["Authorization"] = "Bearer t" });

        Assert.Equal("""{"type":"stdio","command":"npx","args":[]}""", AgentMcp.ServerJson(minimalStdio).ToJsonString());
        Assert.Equal("""{"type":"stdio","command":"npx","args":["-y","@mcp/fs"],"cwd":"/w","env":{"TOKEN":"t"}}""", AgentMcp.ServerJson(fullStdio).ToJsonString());
        Assert.Equal("""{"type":"http","url":"http://docs.test/mcp"}""", AgentMcp.ServerJson(Http("docs", ["x"])).ToJsonString());
        Assert.Equal("""{"type":"sse","url":"http://events.test/sse","headers":{"Authorization":"Bearer t"}}""", AgentMcp.ServerJson(sse).ToJsonString());
    }

    [Fact]
    public void claude_config_keys_servers_by_name_with_later_duplicates_replacing_in_place()
    {
        var config = AgentMcp.ClaudeMcpConfig([
            new McpServerConfigHttp("http", "a", "http://first.test/mcp"),
            Http("b"),
            new McpServerConfigHttp("http", "a", "http://second.test/mcp"),
        ]);

        var servers = config["mcpServers"]!.AsObject();
        Assert.Equal(["a", "b"], servers.Select(kv => kv.Key));
        Assert.Equal("http://second.test/mcp", servers["a"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void codex_stdio_table_writes_env_as_an_inline_table()
    {
        var server = new McpServerConfigStdio("fs", "npx")
        {
            Args = ["-y", "@mcp/fs"],
            Cwd = "/w",
            Env = new Dictionary<string, string> { ["TOKEN"] = "t" },
        };

        var toml = Toml.Write([new(Toml.TablePath("mcp_servers", "fs"), AgentMcp.CodexServerTable(server, bearerTokenEnvVar: "IGNORED"))]);

        Assert.Equal("[mcp_servers.fs]\ncommand = \"npx\"\nargs = [\"-y\", \"@mcp/fs\"]\ncwd = \"/w\"\nenv = { TOKEN = \"t\" }", toml);
    }

    [Fact]
    public void codex_http_table_swaps_authorization_for_bearer_token_env_var()
    {
        var server = new McpServerConfigHttp("http", "secrets", "http://127.0.0.1:5555/mcp/secrets", new Dictionary<string, string>
        {
            ["authorization"] = "Bearer tok",
            ["X-Trace"] = "on",
        });

        var table = AgentMcp.CodexServerTable(server, bearerTokenEnvVar: "OPENAI_API_KEY");
        var toml = Toml.Write([new(Toml.TablePath("mcp_servers", "secrets"), table)]);

        Assert.Equal(["url", "http_headers", "bearer_token_env_var"], table.Select(e => e.Key));
        Assert.Equal("[mcp_servers.secrets]\nurl = \"http://127.0.0.1:5555/mcp/secrets\"\nhttp_headers = { X-Trace = \"on\" }\nbearer_token_env_var = \"OPENAI_API_KEY\"", toml);
        Assert.DoesNotContain("Bearer tok", toml, StringComparison.Ordinal);
        Assert.DoesNotContain("uthorization", toml, StringComparison.Ordinal);

        var tokenOnly = AgentMcp.CodexServerTable(new McpServerConfigHttp("http", "s", "http://h.test/mcp/s", new Dictionary<string, string> { ["Authorization"] = "Bearer tok" }), "OPENAI_API_KEY");
        Assert.Equal(["url", "bearer_token_env_var"], tokenOnly.Select(e => e.Key));
    }

    [Fact]
    public void codex_http_table_without_a_bearer_variable_keeps_every_header()
    {
        var server = new McpServerConfigHttp("http", "remote", "https://remote.test/mcp", new Dictionary<string, string> { ["Authorization"] = "Bearer tok" });

        var table = AgentMcp.CodexServerTable(server);

        Assert.Equal(["url", "http_headers"], table.Select(e => e.Key));
        Assert.DoesNotContain(table, e => e.Key == "type");
        Assert.Equal(
            "[mcp_servers.remote]\nurl = \"https://remote.test/mcp\"\nhttp_headers = { Authorization = \"Bearer tok\" }",
            Toml.Write([new(Toml.TablePath("mcp_servers", "remote"), table)]));
        Assert.Equal(["url"], AgentMcp.CodexServerTable(Http("bare")).Select(e => e.Key));
    }

    [Fact]
    public void codex_sse_server_warns_once_and_is_written_as_streamable_http()
    {
        ProviderLogger.Reset();
        var server = new McpServerConfigHttp("sse", "events", "http://events.test/sse");

        var first = AgentMcp.CodexServerTable(server);
        AgentMcp.CodexServerTable(server);

        Assert.Equal(["url"], first.Select(e => e.Key));
        var warning = Assert.Single(ProviderLogger.Warnings);
        Assert.Contains("'events'", warning, StringComparison.Ordinal);
        Assert.Contains("streamable HTTP", warning, StringComparison.Ordinal);
    }
}

using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.Swe.Tests;

/// <summary>
/// <see cref="Toml"/>: the port of <c>_util/toml.py</c> (ordering, escapes, numbers, lists, null) plus inline tables
/// and quoted keys, and a round trip through the test reader <see cref="MiniToml"/>.
/// </summary>
public class TomlTests
{
    private const string GoldenConfig = """
        web_search = "live"
        features.goals = true
        preferred_auth_method = "apikey"
        model_provider = "openai-proxy"

        [analytics]
        enabled = false

        [model_providers.openai-proxy]
        name = "OpenAI Proxy"
        base_url = "http://host.docker.internal:54321/v1"
        env_key = "OPENAI_API_KEY"
        wire_api = "responses"
        stream_idle_timeout_ms = 3600000
        """;

    /// <summary>The document <c>codex_cli.py:311-340</c> builds, in Python's insertion order.</summary>
    private static List<KeyValuePair<string, object?>> CodexDocument() =>
    [
        new("analytics", new Dictionary<string, object?> { ["enabled"] = false }),
        new("web_search", "live"),
        new("features.goals", true),
        new("preferred_auth_method", "apikey"),
        new("model_provider", "openai-proxy"),
        new("model_providers.openai-proxy", new Dictionary<string, object?>
        {
            ["name"] = "OpenAI Proxy",
            ["base_url"] = "http://host.docker.internal:54321/v1",
            ["env_key"] = "OPENAI_API_KEY",
            ["wire_api"] = "responses",
            ["stream_idle_timeout_ms"] = 3_600_000L,
        }),
    ];

    [Fact]
    public void writes_top_level_values_before_tables_like_python()
    {
        Assert.Equal(GoldenConfig, Toml.Write(CodexDocument()));
    }

    [Fact]
    public void golden_config_round_trips_through_mini_toml()
    {
        var tables = MiniToml.Parse(Toml.Write(CodexDocument()));

        Assert.Equal(["", "analytics", "model_providers.openai-proxy"], tables.Keys);
        Assert.Equal("live", tables[""]["web_search"]!.GetValue<string>());
        Assert.True(tables[""]["features.goals"]!.GetValue<bool>());
        Assert.Equal("openai-proxy", tables[""]["model_provider"]!.GetValue<string>());
        Assert.False(tables["analytics"]["enabled"]!.GetValue<bool>());
        var provider = tables["model_providers.openai-proxy"];
        Assert.Equal("http://host.docker.internal:54321/v1", provider["base_url"]!.GetValue<string>());
        Assert.Equal("responses", provider["wire_api"]!.GetValue<string>());
        Assert.Equal(3_600_000L, provider["stream_idle_timeout_ms"]!.GetValue<long>());
    }

    [Fact]
    public void mcp_server_table_with_inline_headers_round_trips()
    {
        var server = new McpServerConfigHttp("http", "secrets", "http://127.0.0.1:5555/mcp/secrets", new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer tok",
            ["X-Trace"] = "on",
        });
        var document = new List<KeyValuePair<string, object?>>
        {
            new("model_provider", "openai-proxy"),
            new(Toml.TablePath("mcp_servers", "secrets"), AgentMcp.CodexServerTable(server)),
            new(Toml.TablePath("mcp_servers", "my server"), AgentMcp.CodexServerTable(new McpServerConfigStdio("my server", "npx") { Args = ["-y", "srv"] })),
        };

        var text = Toml.Write(document);

        Assert.Equal(
            """
            model_provider = "openai-proxy"

            [mcp_servers.secrets]
            url = "http://127.0.0.1:5555/mcp/secrets"
            http_headers = { Authorization = "Bearer tok", X-Trace = "on" }

            [mcp_servers."my server"]
            command = "npx"
            args = ["-y", "srv"]
            """,
            text);
        var tables = MiniToml.Parse(text);
        var headers = Assert.IsType<JsonObject>(tables["mcp_servers.secrets"]["http_headers"]);
        Assert.Equal("Bearer tok", headers["Authorization"]!.GetValue<string>());
        Assert.Equal("on", headers["X-Trace"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:5555/mcp/secrets", tables["mcp_servers.secrets"]["url"]!.GetValue<string>());
        Assert.Equal(["-y", "srv"], tables["mcp_servers.my server"]["args"]!.AsArray().Select(a => a!.GetValue<string>()));
    }

    [Fact]
    public void strings_escape_quotes_backslashes_whitespace_and_control_characters()
    {
        const string input = "q\"b\\s\nn\rr\tt";
        const string expectedEscapes = """
            "q\"b\\s\nn\rr\tt"
            """;
        var controls = string.Concat("a", (char)0x01, "b", (char)0x7F, "c", (char)0x1F, "d", (char)0x0B, "e", (char)0x00, "f");
        var expectedControls = string.Concat("\"a", Backslash, "u0001b", Backslash, "u007Fc", Backslash, "u001Fd", Backslash, "u000Be", Backslash, "u0000f\"");

        Assert.Equal(expectedEscapes, Toml.FormatValue(input));
        Assert.Equal(expectedControls, Toml.FormatValue(controls));
        Assert.Equal("\"café\"", Toml.FormatValue("café"));
        Assert.Equal(input, MiniToml.Parse("k = " + Toml.FormatValue(input))[""]["k"]!.GetValue<string>());
        Assert.Equal(controls, MiniToml.Parse("k = " + Toml.FormatValue(controls))[""]["k"]!.GetValue<string>());
    }

    private const char Backslash = (char)0x5C;

    [Fact]
    public void scalars_and_lists_format_like_python()
    {
        Assert.Equal("true", Toml.FormatValue(true));
        Assert.Equal("false", Toml.FormatValue(false));
        Assert.Equal("3", Toml.FormatValue(3));
        Assert.Equal("3600000", Toml.FormatValue(3_600_000L));
        Assert.Equal("-7", Toml.FormatValue((short)-7));
        Assert.Equal("1.5", Toml.FormatValue(1.5));
        Assert.Equal("2.0", Toml.FormatValue(2.0));
        Assert.Equal("0.1", Toml.FormatValue(0.1f));
        Assert.Equal("nan", Toml.FormatValue(double.NaN));
        Assert.Equal("-inf", Toml.FormatValue(double.NegativeInfinity));
        Assert.Equal("[1, \"a\", true]", Toml.FormatValue(new object[] { 1, "a", true }));
        Assert.Equal("[[1, 2], []]", Toml.FormatValue(new object[] { new[] { 1, 2 }, Array.Empty<string>() }));
    }

    [Fact]
    public void nested_maps_are_inline_tables_with_quoted_keys()
    {
        var document = new Dictionary<string, object?>
        {
            ["mcp_servers.fs"] = new Dictionary<string, object?>
            {
                ["env"] = new Dictionary<string, string> { ["PATH"] = "/bin", ["my key"] = "v" },
                ["empty"] = new Dictionary<string, object?>(),
                ["nested"] = new List<KeyValuePair<string, object?>> { new("deep", new Dictionary<string, object?> { ["x"] = 1 }) },
            },
        };

        Assert.Equal(
            "[mcp_servers.fs]\nenv = { PATH = \"/bin\", \"my key\" = \"v\" }\nempty = {}\nnested = { deep = { x = 1 } }",
            Toml.Write(document));
    }

    [Fact]
    public void keys_and_table_paths_are_quoted_only_when_not_bare()
    {
        Assert.Equal("secrets", Toml.Key("secrets"));
        Assert.Equal("inspect-tools_2", Toml.Key("inspect-tools_2"));
        Assert.Equal("\"my.server\"", Toml.Key("my.server"));
        Assert.Equal("\"\"", Toml.Key(""));
        Assert.Equal("mcp_servers.secrets", Toml.TablePath("mcp_servers", "secrets"));
        Assert.Equal("mcp_servers.\"my server\"", Toml.TablePath("mcp_servers", "my server"));
        Assert.Equal("[a.\"b.c\"]\nx = 1", Toml.Write([new(Toml.TablePath("a", "b.c"), new Dictionary<string, object?> { ["x"] = 1 })]));
        Assert.Equal("a.b.c", Assert.Single(MiniToml.Parse("[a.\"b.c\"]\nx = 1").Keys, k => k != ""));
    }

    [Fact]
    public void null_values_throw_like_python()
    {
        var topLevel = Assert.Throws<ArgumentException>(() => Toml.Write([new("k", null)]));
        var inTable = Assert.Throws<ArgumentException>(() => Toml.Write([new("t", new Dictionary<string, object?> { ["k"] = null })]));

        Assert.Equal("TOML doesn't support null values", topLevel.Message);
        Assert.Equal("TOML doesn't support null values", inTable.Message);
        Assert.StartsWith("Unsupported type: System.Object", Assert.Throws<ArgumentException>(() => Toml.FormatValue(new object())).Message);
    }

    [Fact]
    public void later_duplicates_replace_in_place_and_tables_alone_have_no_leading_blank_line()
    {
        var document = new List<KeyValuePair<string, object?>>
        {
            new("t", new Dictionary<string, object?> { ["a"] = 1 }),
            new("u", new Dictionary<string, object?> { ["b"] = 2 }),
            new("t", new Dictionary<string, object?> { ["a"] = 3 }),
        };

        Assert.Equal("[t]\na = 3\n\n[u]\nb = 2", Toml.Write(document));
        Assert.Equal("", Toml.Write([]));
    }
}

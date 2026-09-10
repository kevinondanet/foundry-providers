using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tests;

/// <summary>Port of the grant and naming logic of <c>agent/_bridge/sandbox/types.py</c> (<c>_candidate_functions</c>, grants, <c>_json_equal</c>) and the registration checks of <c>sandbox/bridge.py</c>.</summary>
public class BridgedToolRegistryTests
{
    private static ToolDef Tool(string name) =>
        new(name, $"The {name} tool.", new ToolParams(), (_, _) => Task.FromResult<ToolResult>("ok"));

    private static BridgedToolRegistry Registry(params (string Server, string[] Tools)[] servers) =>
        new(servers.Select(s => new BridgedToolsSpec(s.Server, s.Tools.Select(Tool).ToArray())));

    private static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    private static ToolCall Call(string function, string json = "{}") => new("call_1", function, Args(json));

    [Fact]
    public void candidates_cover_the_python_names_then_copilot_and_codex_forms()
    {
        Assert.Equal(
            ["secret_lookup", "mcp__secrets__secret_lookup", "secrets__secret_lookup", "secrets-secret_lookup"],
            BridgedToolRegistry.CandidateFunctions("secrets", "secret_lookup"));

        Assert.Equal(
            ["submit", "mcp__inspect-tools__submit", "inspect-tools__submit", "inspect-tools-submit", "mcp__inspect_tools__submit", "inspect_tools__submit"],
            BridgedToolRegistry.CandidateFunctions("inspect-tools", "submit"));
    }

    [Fact]
    public void codex_sanitization_replaces_everything_outside_word_characters()
    {
        Assert.Equal("inspect_tools_v1_2", BridgedToolRegistry.SanitizeCodexName("inspect-tools.v1 2"));
        Assert.Equal("already_ok_9", BridgedToolRegistry.SanitizeCodexName("already_ok_9"));
    }

    [Fact]
    public void resolve_finds_every_bridged_tool_a_name_denotes()
    {
        var registry = Registry(("secrets", ["secret_lookup"]), ("files", ["read", "secret_lookup"]));

        Assert.Equal([new BridgedToolId("secrets", "secret_lookup")], registry.Resolve("mcp__secrets__secret_lookup"));
        Assert.Equal([new BridgedToolId("files", "read")], registry.Resolve("files-read"));
        Assert.Equal(2, registry.Resolve("secret_lookup").Count);
        Assert.Empty(registry.Resolve("mcp__other__read"));
        Assert.Equal(["secrets", "files"], registry.Servers);
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void an_ambiguous_name_registers_no_grant_and_warns_once()
    {
        ProviderLogger.Reset();
        var registry = Registry(("a", ["b-c"]), ("a-b", ["c"]));

        registry.RegisterToolExecutionGrants([Call("a-b-c")]);
        registry.RegisterToolExecutionGrants([Call("a-b-c")]);

        Assert.Equal(0, registry.GrantCount);
        var warning = Assert.Single(ProviderLogger.Warnings);
        Assert.StartsWith("Approved tool call 'a-b-c' denotes more than one bridged tool; no execution grant registered", warning, StringComparison.Ordinal);
        Assert.Contains("'<server>-<tool>'", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void duplicate_spec_names_throw_the_python_message()
    {
        var ex = Assert.Throws<ArgumentException>(() => new BridgedToolRegistry([new("secrets", [Tool("a")]), new("secrets", [Tool("b")])]));

        Assert.Equal("Duplicate bridged_tools name: 'secrets'. Each BridgedToolsSpec must have a unique name.", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-leading")]
    [InlineData("has space")]
    [InlineData("dot.ted")]
    [InlineData("slash/ed")]
    [InlineData("trailing\n")]
    public void invalid_spec_names_throw(string name)
    {
        var ex = Assert.Throws<ArgumentException>(() => new BridgedToolRegistry([new(name, [Tool("a")])]));

        Assert.Contains(BridgedToolsSpec.NamePattern, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void duplicate_tool_names_within_a_spec_throw()
    {
        var ex = Assert.Throws<ArgumentException>(() => new BridgedToolRegistry([new("secrets", [Tool("lookup"), Tool("lookup")])]));

        Assert.Contains("Duplicate tool name 'lookup'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("5", "5.0", true)]
    [InlineData("5", "5", true)]
    [InlineData("1e2", "100", true)]
    [InlineData("true", "1", false)]
    [InlineData("false", "0", false)]
    [InlineData("true", "true", true)]
    [InlineData("\"5\"", "5", false)]
    [InlineData("null", "null", true)]
    [InlineData("null", "0", false)]
    [InlineData("{\"a\": 1, \"b\": [1, 2]}", "{\"b\": [1, 2.0], \"a\": 1}", true)]
    [InlineData("{\"a\": 1}", "{\"a\": 1, \"b\": null}", false)]
    [InlineData("[1, 2]", "[2, 1]", false)]
    [InlineData("\"x\"", "\"X\"", false)]
    public void json_equal_follows_json_semantics(string left, string right, bool expected)
    {
        Assert.Equal(expected, BridgedToolRegistry.JsonEqual(JsonNode.Parse(left), JsonNode.Parse(right)));
    }

    [Fact]
    public void json_equal_compares_values_built_in_code_with_parsed_ones()
    {
        Assert.True(BridgedToolRegistry.JsonEqual(new JsonObject { ["n"] = 5, ["s"] = "x", ["b"] = true }, Args("""{"n": 5.0, "s": "x", "b": true}""")));
        Assert.False(BridgedToolRegistry.JsonEqual(new JsonObject { ["b"] = true }, Args("""{"b": 1}""")));
    }

    [Fact]
    public void a_grant_is_consumed_once_and_binds_the_arguments()
    {
        var registry = Registry(("secrets", ["secret_lookup"]));

        registry.RegisterToolExecutionGrants([Call("mcp__secrets__secret_lookup", """{"key": "db", "n": 1}""")]);

        Assert.False(registry.ConsumeToolExecutionGrant("secrets", "secret_lookup", Args("""{"key": "other", "n": 1}""")));
        Assert.True(registry.ConsumeToolExecutionGrant("secrets", "secret_lookup", Args("""{"n": 1.0, "key": "db"}""")));
        Assert.False(registry.ConsumeToolExecutionGrant("secrets", "secret_lookup", Args("""{"key": "db", "n": 1}""")));
    }

    [Fact]
    public void unknown_names_register_nothing()
    {
        var registry = Registry(("secrets", ["secret_lookup"]));

        registry.RegisterToolExecutionGrants([Call("bash"), Call("mcp__other__secret_lookup")]);

        Assert.Equal(0, registry.GrantCount);
    }

    [Fact]
    public void grants_keep_a_copy_of_the_arguments()
    {
        var registry = Registry(("secrets", ["secret_lookup"]));
        var call = Call("secret_lookup", """{"key": "db"}""");

        registry.RegisterToolExecutionGrants([call]);
        call.Arguments["key"] = "changed";

        Assert.True(registry.ConsumeToolExecutionGrant("secrets", "secret_lookup", Args("""{"key": "db"}""")));
    }

    [Fact]
    public void past_the_cap_the_oldest_grant_is_evicted_with_a_warning()
    {
        ProviderLogger.Reset();
        var registry = Registry(("secrets", ["secret_lookup"]));

        registry.RegisterToolExecutionGrants(Enumerable.Range(0, BridgedToolRegistry.MaxToolExecutionGrants + 1)
            .Select(i => Call("secret_lookup", $$"""{"i": {{i}}}""")));

        Assert.Equal(BridgedToolRegistry.MaxToolExecutionGrants, registry.GrantCount);
        Assert.False(registry.ConsumeToolExecutionGrant("secrets", "secret_lookup", Args("""{"i": 0}""")));
        Assert.True(registry.ConsumeToolExecutionGrant("secrets", "secret_lookup", Args("""{"i": 1}""")));
        Assert.True(registry.ConsumeToolExecutionGrant("secrets", "secret_lookup", Args($$"""{"i": {{BridgedToolRegistry.MaxToolExecutionGrants}}}""")));
        Assert.Contains(ProviderLogger.Warnings, w => w.StartsWith("Bridged tool execution grants exceeded 1024; evicting the oldest unconsumed grant.", StringComparison.Ordinal));
    }

    [Fact]
    public void mcp_server_configs_point_at_the_bridge_with_the_bearer_token()
    {
        var registry = Registry(("secrets", ["secret_lookup"]), ("inspect-tools", ["submit"]));

        var configs = registry.McpServerConfigs("http://host.docker.internal:4242", "tok");

        Assert.Equal(["secrets", "inspect-tools"], configs.Select(c => c.Name));
        var first = configs[0];
        Assert.Equal("http", first.Type);
        Assert.Equal("http://host.docker.internal:4242/mcp/secrets", first.Url);
        Assert.Equal("Bearer tok", first.Headers!["Authorization"]);
        Assert.Equal("tok", first.AuthorizationToken);
        Assert.Null(first.Tools);
        Assert.Equal("http://host.docker.internal:4242/mcp/inspect-tools", configs[1].Url);
    }

    [Fact]
    public void the_empty_registry_serves_nothing()
    {
        Assert.Equal(0, BridgedToolRegistry.Empty.Count);
        Assert.Empty(BridgedToolRegistry.Empty.McpServerConfigs("http://x", "t"));
        Assert.False(BridgedToolRegistry.Empty.TryGetServer("secrets", out _));
    }
}

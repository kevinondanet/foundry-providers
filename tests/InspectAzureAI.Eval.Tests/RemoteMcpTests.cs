using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Remote execution of MCP servers (<c>tool/_mcp/_remote.py</c> and the <c>supports_remote_mcp</c> gate of
/// <c>model/_model.py</c>): the marker tool the tool source emits, its translation into the Anthropic
/// <c>mcp_servers</c> entry, and the model layer's rejection for apis that cannot execute it themselves.
/// </summary>
public class RemoteMcpTests
{
    private const string DeepWiki = "https://mcp.deepwiki.com/mcp";

    private static readonly ToolInfo Weather = new("get_weather", "Get weather.");

    private static async Task<ToolInfo> MarkerAsync(IToolSource source) => Assert.Single(await source.ToolsAsync()).ToInfo();

    [Fact]
    public async Task the_remote_marker_is_recognised_by_the_provider_and_partitioned_out_of_the_tools()
    {
        var server = Mcp.McpServerHttp(DeepWiki, name: "deepwiki", execution: McpExecution.Remote, authorization: "k");
        var marker = await MarkerAsync(server);
        var plain = new ToolInfo("mcp_server_deepwiki", "no options");

        var (tools, servers) = AnthropicRemoteMcp.PartitionTools([marker, Weather]);

        Assert.Equal("mcp_server_deepwiki", marker.Name);
        Assert.Equal(McpServerRemote.ToolPrefix, AnthropicRemoteMcp.ToolPrefix);
        Assert.True(AnthropicRemoteMcp.IsMcpServerTool(marker));
        Assert.True(McpServerRemote.IsMcpServerTool(marker));
        Assert.False(AnthropicRemoteMcp.IsMcpServerTool(plain));
        Assert.False(McpServerRemote.IsMcpServerTool(plain));
        Assert.Equal("get_weather", Assert.Single(tools).Name);
        Assert.Equal("deepwiki", Assert.Single(servers)["name"]!.ToString());
    }

    [Fact]
    public async Task mcp_server_param_follows_the_python_shape()
    {
        var server = Mcp.McpServerHttp(DeepWiki, name: "deepwiki", execution: McpExecution.Remote, authorization: "k");
        var sse = Mcp.McpServerSse("https://example.test/sse", name: "s", execution: McpExecution.Remote, headers: new Dictionary<string, string> { ["Authorization"] = "bearer abc" });
        var anonymous = Mcp.McpServerHttp("https://example.test/mcp", name: "a", execution: McpExecution.Remote, headers: new Dictionary<string, string> { ["X-Api-Key"] = "z" });

        var all = AnthropicRemoteMcp.McpServerParam((await MarkerAsync(server)).Options!);
        var filtered = AnthropicRemoteMcp.McpServerParam((await MarkerAsync(Mcp.McpTools(server, ["read_wiki_structure", "ask_*"]))).Options!);
        var sseOptions = (await MarkerAsync(sse)).Options!;
        var anonymousOptions = (await MarkerAsync(anonymous)).Options!;

        Assert.Equal("{\"name\":\"deepwiki\",\"type\":\"url\",\"url\":\"https://mcp.deepwiki.com/mcp\",\"authorization_token\":\"k\"}", all.ToJsonString());
        Assert.Equal("{\"enabled\":true,\"allowed_tools\":[\"read_wiki_structure\",\"ask_*\"]}", filtered["tool_configuration"]!.ToJsonString());
        Assert.Equal("k", filtered["authorization_token"]!.ToString());
        Assert.Equal("abc", AnthropicRemoteMcp.AuthorizationToken(sseOptions));
        Assert.Equal("url", AnthropicRemoteMcp.McpServerParam(sseOptions)["type"]!.ToString());
        Assert.Null(AnthropicRemoteMcp.AuthorizationToken(anonymousOptions));
        Assert.Null(AnthropicRemoteMcp.McpServerParam(anonymousOptions)["authorization_token"]);
    }

    [Fact]
    public void malformed_marker_options_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => AnthropicRemoteMcp.ValidateConfig(new JsonObject { ["type"] = "stdio", ["name"] = "x", ["url"] = "u" }));
        Assert.Throws<ArgumentException>(() => AnthropicRemoteMcp.ValidateConfig(new JsonObject { ["type"] = "http", ["name"] = "x" }));
        Assert.Throws<ArgumentException>(() => AnthropicRemoteMcp.ValidateConfig(new JsonObject { ["type"] = "http", ["name"] = "", ["url"] = "u" }));
        Assert.Throws<ArgumentException>(() => AnthropicRemoteMcp.ValidateConfig(new JsonObject { ["type"] = "http", ["name"] = "x", ["url"] = "u", ["tools"] = 3 }));
        Assert.Throws<ArgumentException>(() => AnthropicRemoteMcp.ValidateConfig(new JsonObject { ["type"] = "http", ["name"] = "x", ["url"] = "u", ["headers"] = "h" }));
        Assert.NotNull(AnthropicRemoteMcp.ValidateConfig(new JsonObject { ["type"] = "sse", ["name"] = "x", ["url"] = "u" }));
    }

    [Fact]
    public async Task the_model_rejects_remote_mcp_for_an_api_without_support()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("hi"), ScriptedTurn.Text("hi"));
        var model = new Model(api);
        var marker = await MarkerAsync(Mcp.McpServerHttp(DeepWiki, name: "deepwiki", execution: McpExecution.Remote));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => model.GenerateAsync("hi", tools: [marker]));
        // the check precedes the tool_choice filter, as in Python
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.GenerateAsync("hi", tools: [marker, Weather], toolChoice: new ToolFunction("get_weather")));
        await model.GenerateAsync("hi", tools: [Weather]);

        Assert.Equal("Remote MCP execution is not supported for scripted. Please use \"local\" execution instead.", ex.Message);
        Assert.Equal("get_weather", Assert.Single(Assert.Single(api.Requests).Tools).Name);
    }

    [Fact]
    public async Task the_model_passes_the_marker_through_when_the_api_supports_it()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("hi")) { SupportsRemoteMcp = true };
        var model = new Model(api);
        var marker = await MarkerAsync(Mcp.McpServerHttp(DeepWiki, name: "deepwiki", execution: McpExecution.Remote, authorization: "k"));

        await model.GenerateAsync("hi", tools: [marker, Weather]);

        var sent = Assert.Single(api.Requests).Tools;
        Assert.Equal(["mcp_server_deepwiki", "get_weather"], sent.Select(t => t.Name));
        Assert.Equal(DeepWiki, sent[0].Options!["url"]!.ToString());
        Assert.Equal("Bearer k", sent[0].Options!["headers"]!["Authorization"]!.ToString());
    }

    [Fact]
    public void supports_remote_mcp_follows_the_provider_route()
    {
        var credential = new FakeTokenCredential("token");
        using var anthropic = new AnthropicFoundryModelApi("claude-x", "https://example.invalid", settings: new AzureAIClientSettings { TokenCredential = credential });
        var inference = new AzureAIModelApi("gpt-x", "https://example.invalid", settings: new AzureAIClientSettings { TokenCredential = credential });

        Assert.True(ModelApiHooks.SupportsRemoteMcp(anthropic));
        Assert.False(ModelApiHooks.SupportsRemoteMcp(inference));
        Assert.False(ModelApiHooks.SupportsRemoteMcp(new ScriptedModelApi()));
        Assert.True(ModelApiHooks.SupportsRemoteMcp(new ScriptedModelApi { SupportsRemoteMcp = true }));
    }
}

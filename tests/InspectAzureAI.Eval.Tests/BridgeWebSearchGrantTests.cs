using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>The web-search grant (<c>sandbox_agent_bridge(web_search=...)</c>): a scaffold's web search reaches the served model only when granted and natively supported.</summary>
public class BridgeWebSearchGrantTests
{
    private static readonly ToolInfo Bash = new("bash", "Run a command.");

    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    private static ToolInfo AnthropicMarker() =>
        BridgeBuiltinTools.WebSearchTool(Json("""{"type": "web_search_20250305", "name": "web_search", "allowed_domains": ["docs.python.org"], "blocked_domains": ["example.com"], "max_uses": 3, "cache_control": {"type": "ephemeral"}}"""));

    private static Model AnthropicModel(string name) => new(new AnthropicModelApi(name, baseUrl: "http://127.0.0.1:9", apiKey: "test-key"));

    private static Model ScriptedModel() => new(new ScriptedModelApi(ScriptedTurn.Text("ok")));

    [Fact]
    public void granted_on_a_supported_claude_model_the_marker_becomes_anthropic_options()
    {
        ProviderLogger.Reset();
        var model = AnthropicModel("claude-sonnet-4-6");

        var (tools, choice) = BridgeBuiltinTools.ApplyGrants(model, [Bash, AnthropicMarker()], new ToolFunction("web_search"), webSearch: true);

        Assert.Equal(["bash", "web_search"], tools.Select(t => t.Name));
        var search = tools[1];
        Assert.False(BridgeBuiltinTools.IsWebSearchMarker(search));
        Assert.True(AnthropicWebSearch.IsAnthropicWebSearchTool(search));
        Assert.Equal(
            """{"anthropic": {"allowed_domains": ["docs.python.org"], "blocked_domains": ["example.com"], "max_uses": 3}}""",
            PythonJson.Dumps(search.Options));
        Assert.Equal(
            """{"name": "web_search", "type": "web_search_20250305", "allowed_domains": ["docs.python.org"], "blocked_domains": ["example.com"], "max_uses": 3}""",
            PythonJson.Dumps(AnthropicWebSearch.ServerToolParam(search, "claude-sonnet-4-6")));
        Assert.Equal(new ToolFunction("web_search"), choice);
        Assert.Empty(ProviderLogger.Warnings);
    }

    [Fact]
    public void not_granted_the_tool_is_withheld_with_a_warning_and_the_tool_choice_relaxed()
    {
        ProviderLogger.Reset();

        var (tools, choice) = BridgeBuiltinTools.ApplyGrants(AnthropicModel("claude-sonnet-4-6"), [Bash, AnthropicMarker()], new ToolFunction("web_search"), webSearch: false);

        Assert.Equal(["bash"], tools.Select(t => t.Name));
        Assert.Equal(ToolChoice.Auto, choice);
        Assert.Equal([BridgeBuiltinTools.WebSearchNotGrantedWarning], ProviderLogger.Warnings);
    }

    [Fact]
    public void a_non_anthropic_served_model_withholds_the_tool()
    {
        ProviderLogger.Reset();

        var (tools, choice) = BridgeBuiltinTools.ApplyGrants(ScriptedModel(), [AnthropicMarker(), Bash], new ToolFunction("bash"), webSearch: true);

        Assert.Equal(["bash"], tools.Select(t => t.Name));
        Assert.Equal(new ToolFunction("bash"), choice);
        Assert.Equal([BridgeBuiltinTools.WebSearchUnsupportedWarning], ProviderLogger.Warnings);
    }

    [Fact]
    public void a_claude_model_without_server_web_search_withholds_the_tool()
    {
        ProviderLogger.Reset();

        var (tools, choice) = BridgeBuiltinTools.ApplyGrants(AnthropicModel("claude-3-haiku-20240307"), [AnthropicMarker()], new ToolFunction("web_search"), webSearch: true);

        Assert.Empty(tools);
        Assert.Equal(ToolChoice.Auto, choice);
        Assert.Equal([BridgeBuiltinTools.WebSearchUnsupportedWarning], ProviderLogger.Warnings);
    }

    [Fact]
    public void a_responses_web_search_tool_maps_its_filters_and_location()
    {
        var marker = BridgeBuiltinTools.WebSearchTool(Json("""
            {"type": "web_search", "filters": {"allowed_domains": ["learn.microsoft.com"]}, "search_context_size": "low",
             "user_location": {"type": "approximate", "country": "GB", "city": "London"}}
            """));

        var (tools, _) = BridgeBuiltinTools.ApplyGrants(AnthropicModel("claude-opus-4-7"), [marker], ToolChoice.Auto, webSearch: true);

        Assert.Equal(
            """{"anthropic": {"allowed_domains": ["learn.microsoft.com"], "user_location": {"type": "approximate", "country": "GB", "city": "London"}}}""",
            PythonJson.Dumps(Assert.Single(tools).Options));
    }

    [Fact]
    public void without_markers_the_request_is_returned_unchanged()
    {
        IReadOnlyList<ToolInfo> tools = [Bash];
        var choice = new ToolFunction("bash");

        var result = BridgeBuiltinTools.ApplyGrants(ScriptedModel(), tools, choice, webSearch: false);

        Assert.Same(tools, result.Tools);
        Assert.Same(choice, result.ToolChoice);
    }

    [Fact]
    public async Task an_ungranted_bridge_never_shows_the_served_model_the_search()
    {
        ProviderLogger.Reset();
        var api = new ScriptedModelApi(ScriptedTurn.Text("ok"));
        var bridge = new AgentBridge(new AgentState([]), new Model(api));
        var parsed = AnthropicBridgeApi.ParseRequest(Json("""
            {"model": "claude-sonnet-4-6", "max_tokens": 100, "messages": [{"role": "user", "content": "search"}],
             "tools": [{"name": "bash", "description": "Run.", "input_schema": {"type": "object", "properties": {}}}, {"type": "web_search_20250305", "name": "web_search"}],
             "tool_choice": {"type": "tool", "name": "web_search"}}
            """));

        await bridge.GenerateAsync(parsed.Model, parsed.Messages, parsed.Tools, parsed.ToolChoice, parsed.Config);

        // a client tool keeps the list non-empty: with no tools at all the model layer sends tool_choice none
        var request = Assert.Single(api.Requests);
        Assert.Equal(["bash"], request.Tools.Select(t => t.Name));
        Assert.Equal(ToolChoice.Auto, request.ToolChoice);
        Assert.Contains(BridgeBuiltinTools.WebSearchNotGrantedWarning, ProviderLogger.Warnings);
    }

    /// <summary>Records the request body and answers with a canned Messages API response.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<JsonObject> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject());
            const string reply = """
                {"id": "msg_1", "type": "message", "role": "assistant", "model": "claude-sonnet-4-6",
                 "content": [{"type": "text", "text": "searched"}], "stop_reason": "end_turn", "stop_sequence": null,
                 "usage": {"input_tokens": 3, "output_tokens": 2}}
                """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task a_granted_search_reaches_the_anthropic_provider_as_its_server_tool()
    {
        var handler = new CapturingHandler();
        using var api = new AnthropicModelApi("claude-sonnet-4-6", baseUrl: "http://127.0.0.1:9", apiKey: "test-key", settings: new DirectClientSettings { Handler = handler });
        var bridge = new AgentBridge(new AgentState([]), new Model(api, new GenerateConfig { MaxTokens = 1024 }), webSearch: true);
        var parsed = AnthropicBridgeApi.ParseRequest(Json("""
            {"model": "claude-sonnet-4-6", "max_tokens": 100, "messages": [{"role": "user", "content": "search the python docs"}],
             "tools": [{"type": "web_search_20250305", "name": "web_search", "allowed_domains": ["docs.python.org"], "max_uses": 2}]}
            """));

        var output = await bridge.GenerateAsync(parsed.Model, parsed.Messages, parsed.Tools, parsed.ToolChoice, parsed.Config);

        Assert.Equal("searched", output.Completion);
        var body = Assert.Single(handler.Bodies);
        var tool = Assert.Single(body["tools"]!.AsArray())!;
        Assert.Equal("web_search", tool["name"]!.GetValue<string>());
        Assert.Equal("web_search_20250305", tool["type"]!.GetValue<string>());
        Assert.Equal(["docs.python.org"], tool["allowed_domains"]!.AsArray().Select(d => d!.GetValue<string>()));
        Assert.Equal(2, tool["max_uses"]!.GetValue<int>());
    }
}

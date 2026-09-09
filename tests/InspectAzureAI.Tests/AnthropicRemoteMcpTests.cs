using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Tests;

/// <summary>
/// Provider-side execution of remote MCP servers on the Anthropic Messages route: the <c>mcp_servers</c> request
/// wiring and beta header, <c>mcp_tool_use</c> / <c>mcp_tool_result</c> parsing, replay on later turns and streaming.
/// </summary>
public class AnthropicRemoteMcpTests
{
    private const string Inference = "https://res.services.ai.azure.com/models";

    /// <summary>The marker tool <c>McpServerRemote</c> emits for <c>mcp_server_http(..., execution="remote")</c>: the <c>MCPServerConfigHTTP</c> dump as options.</summary>
    private static ToolInfo Marker(string name = "deepwiki", string url = "https://mcp.deepwiki.com/mcp", JsonNode? tools = null, string? authorization = "Bearer k", string type = "http")
    {
        var options = new JsonObject
        {
            ["type"] = type,
            ["name"] = name,
            ["tools"] = tools ?? JsonValue.Create("all"),
            ["url"] = url,
            ["headers"] = authorization is null ? null : new JsonObject { ["Authorization"] = authorization },
        };
        return new ToolInfo("mcp_server_" + name, "mcp_server_" + name) { Options = options };
    }

    private const string McpResponse = """
        {"id":"msg_2","type":"message","role":"assistant","model":"claude-sonnet-4-6","content":[
          {"type":"text","text":"Let me look that up."},
          {"type":"mcp_tool_use","id":"mcptoolu_1","name":"read_wiki_structure","server_name":"deepwiki","input":{"repoName":"UKGovernmentBEIS/inspect_ai"}},
          {"type":"mcp_tool_result","tool_use_id":"mcptoolu_1","is_error":false,"content":[{"type":"text","text":"- README\n- docs","citations":null}]},
          {"type":"text","text":"The repo has a README and docs."}
        ],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":5}}
        """;

    private static (AnthropicFoundryModelApi Api, FakeArmHandler Handler) Build(string body, IReadOnlyDictionary<string, object?>? modelArgs = null)
    {
        var handler = new FakeArmHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        var api = new AnthropicFoundryModelApi("claude-sonnet-4-6", modelArgs: modelArgs, settings: Fixtures.Entra(), handler: handler);
        return (api, handler);
    }

    [Fact]
    public async Task the_request_moves_markers_into_mcp_servers_and_sends_the_beta()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (api, handler) = Build(McpResponse);

        var result = await api.GenerateAsync(
            [new ChatMessageUser("What is in the repo?")],
            [Marker(tools: new JsonArray("read_wiki_structure", "ask_*")), Fixtures.WeatherTool],
            ToolChoice.Auto,
            new GenerateConfig());

        var request = result.Call.Request;
        var tool = Assert.Single(request["tools"]!.AsArray());
        Assert.Equal("get_weather", tool!["name"]!.ToString());
        Assert.Equal("auto", request["tool_choice"]!["type"]!.ToString());
        var server = Assert.Single(request["mcp_servers"]!.AsArray());
        Assert.Equal(
            "{\"name\":\"deepwiki\",\"type\":\"url\",\"url\":\"https://mcp.deepwiki.com/mcp\",\"authorization_token\":\"k\",\"tool_configuration\":{\"enabled\":true,\"allowed_tools\":[\"read_wiki_structure\",\"ask_*\"]}}",
            server!.ToJsonString());
        Assert.Equal("mcp-client-2025-04-04", handler.Requests[0].Headers.GetValues("anthropic-beta").Single());
        Assert.Contains("\"mcp_servers\":[{\"name\":\"deepwiki\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task markers_alone_send_mcp_servers_without_a_tools_array()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (api, handler) = Build(McpResponse);

        var result = await api.GenerateAsync([new ChatMessageUser("hi")], [Marker()], ToolChoice.Auto, new GenerateConfig());

        var request = result.Call.Request;
        Assert.Null(request["tools"]);
        Assert.Null(request["tool_choice"]);
        var server = Assert.Single(request["mcp_servers"]!.AsArray());
        Assert.Equal("{\"name\":\"deepwiki\",\"type\":\"url\",\"url\":\"https://mcp.deepwiki.com/mcp\",\"authorization_token\":\"k\"}", server!.ToJsonString());
        Assert.Equal("mcp-client-2025-04-04", handler.Requests[0].Headers.GetValues("anthropic-beta").Single());
    }

    [Fact]
    public async Task tool_choice_none_sends_neither_servers_nor_the_beta()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (api, handler) = Build(McpResponse);

        var result = await api.GenerateAsync([new ChatMessageUser("hi")], [Marker(), Fixtures.WeatherTool], ToolChoice.None, new GenerateConfig());

        Assert.Null(result.Call.Request["mcp_servers"]);
        Assert.Null(result.Call.Request["tools"]);
        Assert.False(handler.Requests[0].Headers.Contains("anthropic-beta"));
    }

    [Fact]
    public void beta_header_adds_the_mcp_beta_once()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (api, _) = Build(McpResponse);
        var (withArg, _) = Build(McpResponse, new Dictionary<string, object?> { ["anthropic_beta"] = "mcp-client-2025-04-04, foo" });

        Assert.Null(api.BetaHeader(new GenerateConfig()));
        Assert.Equal("mcp-client-2025-04-04", api.BetaHeader(new GenerateConfig(), remoteMcp: true));
        Assert.Equal("mcp-client-2025-04-04,foo", withArg.BetaHeader(new GenerateConfig(), remoteMcp: true));
    }

    [Fact]
    public void the_response_pairs_mcp_tool_use_with_its_result()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (api, _) = Build(McpResponse);

        var output = api.ParseMessage(JsonNode.Parse(McpResponse)!.AsObject());

        var assistant = output.Choices[0].Message;
        Assert.Null(assistant.ToolCalls);
        Assert.Equal(StopReason.Stop, output.Choices[0].StopReason);
        var items = assistant.Content.Items!;
        Assert.Equal(3, items.Count);
        var call = Assert.IsType<ContentToolUse>(items[1]);
        Assert.Equal("mcp_call", call.ToolType);
        Assert.Equal("mcptoolu_1", call.Id);
        Assert.Equal("read_wiki_structure", call.Name);
        Assert.Equal("deepwiki", call.Context);
        Assert.Equal("{\n  \"repoName\": \"UKGovernmentBEIS/inspect_ai\"\n}", call.Arguments);
        Assert.Equal("[\n  {\n    \"type\": \"text\",\n    \"text\": \"- README\\n- docs\"\n  }\n]", call.Result);   // exclude_none drops the null citations
        Assert.Null(call.Error);
        Assert.Equal("Let me look that up.\nThe repo has a README and docs.", assistant.Text);
    }

    [Fact]
    public void an_error_result_and_a_string_result_are_carried_and_an_orphan_is_rejected()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (api, _) = Build(McpResponse);
        var failed = JsonNode.Parse("""
            {"content":[
              {"type":"mcp_tool_use","id":"mcptoolu_2","name":"ask_question","server_name":"deepwiki","input":{}},
              {"type":"mcp_tool_result","tool_use_id":"mcptoolu_2","is_error":true,"content":"boom"}
            ],"stop_reason":"end_turn"}
            """)!.AsObject();
        var orphan = JsonNode.Parse("""{"content":[{"type":"mcp_tool_result","tool_use_id":"mcptoolu_9","is_error":false,"content":[]}],"stop_reason":"end_turn"}""")!.AsObject();

        var call = Assert.IsType<ContentToolUse>(Assert.Single(api.ParseMessage(failed).Choices[0].Message.Content.Items!));
        Assert.Equal("error", call.Error);
        Assert.Equal("boom", call.Result);
        Assert.Equal("{}", call.Arguments);
        var ex = Assert.Throws<ServiceResponseException>(() => api.ParseMessage(orphan));
        Assert.Equal("MCPToolResultBlock without previous MCPToolUseBlock", ex.Message);
    }

    [Fact]
    public async Task the_assistant_turn_replays_the_mcp_blocks()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (api, _) = Build(McpResponse);
        var previous = api.ParseMessage(JsonNode.Parse(McpResponse)!.AsObject()).Choices[0].Message;
        ProviderLogger.Reset();

        var result = await api.GenerateAsync([new ChatMessageUser("What is in the repo?"), previous, new ChatMessageUser("And the docs?")], [Marker()], ToolChoice.Auto, new GenerateConfig());

        var blocks = result.Call.Request["messages"]!.AsArray()[1]!["content"]!.AsArray();
        Assert.Equal(["text", "mcp_tool_use", "mcp_tool_result", "text"], blocks.Select(b => b!["type"]!.ToString()));
        Assert.Equal(
            "{\"id\":\"mcptoolu_1\",\"input\":{\"repoName\":\"UKGovernmentBEIS/inspect_ai\"},\"name\":\"read_wiki_structure\",\"server_name\":\"deepwiki\",\"type\":\"mcp_tool_use\"}",
            blocks[1]!.ToJsonString());
        Assert.Equal(
            "{\"tool_use_id\":\"mcptoolu_1\",\"type\":\"mcp_tool_result\",\"content\":[{\"type\":\"text\",\"text\":\"- README\\n- docs\"}],\"is_error\":false}",
            blocks[2]!.ToJsonString());
        Assert.Empty(ProviderLogger.Warnings);
    }

    [Fact]
    public void replay_reconstructs_foreign_results_and_flags_errors()
    {
        var plain = new ContentToolUse("mcp_call", "mcp_9", "lookup", "{\"q\": 1}", "plain text from another system") { Error = "boom" };
        var quoted = new ContentToolUse("mcp_call", "mcp_10", "lookup", "not json", "\"quoted\"") { Context = "srv" };

        var blocks = AnthropicRemoteMcp.ReplayBlocks(plain);
        var quotedBlocks = AnthropicRemoteMcp.ReplayBlocks(quoted);

        Assert.Equal("{\"id\":\"mcp_9\",\"input\":{\"q\":1},\"name\":\"lookup\",\"server_name\":\"\",\"type\":\"mcp_tool_use\"}", blocks[0].ToJsonString());
        Assert.Equal("{\"tool_use_id\":\"mcp_9\",\"type\":\"mcp_tool_result\",\"content\":\"plain text from another system\",\"is_error\":true}", blocks[1].ToJsonString());
        Assert.Equal("{}", quotedBlocks[0]["input"]!.ToJsonString());
        Assert.Equal("srv", quotedBlocks[0]["server_name"]!.ToString());
        Assert.Equal("quoted", quotedBlocks[1]["content"]!.ToString());
        Assert.False(quotedBlocks[1]["is_error"]!.GetValue<bool>());
        Assert.Throws<ArgumentException>(() => AnthropicRemoteMcp.ReplayBlocks(new ContentToolUse("web_search", "w", "web_search", "{}", "{}")));
    }

    [Fact]
    public async Task streaming_accumulates_the_mcp_tool_use_input()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (api, _) = Build(McpResponse);
        var events = new[]
        {
            """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-4-6","content":[],"stop_reason":null,"usage":{"input_tokens":10,"output_tokens":1}}}""",
            """{"type":"content_block_start","index":0,"content_block":{"type":"mcp_tool_use","id":"mcptoolu_1","name":"read_wiki_structure","server_name":"deepwiki","input":{}}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"repoName\": "}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"\"x/y\"}"}}""",
            """{"type":"content_block_stop","index":0}""",
            """{"type":"content_block_start","index":1,"content_block":{"type":"mcp_tool_result","tool_use_id":"mcptoolu_1","is_error":false,"content":[{"type":"text","text":"ok"}]}}""",
            """{"type":"content_block_stop","index":1}""",
            """{"type":"content_block_start","index":2,"content_block":{"type":"text","text":""}}""",
            """{"type":"content_block_delta","index":2,"delta":{"type":"text_delta","text":"Done."}}""",
            """{"type":"content_block_stop","index":2}""",
            """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":7}}""",
            """{"type":"message_stop"}""",
        };

        var message = await AnthropicFoundryModelApi.AccumulateAsync(Events(events));
        var output = api.ParseMessage(message);

        var items = output.Choices[0].Message.Content.Items!;
        var call = Assert.IsType<ContentToolUse>(items[0]);
        Assert.Equal("mcp_call", call.ToolType);
        Assert.Equal("{\n  \"repoName\": \"x/y\"\n}", call.Arguments);
        Assert.Equal("deepwiki", call.Context);
        Assert.Contains("\"text\": \"ok\"", call.Result);
        Assert.Equal("Done.", Assert.IsType<ContentText>(items[1]).Text);
        Assert.Equal(StopReason.Stop, output.Choices[0].StopReason);
    }

    private static async IAsyncEnumerable<JsonObject> Events(IEnumerable<string> events)
    {
        foreach (var evt in events)
        {
            yield return JsonNode.Parse(evt)!.AsObject();
            await Task.Yield();
        }
    }
}

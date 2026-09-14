using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tests;

/// <summary>The JSON-RPC face of the bridged-tools MCP endpoint (port of the proxy's <c>mcp_endpoint</c> and the service's <c>list_tools</c> / <c>call_tool</c>), without a listener.</summary>
public class BridgedToolsMcpApiTests
{
    private static readonly ToolParams LookupParams = new()
    {
        Properties = new Dictionary<string, ToolParam> { ["key"] = ToolParam.Of("string", "The secret's key.") },
        Required = ["key"],
    };

    private static BridgedToolRegistry Registry(ToolExecute? execute = null, ToolParams? parameters = null) =>
        new([new BridgedToolsSpec("secrets", [new ToolDef("secret_lookup", "Look up a secret.", parameters ?? LookupParams, execute ?? ((args, _) => Task.FromResult<ToolResult>($"value of {args["key"]}")))])]);

    private static JsonNode Rpc(string json) => JsonNode.Parse(json)!;

    private static Task<McpHttpReply> Handle(BridgedToolRegistry registry, string json, string? server = "secrets", IReadOnlyList<ApprovalPolicy>? approval = null) =>
        BridgedToolsMcpApi.HandleAsync(registry, approval, server, Rpc(json), CancellationToken.None);

    private static void AssertError(McpHttpReply reply, JsonNode? id, int code, string message)
    {
        Assert.Equal(200, reply.Status);
        Assert.Equal("2.0", reply.Body!["jsonrpc"]!.GetValue<string>());
        Assert.True(BridgedToolRegistry.JsonEqual(id, reply.Body["id"]), $"id {reply.Body["id"]?.ToJsonString()}");
        Assert.Equal(code, reply.Body["error"]!["code"]!.GetValue<int>());
        Assert.Equal(message, reply.Body["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task initialize_answers_the_proxy_golden()
    {
        var reply = await Handle(Registry(), """{"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-06-18"}}""");

        Assert.Equal(200, reply.Status);
        Assert.Equal(
            """{"jsonrpc": "2.0", "id": 1, "result": {"protocolVersion": "2025-03-26", "capabilities": {"tools": {}}, "serverInfo": {"name": "secrets", "version": "1.0.0"}}}""",
            PythonJson.Dumps(reply.Body));
    }

    [Theory]
    [InlineData("""{"jsonrpc": "2.0", "method": "notifications/initialized"}""")]
    [InlineData("""{"jsonrpc": "2.0", "method": "notifications/cancelled", "params": {"requestId": 3}}""")]
    [InlineData("""{"jsonrpc": "2.0", "method": "tools/list"}""")]
    public async Task notifications_and_messages_without_an_id_are_accepted_without_a_body(string json)
    {
        var reply = await Handle(Registry(), json);

        Assert.Equal(new McpHttpReply(202, null), reply);
    }

    [Fact]
    public async Task ping_answers_an_empty_result()
    {
        var reply = await Handle(Registry(), """{"jsonrpc": "2.0", "id": "p-1", "method": "ping"}""");

        Assert.Equal("""{"jsonrpc": "2.0", "id": "p-1", "result": {}}""", PythonJson.Dumps(reply.Body));
    }

    [Fact]
    public async Task tools_list_carries_the_tool_params_schema()
    {
        var reply = await Handle(Registry(), """{"jsonrpc": "2.0", "id": 2, "method": "tools/list"}""");

        var tool = Assert.Single(reply.Body!["result"]!["tools"]!.AsArray())!;
        Assert.Equal("secret_lookup", tool["name"]!.GetValue<string>());
        Assert.Equal("Look up a secret.", tool["description"]!.GetValue<string>());
        Assert.Equal(PythonJson.Dumps(LookupParams.ToJson()), PythonJson.Dumps(tool["inputSchema"]));
    }

    [Fact]
    public async Task tools_call_runs_the_tool_and_returns_text_content()
    {
        var reply = await Handle(Registry(), """{"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "secret_lookup", "arguments": {"key": "db"}}}""");

        Assert.Equal("""{"jsonrpc": "2.0", "id": 3, "result": {"content": [{"type": "text", "text": "value of db"}]}}""", PythonJson.Dumps(reply.Body));
    }

    [Fact]
    public async Task errors_carry_their_code_message_and_the_request_id()
    {
        var registry = Registry(execute: (_, _) => throw new InvalidOperationException("vault sealed"));

        AssertError(await Handle(registry, """{"jsonrpc": "2.0", "id": 1, "method": "initialize"}""", server: null), null, -32600, "Invalid path: expected /mcp/{server_name}");
        AssertError(await Handle(registry, """[{"jsonrpc": "2.0", "id": 1, "method": "ping"}]"""), null, -32600, "Batch requests are not supported");
        AssertError(await Handle(registry, "\"ping\""), null, -32600, "Invalid request: expected a JSON-RPC message object");
        AssertError(await Handle(registry, """{"jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": {"arguments": {}}}"""), 4, -32602, "Missing 'name' in params");
        AssertError(await Handle(registry, """{"jsonrpc": "2.0", "id": 5, "method": "tools/call", "params": {"name": "secret_lookup", "arguments": [1]}}"""), 5, -32602, "Invalid 'arguments' in params: expected an object");
        AssertError(await Handle(registry, """{"jsonrpc": "2.0", "id": "six", "method": "resources/list"}"""), "six", -32601, "Unknown method: resources/list");
        AssertError(await Handle(registry, """{"jsonrpc": "2.0", "id": 7, "method": "tools/list"}""", server: "other"), 7, -32603, "Unknown bridged tools server: other");
        AssertError(await Handle(registry, """{"jsonrpc": "2.0", "id": 8, "method": "tools/call", "params": {"name": "nope"}}"""), 8, -32603, "Unknown tool 'nope' in server 'secrets'");
        AssertError(await Handle(registry, """{"jsonrpc": "2.0", "id": 9, "method": "tools/call", "params": {"name": "secret_lookup", "arguments": {"key": "db"}}}"""), 9, -32603, "vault sealed");
    }

    [Fact]
    public async Task arguments_are_validated_before_the_tool_runs()
    {
        var ran = 0;
        var registry = Registry(execute: (_, _) =>
        {
            ran++;
            return Task.FromResult<ToolResult>("ran");
        });

        var missing = await Handle(registry, """{"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": "secret_lookup"}}""");
        var wrongType = await Handle(registry, """{"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": "secret_lookup", "arguments": {"key": 5}}}""");

        AssertError(missing, 1, -32603, "Required parameter key not provided to tool call.");
        Assert.Equal(-32603, wrongType.Body!["error"]!["code"]!.GetValue<int>());
        Assert.StartsWith("Found 1 validation errors parsing tool input arguments:", wrongType.Body["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, ran);
    }

    [Fact]
    public async Task under_approval_a_call_without_a_grant_is_denied_with_the_python_message()
    {
        ProviderLogger.Reset();
        var ran = 0;
        var registry = Registry(execute: (_, _) =>
        {
            ran++;
            return Task.FromResult<ToolResult>("ran");
        });
        var approval = new[] { new ApprovalPolicy(Approvers.Auto(), "*") };
        const string request = """{"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": "secret_lookup", "arguments": {"key": "db"}}}""";

        AssertError(await Handle(registry, request, approval: approval), 1, -32603, "Host tool call 'secrets/secret_lookup' was not approved for execution");
        Assert.Contains("Denied host tool call 'secrets/secret_lookup': no approved execution grant matched it.", ProviderLogger.Warnings);

        registry.RegisterToolExecutionGrants([new ToolCall("c1", "mcp__secrets__secret_lookup", new JsonObject { ["key"] = "db" })]);
        Assert.Equal("ran", (await Handle(registry, request, approval: approval)).Body!["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(1, ran);
        Assert.Equal(-32603, (await Handle(registry, request, approval: approval)).Body!["error"]!["code"]!.GetValue<int>());
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task without_approval_no_grant_is_needed()
    {
        var reply = await Handle(Registry(), """{"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": "secret_lookup", "arguments": {"key": "db"}}}""");

        Assert.Equal("value of db", reply.Body!["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task limits_terminations_and_cancellation_propagate()
    {
        var limit = Registry(execute: (_, _) => throw new LimitExceededException("token", "10", 12));
        var terminate = Registry(execute: (_, _) => throw new TerminateSampleException("stop"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var cancelling = Registry(execute: (_, ct) => Task.FromCanceled<ToolResult>(ct));
        const string request = """{"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": "secret_lookup", "arguments": {"key": "db"}}}""";

        await Assert.ThrowsAsync<LimitExceededException>(() => Handle(limit, request));
        await Assert.ThrowsAsync<TerminateSampleException>(() => Handle(terminate, request));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BridgedToolsMcpApi.HandleAsync(cancelling, null, "secrets", Rpc(request), cancelled.Token));
    }

    [Fact]
    public void result_content_maps_text_images_and_other_content()
    {
        Assert.Equal("""[{"type": "text", "text": "plain"}]""", PythonJson.Dumps(BridgedToolsMcpApi.ResultContent("plain")));
        Assert.Equal("""[{"type": "text", "text": ""}]""", PythonJson.Dumps(BridgedToolsMcpApi.ResultContent(ToolResult.Empty)));

        var mixed = BridgedToolsMcpApi.ResultContent(ToolResult.FromContents(
        [
            new ContentText("caption"),
            new ContentImage("data:image/jpeg;base64,AQID"),
            new ContentImage("https://example.test/cat.png"),
            new ContentReasoning("why"),
        ]));

        Assert.Equal(4, mixed.Count);
        Assert.Equal("""{"type": "text", "text": "caption"}""", PythonJson.Dumps(mixed[0]));
        Assert.Equal("""{"type": "image", "data": "AQID", "mimeType": "image/jpeg"}""", PythonJson.Dumps(mixed[1]));
        Assert.Equal("""{"type": "text", "text": "https://example.test/cat.png"}""", PythonJson.Dumps(mixed[2]));
        Assert.Equal("text", mixed[3]!["type"]!.GetValue<string>());
        var other = JsonNode.Parse(mixed[3]!["text"]!.GetValue<string>())!;
        Assert.Equal("reasoning", other["type"]!.GetValue<string>());
        Assert.Equal("why", other["reasoning"]!.GetValue<string>());
    }

    [Fact]
    public void text_only_content_lists_become_text_blocks()
    {
        var content = BridgedToolsMcpApi.ResultContent(ToolResult.FromContents([new ContentText("a"), new ContentText("b")]));

        Assert.Equal("""[{"type": "text", "text": "a"}, {"type": "text", "text": "b"}]""", PythonJson.Dumps(content));
    }

    [Theory]
    [InlineData("/mcp/secrets", "secrets")]
    [InlineData("/mcp/secrets/extra/segments", "secrets")]
    [InlineData("/mcp/inspect%2Dtools", "inspect-tools")]
    [InlineData("/mcp/my%20server", "my server")]
    [InlineData("/mcp/", null)]
    [InlineData("/mcp", null)]
    [InlineData("/v1/messages", null)]
    public void server_is_the_first_segment_after_mcp(string path, string? expected)
    {
        Assert.Equal(expected, BridgedToolsMcpApi.ServerFromPath(path));
    }

    [Fact]
    public void json_rpc_bodies_copy_the_id()
    {
        var request = Rpc("""{"id": {"n": 1}}""").AsObject();

        var result = BridgedToolsMcpApi.JsonRpcResult(request["id"], new JsonObject());
        var error = BridgedToolsMcpApi.JsonRpcError(request["id"], -32601, "x");

        Assert.Equal("""{"jsonrpc": "2.0", "id": {"n": 1}, "result": {}}""", PythonJson.Dumps(result));
        Assert.Equal("""{"jsonrpc": "2.0", "id": {"n": 1}, "error": {"code": -32601, "message": "x"}}""", PythonJson.Dumps(error));
        Assert.NotNull(request["id"]);
    }
}

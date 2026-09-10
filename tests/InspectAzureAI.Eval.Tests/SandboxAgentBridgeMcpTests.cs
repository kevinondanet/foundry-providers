using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>The bridged-tools MCP endpoint on a real loopback listener: auth, methods, SDK conformance, sample signals and approval grants end to end.</summary>
public class SandboxAgentBridgeMcpTests
{
    private static readonly ToolParams LookupParams = new()
    {
        Properties = new Dictionary<string, ToolParam> { ["key"] = ToolParam.Of("string", "The secret's key.") },
        Required = ["key"],
    };

    private sealed class Harness(SandboxAgentBridge server, AgentBridge bridge, ScriptedModelApi api, HttpClient client) : IAsyncDisposable
    {
        public SandboxAgentBridge Server { get; } = server;

        public AgentBridge Bridge { get; } = bridge;

        public ScriptedModelApi Api { get; } = api;

        public HttpClient Client { get; } = client;

        public int Executions;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Server.DisposeAsync();
        }
    }

    private static async Task<Harness> StartAsync(Func<JsonObject, ToolResult>? behaviour = null, IReadOnlyList<ApprovalPolicy>? approval = null, params ScriptedTurn[] turns)
    {
        var api = new ScriptedModelApi(turns, "served-model");
        var bridge = new AgentBridge(new AgentState([new ChatMessageUser("Find the database password.")]), new Model(api), approval: approval);
        Harness? harness = null;
        var tool = new ToolDef("secret_lookup", "Look up a secret.", LookupParams, (args, _) =>
        {
            Interlocked.Increment(ref harness!.Executions);
            return Task.FromResult(behaviour?.Invoke(args) ?? $"secret for {args["key"]}");
        });
        var server = await SandboxAgentBridge.StartAsync(bridge, new FakeSandboxEnvironment(), 0, [new BridgedToolsSpec("secrets", [tool])]);
        var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl + "/") };
        client.DefaultRequestHeaders.Authorization = new("Bearer", server.AuthToken);
        harness = new Harness(server, bridge, api, client);
        return harness;
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static string CallRequest(int id, string key = "db") =>
        $$"""{"jsonrpc": "2.0", "id": {{id}}, "method": "tools/call", "params": {"name": "secret_lookup", "arguments": {"key": "{{key}}"} } }""";

    private static async Task<JsonObject> RpcAsync(HttpClient client, string json, string path = "mcp/secrets")
    {
        var response = await client.PostAsync(path, Body(json));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(BridgedToolsMcpApi.ProtocolVersion, Assert.Single(response.Headers.GetValues("MCP-Protocol-Version")));
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    private const string MessagesRequest = """
        {"model": "claude-sonnet-4-6", "max_tokens": 100,
         "messages": [{"role": "user", "content": "Find the database password."}],
         "tools": [{"name": "mcp__secrets__secret_lookup", "description": "Look up a secret.", "input_schema": {"type": "object", "properties": {"key": {"type": "string"}}, "required": ["key"]}}]}
        """;

    [Fact]
    public async Task mcp_server_configs_point_at_this_listener_with_its_token()
    {
        await using var harness = await StartAsync();

        var config = Assert.Single(harness.Server.McpServerConfigs);
        Assert.Equal($"{harness.Server.BaseUrl}/mcp/secrets", config.Url);
        Assert.Equal($"Bearer {harness.Server.AuthToken}", config.Headers!["Authorization"]);
        Assert.Equal("http", config.Type);
        Assert.Null(config.Tools);
        Assert.Same(harness.Bridge.BridgedTools, harness.Server.BridgedTools);
    }

    [Fact]
    public async Task without_the_token_the_reply_is_a_401_with_a_json_rpc_body()
    {
        ProviderLogger.Reset();
        await using var harness = await StartAsync();
        using var anonymous = new HttpClient { BaseAddress = harness.Client.BaseAddress };

        var response = await anonymous.PostAsync("mcp/secrets", Body(CallRequest(1)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("2.0", body["jsonrpc"]!.GetValue<string>());
        Assert.Equal(-32600, body["error"]!["code"]!.GetValue<int>());
        Assert.Equal("invalid x-api-key / bearer token", body["error"]!["message"]!.GetValue<string>());
        Assert.Equal(0, harness.Executions);
        Assert.Contains("agent bridge answered 401 to POST /mcp/secrets", ProviderLogger.Warnings);
    }

    [Fact]
    public async Task a_get_is_405_allowing_post()
    {
        await using var harness = await StartAsync();

        var response = await harness.Client.GetAsync("mcp/secrets");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("POST", response.Content.Headers.Allow);
        Assert.Equal(BridgedToolsMcpApi.ProtocolVersion, Assert.Single(response.Headers.GetValues("MCP-Protocol-Version")));
    }

    [Fact]
    public async Task notifications_are_accepted_and_malformed_bodies_are_json_rpc_errors()
    {
        await using var harness = await StartAsync();

        var notification = await harness.Client.PostAsync("mcp/secrets", Body("""{"jsonrpc": "2.0", "method": "notifications/initialized"}"""));
        var malformed = await harness.Client.PostAsync("mcp/secrets", Body("{not json"));
        var noServer = await RpcAsync(harness.Client, """{"jsonrpc": "2.0", "id": 1, "method": "tools/list"}""", path: "mcp");

        Assert.Equal(HttpStatusCode.Accepted, notification.StatusCode);
        Assert.Empty(await notification.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(-32700, (await malformed.Content.ReadFromJsonAsync<JsonObject>())!["error"]!["code"]!.GetValue<int>());
        Assert.Equal("Invalid path: expected /mcp/{server_name}", noServer["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task the_mcp_sdk_client_lists_and_calls_a_bridged_tool()
    {
        await using var harness = await StartAsync();
        var url = Assert.Single(harness.Server.McpServerConfigs).Url;

        var server = Mcp.McpServerHttp(url, authorization: harness.Server.AuthToken);
        var source = Mcp.McpTools(server);
        await using var connection = await McpConnection.ConnectAsync(source);
        var tool = Assert.Single(await source.ToolsAsync());

        Assert.Equal("secret_lookup", tool.Name);
        Assert.Equal("Look up a secret.", tool.Description);
        Assert.Equal(["key"], tool.Parameters.Required);
        var result = await tool.Execute(new JsonObject { ["key"] = "db" }, CancellationToken.None);
        Assert.Equal("secret for db", Assert.IsType<ContentText>(Assert.Single(result.Contents!)).Text);
        Assert.Equal(1, harness.Executions);
    }

    [Fact]
    public async Task a_limit_raised_by_a_tool_is_signalled_and_answered_with_an_internal_error()
    {
        await using var harness = await StartAsync(_ => throw new LimitExceededException("token", "10", 12, "Token limit exceeded"));

        var body = await RpcAsync(harness.Client, CallRequest(7));

        Assert.Equal(7, body["id"]!.GetValue<int>());
        Assert.Equal(-32603, body["error"]!["code"]!.GetValue<int>());
        Assert.Equal("Token limit exceeded", body["error"]!["message"]!.GetValue<string>());
        Assert.True(harness.Server.LimitReached.IsCancellationRequested);
        Assert.Equal("token", harness.Server.LimitError!.Type);
    }

    [Fact]
    public async Task a_termination_raised_by_a_tool_is_signalled_and_answered_with_an_internal_error()
    {
        await using var harness = await StartAsync(_ => throw new TerminateSampleException("approver said stop"));

        var body = await RpcAsync(harness.Client, CallRequest(8));

        Assert.Equal(-32603, body["error"]!["code"]!.GetValue<int>());
        Assert.Equal("approver said stop", body["error"]!["message"]!.GetValue<string>());
        Assert.True(harness.Server.TerminateRequested.IsCancellationRequested);
        Assert.Equal("approver said stop", harness.Server.TerminateError!.Reason);
    }

    [Fact]
    public async Task under_an_approving_policy_an_approved_call_executes_once()
    {
        ProviderLogger.Reset();
        await using var harness = await StartAsync(
            approval: [new ApprovalPolicy(Approvers.Auto(), "*")],
            turns: [ScriptedTurn.ToolCall("mcp__secrets__secret_lookup", new { key = "db" }, id: "toolu_1")]);

        var messages = await harness.Client.PostAsync("v1/messages", Body(MessagesRequest));
        Assert.Equal(HttpStatusCode.OK, messages.StatusCode);
        Assert.Equal("tool_use", (await messages.Content.ReadFromJsonAsync<JsonObject>())!["stop_reason"]!.GetValue<string>());

        var first = await RpcAsync(harness.Client, CallRequest(1));
        var repeat = await RpcAsync(harness.Client, CallRequest(2));
        var otherArguments = await RpcAsync(harness.Client, CallRequest(3, key: "prod"));

        Assert.Equal("secret for db", first["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("Host tool call 'secrets/secret_lookup' was not approved for execution", repeat["error"]!["message"]!.GetValue<string>());
        Assert.Equal(-32603, otherArguments["error"]!["code"]!.GetValue<int>());
        Assert.Equal(1, harness.Executions);
        Assert.Contains("Denied host tool call 'secrets/secret_lookup': no approved execution grant matched it.", ProviderLogger.Warnings);
    }

    [Fact]
    public async Task under_a_rejecting_policy_the_host_tool_never_runs()
    {
        await using var harness = await StartAsync(
            approval: [new ApprovalPolicy(Approvers.Auto(ApprovalDecision.Reject), "*")],
            turns: [ScriptedTurn.ToolCall("mcp__secrets__secret_lookup", new { key = "db" }, id: "toolu_1"), ScriptedTurn.Text("I cannot look that up.")]);

        var messages = await harness.Client.PostAsync("v1/messages", Body(MessagesRequest));
        Assert.Equal(HttpStatusCode.OK, messages.StatusCode);

        var call = await RpcAsync(harness.Client, CallRequest(1));

        Assert.Equal(-32603, call["error"]!["code"]!.GetValue<int>());
        Assert.Equal(0, harness.Executions);
        Assert.Equal(0, harness.Bridge.BridgedTools.GrantCount);
    }

    [Fact]
    public async Task invalid_bridged_tools_fail_before_the_listener_starts()
    {
        var bridge = new AgentBridge(new AgentState([]), new Model(new ScriptedModelApi()));
        ToolDef Tool(string name) => new(name, "d", new ToolParams(), (_, _) => Task.FromResult<ToolResult>("x"));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => SandboxAgentBridge.StartAsync(bridge, new FakeSandboxEnvironment(), 0, [new("secrets", [Tool("a")]), new("secrets", [Tool("b")])]));

        Assert.StartsWith("Duplicate bridged_tools name: 'secrets'.", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, bridge.BridgedTools.Count);
    }

    [Fact]
    public async Task without_bridged_tools_the_mcp_endpoint_knows_no_server()
    {
        var bridge = new AgentBridge(new AgentState([]), new Model(new ScriptedModelApi()));
        await using var server = await SandboxAgentBridge.StartAsync(bridge, new FakeSandboxEnvironment());
        using var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl + "/") };
        client.DefaultRequestHeaders.Add("x-api-key", server.AuthToken);

        var body = await RpcAsync(client, """{"jsonrpc": "2.0", "id": 1, "method": "tools/list"}""");

        Assert.Empty(server.McpServerConfigs);
        Assert.Equal("Unknown bridged tools server: secrets", body["error"]!["message"]!.GetValue<string>());
    }
}

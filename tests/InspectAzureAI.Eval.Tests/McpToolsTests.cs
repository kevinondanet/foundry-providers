using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SdkMcpServer = ModelContextProtocol.Server.McpServer;
using SdkMcpServerOptions = ModelContextProtocol.Server.McpServerOptions;
using SdkMcpServerTool = ModelContextProtocol.Server.McpServerTool;
using SdkMcpServerToolCreateOptions = ModelContextProtocol.Server.McpServerToolCreateOptions;
using SdkPrimitiveCollection = ModelContextProtocol.Server.McpServerPrimitiveCollection<ModelContextProtocol.Server.McpServerTool>;
using SdkStreamServerTransport = ModelContextProtocol.Server.StreamServerTransport;

namespace InspectAzureAI.Eval.Tests;

/// <summary>A fact that runs only when INSPECT_MCP_STDIO_SERVER names a stdio MCP server command line (e.g. a python interpreter and script).</summary>
public sealed class StdioServerFactAttribute : FactAttribute
{
    public StdioServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INSPECT_MCP_STDIO_SERVER")))
        {
            Skip = "Set INSPECT_MCP_STDIO_SERVER to a stdio MCP server command line to run this test.";
        }
    }
}

/// <summary>
/// Port-level behaviour of <c>tool/_mcp</c> (<c>tests/tools/test_mcp_tools.py</c>, <c>test_mcp_session_isolation.py</c>,
/// <c>test_mcp_call_tool_timeout.py</c>) against in-process SDK servers joined by pipes; no network or processes.
/// </summary>
public class McpToolsTests
{
    private static readonly JsonElement EmptyObjectSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{}}""");

    // ----- the Python test server (mcp_test_server.py) plus a few tools for error, content and cancellation paths -----

    private static IEnumerable<SdkMcpServerTool> TestServerTools()
    {
        yield return SdkMcpServerTool.Create(([Description("The message")] string message) => message, new SdkMcpServerToolCreateOptions { Name = "echo", Description = "Echoes back the input message" });
        yield return SdkMcpServerTool.Create((int x, int y) => (x + y).ToString(), new SdkMcpServerToolCreateOptions { Name = "add", Description = "Adds two numbers" });
        yield return SdkMcpServerTool.Create(() => "ok", new SdkMcpServerToolCreateOptions { Name = "get_status", Description = "Returns a fixed status string" });
        yield return SdkMcpServerTool.Create(() => "test server v1", new SdkMcpServerToolCreateOptions { Name = "get_info", Description = "Returns a fixed info string" });
        yield return SdkMcpServerTool.Create(
            () => new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "boom" }, new TextContentBlock { Text = "details" }] },
            new SdkMcpServerToolCreateOptions { Name = "fail", Description = "Always fails" });
        yield return SdkMcpServerTool.Create(
            () => new CallToolResult { Content = [ImageContentBlock.FromBytes(new byte[] { 1, 2, 3 }, "image/png"), new TextContentBlock { Text = "caption" }] },
            new SdkMcpServerToolCreateOptions { Name = "picture", Description = "Returns an image" });
        yield return SdkMcpServerTool.Create(
            async (CancellationToken cancellationToken) =>
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
                return "never";
            },
            new SdkMcpServerToolCreateOptions { Name = "block", Description = "Never returns" });
    }

    private static SdkMcpServerOptions ServerOptions()
    {
        var options = new SdkMcpServerOptions
        {
            ServerInfo = new Implementation { Name = "test-server", Version = "1" },
            ToolCollection = new SdkPrimitiveCollection(StringComparer.Ordinal),
        };
        foreach (var tool in TestServerTools())
        {
            options.ToolCollection.Add(tool);
        }

        return options;
    }

    /// <summary>An SDK client transport whose every connect starts a fresh in-process server joined by two pipes.</summary>
    private sealed class InProcessTransport : IClientTransport
    {
        private int _connects;
        private int _disposals;

        public int Connects => _connects;

        public int Disposals => _disposals;

        public string Name => "in-process";

        public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connects);
            var toServer = new Pipe();
            var toClient = new Pipe();
            var server = SdkMcpServer.Create(new SdkStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), "test-server", null), ServerOptions(), null, null);
            var run = server.RunAsync(CancellationToken.None);
            var client = await new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream(), null).ConnectAsync(cancellationToken);
            return new Session(this, client, server, run, toServer, toClient);
        }

        private sealed class Session(InProcessTransport owner, ITransport client, SdkMcpServer server, Task run, Pipe toServer, Pipe toClient) : ITransport
        {
            public string? SessionId => client.SessionId;

            public ChannelReader<JsonRpcMessage> MessageReader => client.MessageReader;

            public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) => client.SendMessageAsync(message, cancellationToken);

            public async ValueTask DisposeAsync()
            {
                await client.DisposeAsync();
                toServer.Writer.Complete();
                toClient.Writer.Complete();
                await server.DisposeAsync();
                try
                {
                    await run;
                }
                catch (Exception)
                {
                    // the pipes closed under the server
                }

                Interlocked.Increment(ref owner._disposals);
            }
        }
    }

    private static (McpServerLocal Server, InProcessTransport Transport) LocalServer(TimeSpan? timeout = null)
    {
        var transport = new InProcessTransport();
        return (new McpServerLocal(() => transport, "test", events: false, timeout), transport);
    }

    private static IClientTransport NoTransport() => throw new InvalidOperationException("these tests must not open a transport");

    private static Task<ToolResult> Call(ToolDef tool, object? args = null, CancellationToken cancellationToken = default) =>
        tool.Execute(args is null ? new JsonObject() : JsonNode.Parse(JsonSerializer.Serialize(args))!.AsObject(), cancellationToken);

    private static string SingleText(ToolResult result) => Assert.IsType<ContentText>(Assert.Single(result.Contents!)).Text;

    // ----- listing, filtering, invocation -----

    [Fact]
    public async Task listing_tools_passes_the_server_schema_through_to_tool_params()
    {
        var (server, transport) = LocalServer();
        await using var connection = await McpConnection.ConnectAsync(server);

        var tools = await server.ToolsAsync();

        // The SDK's tool collection is unordered, so compare as sets.
        Assert.Equal(new[] { "add", "block", "echo", "fail", "get_info", "get_status", "picture" }, tools.Select(t => t.Name).Order(StringComparer.Ordinal).ToArray());
        var add = tools.Single(t => t.Name == "add");
        Assert.Equal("Adds two numbers", add.Description);
        var schema = add.Parameters.ToJson();
        Assert.Equal("integer", schema["properties"]!["x"]!["type"]!.GetValue<string>());
        Assert.Equal("x", schema["properties"]!["x"]!["description"]!.GetValue<string>());
        Assert.Equal("y", schema["properties"]!["y"]!["description"]!.GetValue<string>());
        Assert.Equal(new[] { "x", "y" }, schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Equal("The message", tools.Single(t => t.Name == "echo").Parameters.Properties["message"].Description);
        Assert.True(tools.All(t => t.Parallel));
        Assert.Equal(1, transport.Connects);
    }

    [Fact]
    public async Task mcp_tools_filters_by_glob()
    {
        var (server, _) = LocalServer();
        var filtered = Mcp.McpTools(server, ["get_*"]);
        await using var connection = await McpConnection.ConnectAsync(filtered);

        var tools = await filtered.ToolsAsync();

        Assert.Equal(new[] { "get_info", "get_status" }, tools.Select(t => t.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("ok", SingleText(await Call(tools.Single(t => t.Name == "get_status"))));
    }

    [Fact]
    public async Task calling_a_tool_returns_its_content_list()
    {
        var (server, _) = LocalServer();
        await using var connection = await McpConnection.ConnectAsync(server);
        var tools = await server.ToolsAsync();

        Assert.Equal("hello", SingleText(await Call(tools.Single(t => t.Name == "echo"), new { message = "hello" })));
        Assert.Equal("5", SingleText(await Call(tools.Single(t => t.Name == "add"), new { x = 2, y = 3 })));
    }

    [Fact]
    public async Task an_error_result_becomes_a_tool_error_carrying_its_text()
    {
        var (server, _) = LocalServer();
        await using var connection = await McpConnection.ConnectAsync(server);
        var fail = (await server.ToolsAsync()).Single(t => t.Name == "fail");

        var ex = await Assert.ThrowsAsync<ToolError>(() => Call(fail));

        Assert.Equal("boom\n\ndetails", ex.Message);
    }

    [Fact]
    public async Task a_json_rpc_error_from_the_server_is_fed_back_to_the_model()
    {
        var (server, _) = LocalServer();
        await using var connection = await McpConnection.ConnectAsync(server);
        var missing = server.Session.ToolDefFromMcpTool(new Tool { Name = "missing", Description = "not on the server", InputSchema = EmptyObjectSchema });

        var ex = await Assert.ThrowsAnyAsync<ToolError>(() => Call(missing));

        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public async Task image_content_maps_to_a_data_uri()
    {
        var (server, _) = LocalServer();
        await using var connection = await McpConnection.ConnectAsync(server);
        var picture = (await server.ToolsAsync()).Single(t => t.Name == "picture");

        var result = await Call(picture);

        Assert.Equal(2, result.Contents!.Count);
        Assert.Equal("data:image/png;base64,AQID", Assert.IsType<ContentImage>(result.Contents[0]).Image);
        Assert.Equal("caption", Assert.IsType<ContentText>(result.Contents[1]).Text);
    }

    // ----- connection lifecycle -----

    [Fact]
    public async Task the_connection_is_reused_across_calls_and_nested_scopes_and_closed_on_dispose()
    {
        var (server, transport) = LocalServer();

        await using (await McpConnection.ConnectAsync(server))
        {
            var echo = (await server.ToolsAsync()).Single(t => t.Name == "echo");
            await Call(echo, new { message = "a" });
            await Call(echo, new { message = "b" });
            await using (await McpConnection.ConnectAsync(server))
            {
                Assert.Equal(2, server.Session.RefCount);
                Assert.NotEmpty(await server.ToolsAsync());
            }

            Assert.Equal(1, server.Session.RefCount);
            Assert.True(server.Session.IsConnected);
            Assert.True(server.Session.HasCachedToolList);
            Assert.Equal(1, transport.Connects);
        }

        Assert.False(server.Session.IsConnected);
        Assert.False(server.Session.HasCachedToolList);
        Assert.Equal(1, transport.Disposals);

        // Outside a scope every listing (until cached) and every call opens a transient connection.
        var tools = await server.ToolsAsync();
        Assert.Equal(2, transport.Connects);
        await server.ToolsAsync();
        Assert.Equal(2, transport.Connects);
        Assert.Equal("c", SingleText(await Call(tools.Single(t => t.Name == "echo"), new { message = "c" })));
        Assert.Equal(3, transport.Connects);
        Assert.Equal(3, transport.Disposals);

        // Re-entering the same session reconnects.
        await using (await McpConnection.ConnectAsync(server))
        {
            Assert.True(server.Session.IsConnected);
            Assert.Equal(4, transport.Connects);
        }
    }

    [Fact]
    public async Task cancelling_a_call_leaves_the_scope_able_to_dispose_the_connection()
    {
        var (server, transport) = LocalServer();
        using var cts = new CancellationTokenSource();
        var connection = await McpConnection.ConnectAsync(server);
        try
        {
            var block = (await server.ToolsAsync()).Single(t => t.Name == "block");
            var call = Call(block, cancellationToken: cts.Token);
            await Task.Delay(50);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.False(server.Session.IsConnected);
        Assert.Equal(1, transport.Disposals);
    }

    [Fact]
    public async Task a_tool_call_times_out_instead_of_hanging()
    {
        var (server, transport) = LocalServer(timeout: TimeSpan.FromMilliseconds(300));
        var block = (await server.ToolsAsync()).Single(t => t.Name == "block");

        var ex = await Assert.ThrowsAsync<ToolError>(() => Call(block).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal("Tool 'block' timed out before completing.", ex.Message);
        Assert.Equal(transport.Connects, transport.Disposals);
    }

    [Fact]
    public async Task a_failed_connect_leaves_the_session_disconnected_so_a_retry_reconnects()
    {
        var attempts = 0;
        var real = new InProcessTransport();
        var server = new McpServerLocal(() => ++attempts == 1 ? throw new IOException("connection refused") : real, "flaky", events: false);

        await Assert.ThrowsAsync<IOException>(() => McpConnection.ConnectAsync(server));
        Assert.False(server.Session.IsConnected);
        Assert.Equal(0, server.Session.RefCount);

        await using var connection = await McpConnection.ConnectAsync(server);
        Assert.True(server.Session.IsConnected);
        Assert.Equal(1, real.Connects);
    }

    [Fact]
    public async Task exit_without_enter_is_an_error()
    {
        var server = new McpServerLocal(NoTransport, "s", events: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => server.ExitAsync());
    }

    [Fact]
    public async Task sessions_are_per_server_instance_and_per_async_flow()
    {
        var a = new McpServerLocal(NoTransport, "same-name", events: false);
        var b = new McpServerLocal(NoTransport, "same-name", events: false);
        Assert.NotSame(a.Session, b.Session);
        Assert.Same(a.Session, a.Session);

        // A flow that starts before its parent has a session gets its own, which never leaks to the parent.
        var c = new McpServerLocal(NoTransport, "c", events: false);
        McpServerLocalSession? childOwn = null;
        await Task.Run(() => childOwn = c.Session);
        Assert.NotNull(childOwn);
        Assert.NotSame(childOwn, c.Session);

        // A flow started after the parent's session exists shares it (parallel tool stages).
        var parent = a.Session;
        McpServerLocalSession? inherited = null;
        await Task.Run(() => inherited = a.Session);
        Assert.Same(parent, inherited);
    }

    [Fact]
    public async Task connection_discovers_servers_behind_tool_sources_and_rejects_unknown_entries()
    {
        var (server, transport) = LocalServer();
        var plain = new ToolDef("plain", "a plain tool", new ToolParams(), (_, _) => Task.FromResult(ToolResult.Empty));
        var other = new CountingServer();

        await using (var connection = await McpConnection.ConnectAsync([plain, new McpToolSourceLocal(other, null), Mcp.McpTools(server, ["echo"])]))
        {
            Assert.Equal(2, connection.Servers.Count);
            Assert.True(server.Session.IsConnected);
            Assert.Equal(1, transport.Connects);
        }

        Assert.False(server.Session.IsConnected);
        Assert.Equal(1, other.Entered);
        Assert.Equal(1, other.Exited);
        await Assert.ThrowsAsync<ArgumentException>(() => McpConnection.ConnectAsync([new object()]));
    }

    [Fact]
    public async Task a_failure_entering_one_server_exits_the_ones_already_entered()
    {
        var first = new CountingServer();
        var second = new McpServerLocal(() => throw new IOException("nope"), "second", events: false);

        await Assert.ThrowsAsync<IOException>(() => McpConnection.ConnectAsync([first, second]));

        Assert.Equal(1, first.Entered);
        Assert.Equal(1, first.Exited);
    }

    /// <summary>Port of the test's <c>_FakeToolServer</c>: counts resolutions and hands out a tool bound to each one.</summary>
    private sealed class CountingServer : McpServer
    {
        public int Calls;
        public int Entered;
        public int Exited;

        public override Task<IReadOnlyList<ToolDef>> ToolsAsync(CancellationToken cancellationToken = default)
        {
            var marker = ++Calls;
            return Task.FromResult<IReadOnlyList<ToolDef>>([new ToolDef("tool_a", "a", new ToolParams(), (_, _) => Task.FromResult<ToolResult>(marker.ToString()))]);
        }

        public override Task EnterAsync(CancellationToken cancellationToken = default)
        {
            Entered++;
            return Task.CompletedTask;
        }

        public override Task ExitAsync()
        {
            Exited++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task mcp_tools_never_caches_so_each_caller_gets_tools_bound_to_its_own_resolution()
    {
        var server = new CountingServer();
        var source = new McpToolSourceLocal(server, null);
        var seen = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var tools = await source.ToolsAsync();
            seen.Add((await Call(tools[0])).AsText());
        }

        Assert.Equal(3, server.Calls);
        Assert.Equal(new[] { "1", "2", "3" }, seen);
    }

    // ----- the sandbox transport -----

    /// <summary>A scripted carrier (Python's monkeypatched <c>exec_*</c> functions).</summary>
    private sealed class ScriptedRpc : IMcpSandboxRpc
    {
        public List<(string Method, JsonNode? Params, bool Notification, TimeSpan? Timeout)> Calls { get; } = [];

        public Dictionary<string, Func<JsonNode?, string>> Handlers { get; } = new(StringComparer.Ordinal)
        {
            ["mcp_launch_server"] = _ => """{"jsonrpc": "2.0", "id": 1, "result": 7}""",
            ["mcp_kill_server"] = _ => """{"jsonrpc": "2.0", "id": 2, "result": null}""",
            ["mcp_send_notification"] = _ => "",
        };

        public Task<string> CallAsync(string method, JsonNode? parameters, bool isNotification, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((method, parameters, isNotification, timeout));
            return Task.FromResult(Handlers[method](parameters));
        }
    }

    private static async Task<(ITransport Transport, ScriptedRpc Rpc)> SandboxTransport(ScriptedRpc? rpc = null, TimeSpan? timeout = null)
    {
        rpc ??= new ScriptedRpc();
        var transport = await new McpSandboxClientTransport(new McpStdioServerParameters("fake"), _ => Task.FromResult<IMcpSandboxRpc>(rpc), timeout).ConnectAsync();
        return (transport, rpc);
    }

    private static JsonRpcRequest CallRequest(long id = 42) => new() { Id = new RequestId(id), Method = "tools/call", Params = new JsonObject() };

    [Fact]
    public async Task sandbox_connect_launches_the_server_and_relays_requests_by_session_id()
    {
        var rpc = new ScriptedRpc();
        rpc.Handlers["mcp_send_request"] = _ => """{"jsonrpc": "2.0", "id": 3, "result": {"jsonrpc": "2.0", "id": 42, "result": {"content": []}}}""";
        var (transport, _) = await SandboxTransport(rpc);

        await using (transport)
        {
            var launch = Assert.Single(rpc.Calls);
            Assert.Equal("mcp_launch_server", launch.Method);
            Assert.Equal(TimeSpan.FromSeconds(180), launch.Timeout);
            Assert.Equal(
                """{"command":"fake","args":[],"env":null,"cwd":null,"encoding":"utf-8","encoding_error_handler":"strict"}""",
                launch.Params!["server_params"]!.ToJsonString());
            Assert.Equal("7", transport.SessionId);

            await transport.SendMessageAsync(CallRequest());
            var response = Assert.IsType<JsonRpcResponse>(await transport.MessageReader.ReadAsync());
            Assert.Equal(new RequestId(42), response.Id);

            var send = rpc.Calls[1];
            Assert.Equal("mcp_send_request", send.Method);
            Assert.False(send.Notification);
            Assert.Equal(7, send.Params!["session_id"]!.GetValue<long>());
            Assert.Equal("tools/call", send.Params["request"]!["method"]!.GetValue<string>());
            Assert.Equal(42, send.Params["request"]!["id"]!.GetValue<long>());

            await transport.SendMessageAsync(new JsonRpcNotification { Method = "notifications/initialized" });
            var notify = rpc.Calls[2];
            Assert.Equal("mcp_send_notification", notify.Method);
            Assert.True(notify.Notification);
            Assert.Equal("notifications/initialized", notify.Params!["notification"]!["method"]!.GetValue<string>());
        }

        var kill = rpc.Calls[^1];
        Assert.Equal("mcp_kill_server", kill.Method);
        Assert.Equal(7, kill.Params!["session_id"]!.GetValue<long>());
        Assert.Equal(TimeSpan.FromSeconds(30), kill.Timeout);
        Assert.True(transport.MessageReader.Completion.IsCompleted);
    }

    [Fact]
    public async Task sandbox_transport_synthesizes_a_json_rpc_error_when_the_carrier_fails()
    {
        var rpc = new ScriptedRpc();
        rpc.Handlers["mcp_send_request"] = _ => throw new InvalidOperationException("boom");
        var (transport, _) = await SandboxTransport(rpc);

        await using (transport)
        {
            await transport.SendMessageAsync(CallRequest());

            var error = Assert.IsType<JsonRpcError>(await transport.MessageReader.ReadAsync());
            Assert.Equal(new RequestId(42), error.Id);
            Assert.Equal(-32603, error.Error.Code);
            Assert.Contains("InvalidOperationException", error.Error.Message);
            Assert.Contains("boom", error.Error.Message);
        }
    }

    [Fact]
    public async Task sandbox_transport_uses_the_friendly_message_for_timeouts()
    {
        var rpc = new ScriptedRpc();
        rpc.Handlers["mcp_send_request"] = _ => throw new SandboxTimeoutException("transport deadline exceeded", "");
        var (transport, _) = await SandboxTransport(rpc);

        await using (transport)
        {
            await transport.SendMessageAsync(CallRequest());

            var error = Assert.IsType<JsonRpcError>(await transport.MessageReader.ReadAsync());
            Assert.Equal(-32603, error.Error.Code);
            Assert.Equal("MCP request timed out before completing.", error.Error.Message);
        }
    }

    [Fact]
    public async Task sandbox_transport_logs_and_drops_a_failed_notification()
    {
        var rpc = new ScriptedRpc();
        rpc.Handlers["mcp_send_notification"] = _ => throw new TimeoutException("notify timeout");
        var (transport, _) = await SandboxTransport(rpc);
        var before = ProviderLogger.Warnings.Count;

        await using (transport)
        {
            await transport.SendMessageAsync(new JsonRpcNotification { Method = "notifications/initialized" });
        }

        var warning = Assert.Single(ProviderLogger.Warnings.Skip(before), w => w.Contains("notification dropped", StringComparison.Ordinal));
        Assert.Contains("TimeoutException", warning);
        Assert.Contains("notify timeout", warning);
    }

    [Fact]
    public async Task sandbox_kill_failure_does_not_propagate()
    {
        var rpc = new ScriptedRpc();
        rpc.Handlers["mcp_kill_server"] = _ => throw new InvalidOperationException("simulated kill failure");
        var (transport, _) = await SandboxTransport(rpc);
        var before = ProviderLogger.Warnings.Count;

        await transport.DisposeAsync();

        Assert.Contains(ProviderLogger.Warnings.Skip(before), w => w.Contains("shutdown failed", StringComparison.Ordinal) && w.Contains("simulated kill failure", StringComparison.Ordinal));
        Assert.True(transport.MessageReader.Completion.IsCompleted);
    }

    [Fact]
    public async Task sandbox_launch_errors_propagate_with_the_sandbox_tools_mapping()
    {
        var rpc = new ScriptedRpc();
        rpc.Handlers["mcp_launch_server"] = _ => """{"jsonrpc": "2.0", "id": 1, "error": {"code": -32099, "message": "no such command"}}""";

        var ex = await Assert.ThrowsAsync<ToolError>(() => SandboxTransport(rpc));

        Assert.Equal("no such command", ex.Message);
    }

    [Fact]
    public void sandbox_server_defaults_the_timeout_so_a_lost_response_cannot_hang()
    {
        var server = Assert.IsType<McpServerLocal>(Mcp.McpServerSandbox("cmd"));
        Assert.Equal(TimeSpan.FromSeconds(180), server.Timeout);
        Assert.False(server.Events);
        Assert.Equal("cmd", server.Name);

        var explicitTimeout = Assert.IsType<McpServerLocal>(Mcp.McpServerSandbox("cmd", ["--flag"], timeout: TimeSpan.FromSeconds(42)));
        Assert.Equal(TimeSpan.FromSeconds(42), explicitTimeout.Timeout);
        Assert.Equal("cmd --flag", explicitTimeout.Name);
    }

    /// <summary>Stands in for the injected sandbox tools CLI: <c>{cli} exec</c> requests on stdin are answered by relaying to an in-process SDK server.</summary>
    private sealed class CarrierSandbox : ISandboxEnvironment
    {
        private readonly Dictionary<long, (SdkMcpServer Server, CarrierServerTransport Transport, Task Run)> _sessions = new();
        private long _nextSession = 100;

        public List<FakeExecCall> Calls { get; } = [];

        public string HostAddress => "127.0.0.1";

        public async Task<ExecResult> ExecAsync(IReadOnlyList<string> cmd, string? input = null, string? cwd = null, IReadOnlyDictionary<string, string>? env = null, string? user = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(new FakeExecCall(cmd, input, cwd, env, user, timeout));
            var request = JsonNode.Parse(input!)!.AsObject();
            var method = request["method"]!.GetValue<string>();
            var parameters = request["params"]?.AsObject();
            JsonNode? result = null;
            switch (method)
            {
                case "mcp_launch_server":
                    var transport = new CarrierServerTransport();
                    var server = SdkMcpServer.Create(transport, ServerOptions(), null, null);
                    var id = _nextSession++;
                    _sessions[id] = (server, transport, server.RunAsync(CancellationToken.None));
                    result = id;
                    break;
                case "mcp_send_request":
                    var session = _sessions[parameters!["session_id"]!.GetValue<long>()];
                    var incoming = Assert.IsType<JsonRpcRequest>(JsonSerializer.Deserialize<JsonRpcMessage>(parameters["request"], McpJsonUtilities.DefaultOptions));
                    var response = await session.Transport.RequestAsync(incoming, cancellationToken);
                    result = JsonSerializer.SerializeToNode<JsonRpcMessage>(response, McpJsonUtilities.DefaultOptions);
                    break;
                case "mcp_send_notification":
                    var notified = _sessions[parameters!["session_id"]!.GetValue<long>()];
                    var notification = Assert.IsType<JsonRpcNotification>(JsonSerializer.Deserialize<JsonRpcMessage>(parameters["notification"], McpJsonUtilities.DefaultOptions));
                    await notified.Transport.NotifyAsync(notification);
                    return new ExecResult(true, 0, "", "");
                case "mcp_kill_server":
                    var killedId = parameters!["session_id"]!.GetValue<long>();
                    var killed = _sessions[killedId];
                    _sessions.Remove(killedId);
                    killed.Transport.Complete();
                    await killed.Server.DisposeAsync();
                    try
                    {
                        await killed.Run;
                    }
                    catch (Exception)
                    {
                        // the transport completed under the server
                    }

                    break;
                default:
                    throw new InvalidOperationException($"unexpected carrier method {method}");
            }

            var envelope = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = result };
            return new ExecResult(true, 0, envelope.ToJsonString(), "");
        }

        public Task WriteFileAsync(string path, string contents, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task WriteFileAsync(string path, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<byte[]> ReadFileBytesAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Server-side transport for the carrier: requests are pushed in and their responses matched by id; server-initiated messages are not relayed (as in Python).</summary>
    private sealed class CarrierServerTransport : ITransport
    {
        private readonly Channel<JsonRpcMessage> _incoming = Channel.CreateUnbounded<JsonRpcMessage>();
        private readonly ConcurrentDictionary<RequestId, TaskCompletionSource<JsonRpcMessage>> _pending = new();

        public string? SessionId => "carrier";

        public ChannelReader<JsonRpcMessage> MessageReader => _incoming.Reader;

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            if (message is JsonRpcMessageWithId withId && _pending.TryRemove(withId.Id, out var pending))
            {
                pending.TrySetResult(message);
            }

            return Task.CompletedTask;
        }

        public async Task<JsonRpcMessage> RequestAsync(JsonRpcRequest request, CancellationToken cancellationToken)
        {
            var pending = new TaskCompletionSource<JsonRpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[request.Id] = pending;
            await _incoming.Writer.WriteAsync(request, cancellationToken);
            return await pending.Task.WaitAsync(cancellationToken);
        }

        public ValueTask NotifyAsync(JsonRpcNotification notification) => _incoming.Writer.WriteAsync(notification);

        public void Complete() => _incoming.Writer.TryComplete();

        public ValueTask DisposeAsync()
        {
            Complete();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task sandbox_server_runs_through_the_cli_carrier_end_to_end()
    {
        var carrier = new CarrierSandbox();
        using var scope = new SampleContextScope(sandbox: carrier);
        var server = Mcp.McpServerSandbox("npx", ["--offline", "@modelcontextprotocol/server-filesystem", "/"], cwd: "/work", env: new Dictionary<string, string> { ["A"] = "1" });

        await using (await McpConnection.ConnectAsync(server))
        {
            var tools = await server.ToolsAsync();
            Assert.Contains(tools, t => t.Name == "add");
            Assert.Equal("hi", SingleText(await Call(tools.Single(t => t.Name == "echo"), new { message = "hi" })));
            var ex = await Assert.ThrowsAsync<ToolError>(() => Call(tools.Single(t => t.Name == "fail")));
            Assert.Equal("boom\n\ndetails", ex.Message);
        }

        Assert.All(carrier.Calls, call => Assert.Equal(new[] { McpSandboxExecRpc.DefaultCli, "exec" }, call.Cmd));
        var methods = carrier.Calls.Select(c => JsonNode.Parse(c.Input!)!["method"]!.GetValue<string>()).ToArray();
        Assert.Equal("mcp_launch_server", methods[0]);
        Assert.Equal("mcp_kill_server", methods[^1]);
        var relayed = carrier.Calls.Select(c => JsonNode.Parse(c.Input!)!["params"]?["request"]?["method"]?.GetValue<string>()).OfType<string>().ToArray();
        // A 2.x peer negotiates with server/discover; older servers (e.g. the Python test server) get initialize.
        Assert.Contains(relayed[0], new[] { "server/discover", "initialize" });
        Assert.Contains("tools/list", relayed);
        Assert.Equal(2, relayed.Count(m => m == "tools/call"));
        Assert.All(carrier.Calls.Skip(1).SkipLast(1), call => Assert.Equal(TimeSpan.FromSeconds(180), call.Timeout));
        Assert.Equal(
            """{"command":"npx","args":["--offline","@modelcontextprotocol/server-filesystem","/"],"env":{"A":"1"},"cwd":"/work","encoding":"utf-8","encoding_error_handler":"strict"}""",
            JsonNode.Parse(carrier.Calls[0].Input!)!["params"]!["server_params"]!.ToJsonString());
        Assert.Equal(TimeSpan.FromSeconds(180), carrier.Calls[0].Timeout);
        Assert.Equal(TimeSpan.FromSeconds(30), carrier.Calls[^1].Timeout);
        Assert.Equal("npx --offline @modelcontextprotocol/server-filesystem /", ((McpServerLocal)server).Name);
    }

    [Fact]
    public async Task sandbox_exec_rpc_reports_a_failed_exec_with_its_output()
    {
        var sandbox = new FakeSandboxEnvironment(_ => FakeSandboxEnvironment.Fail(1, stderr: "", stdout: "entrypoint crashed"));
        var rpc = new McpSandboxExecRpc(sandbox, "/opt/tools");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rpc.CallAsync("mcp_launch_server", new JsonObject { ["server_params"] = new JsonObject() }, false, TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Equal("Sandbox.exec failure executing mcp_launch_server(server_params: {}): entrypoint crashed", ex.Message);
        var call = Assert.Single(sandbox.Calls);
        Assert.Equal(new[] { "/opt/tools", "exec" }, call.Cmd);
        var request = JsonNode.Parse(call.Input!)!.AsObject();
        Assert.Equal("2.0", request["jsonrpc"]!.GetValue<string>());
        Assert.True(request["id"]!.GetValue<long>() >= 666);
        Assert.Equal(TimeSpan.FromSeconds(1), call.Timeout);
    }

    // ----- factories, config and remote execution -----

    [Fact]
    public void stdio_factory_names_the_server_after_the_command_line_and_scopes_the_environment()
    {
        var server = Assert.IsType<McpServerLocal>(Mcp.McpServerStdio("python3", ["-m", "mcp_server_git"]));
        Assert.Equal("python3 -m mcp_server_git", server.Name);
        Assert.True(server.Events);
        Assert.Null(server.Timeout);
        Assert.Equal("Git", ((McpServerLocal)Mcp.McpServerStdio("python3", name: "Git")).Name);

        using var leak = new EnvVarScope().Set("INSPECT_MCP_TEST_LEAK", "1");
        var env = Mcp.StdioEnvironment(new Dictionary<string, string> { ["GOOGLE_MAPS_API_KEY"] = "k" });
        Assert.Equal("k", env["GOOGLE_MAPS_API_KEY"]);
        Assert.True(env.ContainsKey("PATH"));
        Assert.False(env.ContainsKey("INSPECT_MCP_TEST_LEAK"));
    }

    [Fact]
    public void http_factories_resolve_headers_and_default_the_name_to_the_url()
    {
        var server = Assert.IsType<McpServerLocal>(Mcp.McpServerHttp("http://127.0.0.1:8000/mcp", authorization: "k"));
        Assert.Equal("http://127.0.0.1:8000/mcp", server.Name);
        Assert.True(server.Events);
        Assert.IsType<McpServerLocal>(Mcp.McpServerSse("http://127.0.0.1:8000/sse"));

        Assert.Null(Mcp.ResolveHeaders(null, null));
        Assert.Equal(new Dictionary<string, string> { ["X-Api"] = "1", ["Authorization"] = "Bearer k" }, Mcp.ResolveHeaders("k", new Dictionary<string, string> { ["X-Api"] = "1" }));
        Assert.Equal(new Dictionary<string, string> { ["Authorization"] = "Bearer k" }, Mcp.ResolveHeaders("k", null));
        Assert.Equal(new Dictionary<string, string> { ["X-Api"] = "1" }, Mcp.ResolveHeaders(null, new Dictionary<string, string> { ["X-Api"] = "1" }));
    }

    [Fact]
    public async Task remote_execution_yields_a_marker_tool_carrying_the_config()
    {
        var server = Mcp.McpServerHttp("https://mcp.deepwiki.com/mcp", name: "deepwiki", execution: McpExecution.Remote, authorization: "k");
        var remote = Assert.IsType<McpServerRemote>(server);
        Assert.Equal("k", remote.Config.AuthorizationToken);

        var tool = Assert.Single(await server.ToolsAsync());
        Assert.Equal("mcp_server_deepwiki", tool.Name);
        Assert.Equal(tool.Name, tool.Description);
        Assert.Equal(
            """{"type":"http","name":"deepwiki","tools":"all","url":"https://mcp.deepwiki.com/mcp","headers":{"Authorization":"Bearer k"}}""",
            tool.Options!.ToJsonString());
        Assert.True(McpServerRemote.IsMcpServerTool(tool.ToInfo()));
        Assert.False(McpServerRemote.IsMcpServerTool(new ToolInfo("mcp_server_x", "no options")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Call(tool));

        var filtered = Assert.Single(await Mcp.McpTools(Mcp.McpServerSse("u", name: "n", execution: McpExecution.Remote), ["a", "b*"]).ToolsAsync());
        Assert.Equal("""{"type":"sse","name":"n","tools":["a","b*"],"url":"u","headers":null}""", filtered.Options!.ToJsonString());

        await using var connection = await McpConnection.ConnectAsync(server);
        Assert.Same(server, Assert.Single(connection.Servers));
    }

    [Fact]
    public void config_dumps_match_pydantic()
    {
        Assert.Equal(
            """{"type":"stdio","name":"s","tools":"all","command":"npx","args":["-y","x"],"cwd":null,"env":null}""",
            new McpServerConfigStdio("s", "npx") { Args = ["-y", "x"] }.ToJson().ToJsonString());
        Assert.Equal("BEARER x", new McpServerConfigHttp("http", "n", "u", new Dictionary<string, string> { ["Authorization"] = "BEARER x" }).ToJson()["headers"]!["Authorization"]!.GetValue<string>());
        Assert.Equal("x", new McpServerConfigHttp("http", "n", "u", new Dictionary<string, string> { ["Authorization"] = "bearer x" }).AuthorizationToken);
        Assert.Equal("raw", new McpServerConfigHttp("http", "n", "u", new Dictionary<string, string> { ["Authorization"] = "raw" }).AuthorizationToken);
        Assert.Null(new McpServerConfigHttp("http", "n", "u").AuthorizationToken);
        Assert.Throws<ArgumentException>(() => new McpServerConfigHttp("stdio", "n", "u"));
    }

    // ----- content mapping, error mapping and fnmatch -----

    [Fact]
    public void content_blocks_map_to_inspect_content_and_back()
    {
        var image = ImageContentBlock.FromBytes(new byte[] { 1, 2, 3 }, "image/png");
        var audio = AudioContentBlock.FromBytes(new byte[] { 1 }, "audio/wav");
        var link = new ResourceLinkBlock { Uri = "file:///a.txt", Name = "a", Description = "A file" };
        var embedded = new EmbeddedResourceBlock { Resource = new TextResourceContents { Uri = "file:///b", Text = "body" } };

        Assert.Equal("data:image/png;base64,AQID", Assert.IsType<ContentImage>(McpContent.AsInspectContent(image)).Image);
        var inspectAudio = Assert.IsType<ContentAudio>(McpContent.AsInspectContent(audio));
        Assert.Equal("data:audio/wav;base64,AQ==", inspectAudio.Audio);
        Assert.Equal("wav", inspectAudio.Format);
        Assert.Equal("mp3", Assert.IsType<ContentAudio>(McpContent.AsInspectContent(AudioContentBlock.FromBytes(new byte[] { 1 }, "audio/mpeg"))).Format);
        Assert.Throws<ArgumentException>(() => McpContent.AsInspectContent(AudioContentBlock.FromBytes(new byte[] { 1 }, "audio/ogg")));
        Assert.Equal("A file (file:///a.txt)", Assert.IsType<ContentText>(McpContent.AsInspectContent(link)).Text);
        Assert.Equal("body", Assert.IsType<ContentText>(McpContent.AsInspectContent(embedded)).Text);
        Assert.Throws<ArgumentException>(() => McpContent.AsInspectContent(new EmbeddedResourceBlock { Resource = BlobResourceContents.FromBytes(new byte[] { 1 }, "file:///c", "application/octet-stream") }));

        var back = Assert.IsType<ImageContentBlock>(McpContent.AsMcpContent(new ContentImage("data:image/jpeg;base64,AQID")));
        Assert.Equal("image/jpeg", back.MimeType);
        Assert.Equal(new byte[] { 1, 2, 3 }, back.DecodedData.ToArray());
        Assert.Equal("hi", Assert.IsType<TextContentBlock>(McpContent.AsMcpContent(new ContentText("hi"))).Text);

        Assert.Equal(
            "boom\n\n(base64 encoded image omitted)\n\n(base64 encoded audio omitted)\n\nA file (file:///a.txt)\n\nbody",
            McpContent.ToolResultAsText([new TextContentBlock { Text = "boom" }, image, audio, link, embedded]));
    }

    [Fact]
    public void rpc_error_codes_map_to_tool_errors_for_the_model_and_to_bugs_otherwise()
    {
        var parameters = new JsonObject { ["message"] = "hi", ["count"] = 2 };

        Assert.IsType<ToolParsingError>(McpJsonRpc.ExceptionForRpcResponseError(-32602, "bad", "echo", parameters, McpJsonRpc.McpServerError));
        Assert.IsType<ToolError>(McpJsonRpc.ExceptionForRpcResponseError(-32603, "internal", "echo", parameters, McpJsonRpc.McpServerError), exactMatch: true);
        Assert.IsType<ToolError>(McpJsonRpc.ExceptionForRpcResponseError(-32050, "server", "echo", parameters, McpJsonRpc.McpServerError), exactMatch: true);
        Assert.IsType<ToolError>(McpJsonRpc.ExceptionForRpcResponseError(-32099, "tool", "echo", parameters, McpJsonRpc.SandboxToolsServerError), exactMatch: true);
        Assert.IsType<InvalidOperationException>(McpJsonRpc.ExceptionForRpcResponseError(-32098, "unexpected", "echo", parameters, McpJsonRpc.SandboxToolsServerError));
        var bug = Assert.IsType<InvalidOperationException>(McpJsonRpc.ExceptionForRpcResponseError(-32601, "not found", "echo", parameters, McpJsonRpc.McpServerError));
        Assert.Equal("Error executing tool command  echo(message: hi, count: 2): code=-32601 not found", bug.Message);
        Assert.Equal("Error executing tool command: code=-32700 parse", McpJsonRpc.ExceptionForRpcResponseError(-32700, "parse", "echo", null, McpJsonRpc.McpServerError).Message);

        Assert.Equal(5, McpJsonRpc.ParseResponse("""{"jsonrpc": "2.0", "id": 1, "result": 5}""", "m", null, McpJsonRpc.McpServerError)!.GetValue<int>());
        Assert.Null(McpJsonRpc.ParseResponse("""{"jsonrpc": "2.0", "id": 1, "result": null}""", "m", null, McpJsonRpc.McpServerError));
        Assert.Equal("nope", Assert.IsType<ToolError>(Assert.Throws<ToolError>(() => McpJsonRpc.ParseResponse("""{"jsonrpc": "2.0", "id": 1, "error": {"code": -32603, "message": "nope"}}""", "m", null, McpJsonRpc.McpServerError))).Message);
        Assert.Contains("Unexpected JSON RPC response to request m(): garbage", Assert.Throws<InvalidOperationException>(() => McpJsonRpc.ParseResponse("garbage", "m", null, McpJsonRpc.McpServerError)).Message);

        var request = McpJsonRpc.CreateRequest("mcp_send_request", new JsonObject { ["session_id"] = 1, ["request"] = new JsonObject { ["params"] = null, ["method"] = "x" } }, false);
        Assert.Equal("""{"jsonrpc":"2.0","method":"mcp_send_request","params":{"session_id":1,"request":{"method":"x"}},"id":""", request.ToJsonString()[..^4]);
        Assert.Equal("""{"jsonrpc":"2.0","method":"n"}""", McpJsonRpc.CreateRequest("n", new JsonObject(), true).ToJsonString());
    }

    [Theory]
    [InlineData("get_status", "get_*", true)]
    [InlineData("get_status", "GET_*", false)]
    [InlineData("a.b", "a?b", true)]
    [InlineData("a.b", "a[.]b", true)]
    [InlineData("abc", "a[!b]c", false)]
    [InlineData("a-c", "a[!b]c", true)]
    [InlineData("x*y", "x[*]y", true)]
    [InlineData("x*y", "x*y", true)]
    [InlineData("", "*", true)]
    [InlineData("a/b", "*", true)]
    [InlineData("abc", "[a-c]bc", true)]
    [InlineData("]", "[]]", true)]
    [InlineData("a", "[!]a]", false)]
    [InlineData("foo.txt", "*.txt", true)]
    [InlineData("foo.txt", "*.TXT", false)]
    [InlineData("a\\b", "a\\b", true)]
    [InlineData("ab", "a\\b", false)]
    [InlineData("a[b", "a[b", true)]
    [InlineData("maps_geocode", "maps_geocode", true)]
    public void fnmatch_matches_python(string name, string pattern, bool expected) => Assert.Equal(expected, FnMatch.Match(name, pattern));

    // ----- a real stdio server (opt-in) -----

    [StdioServerFact]
    public async Task stdio_server_round_trip()
    {
        var commandLine = Environment.GetEnvironmentVariable("INSPECT_MCP_STDIO_SERVER")!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var server = Mcp.McpServerStdio(commandLine[0], commandLine[1..]);

        await using var connection = await McpConnection.ConnectAsync(server);
        var tools = await server.ToolsAsync();

        Assert.Contains(tools, t => t.Name == "echo");
        Assert.Equal("hello", SingleText(await Call(tools.Single(t => t.Name == "echo"), new { message = "hello" })));
    }
}

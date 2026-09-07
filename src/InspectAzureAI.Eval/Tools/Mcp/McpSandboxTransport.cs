using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Provider.Util;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace InspectAzureAI.Eval.Tools.Mcp;

/// <summary>Port of mcp's <c>StdioServerParameters</c> as sent to the sandbox in <c>mcp_launch_server</c>.</summary>
/// <param name="Command">The executable to run to start the server.</param>
/// <param name="Args">Command line arguments to pass to the executable.</param>
/// <param name="Cwd">The working directory to use when spawning the process.</param>
/// <param name="Env">Environment variables added to the platform default set when spawning the process.</param>
public sealed record McpStdioServerParameters(string Command, IReadOnlyList<string>? Args = null, string? Cwd = null, IReadOnlyDictionary<string, string>? Env = null)
{
    /// <summary>Port of <c>StdioServerParameters.model_dump()</c>: the wire shape the sandbox tools expect.</summary>
    public JsonObject ToJson() => new()
    {
        ["command"] = Command,
        ["args"] = new JsonArray((Args ?? []).Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
        ["env"] = Env is null ? null : new JsonObject(Env.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
        ["cwd"] = Cwd,
        ["encoding"] = "utf-8",
        ["encoding_error_handler"] = "strict",
    };
}

/// <summary>
/// The JSON-RPC carrier the sandbox transport speaks over (Python's <c>SandboxJSONRPCTransport</c> callable):
/// one exec of the sandbox-tools CLI per message. <see cref="McpSandboxExecRpc"/> is the real thing; tests and
/// alternative injection schemes supply their own.
/// </summary>
public interface IMcpSandboxRpc
{
    /// <summary>Sends one JSON-RPC request (or notification) and returns the raw response text (empty for a notification).</summary>
    /// <param name="method">JSON-RPC method (<c>mcp_launch_server</c>, <c>mcp_send_request</c>, <c>mcp_send_notification</c>, <c>mcp_kill_server</c>).</param>
    /// <param name="parameters">Method parameters.</param>
    /// <param name="isNotification">Whether no response is expected.</param>
    /// <param name="timeout">Bound on the exec.</param>
    /// <param name="cancellationToken">Cancels the exec.</param>
    Task<string> CallAsync(string method, JsonNode? parameters, bool isNotification, TimeSpan? timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Port of <c>util/_sandbox/_json_rpc_transport.py</c> <c>SandboxJSONRPCTransport</c> for the MCP methods: runs
/// <c>{cli} exec</c> in the sandbox with the JSON-RPC request on stdin and returns stdout. A failed exec is an
/// <see cref="InvalidOperationException"/> carrying stderr (or stdout when that is where the CLI wrote its
/// diagnostic). The chunked-response continuation protocol for oversized responses is not ported.
/// </summary>
public sealed class McpSandboxExecRpc(ISandboxEnvironment sandbox, string cli = McpSandboxExecRpc.DefaultCli) : IMcpSandboxRpc
{
    /// <summary>Python's <c>SANDBOX_CLI</c>: the sandbox tools launcher as injected by Inspect.</summary>
    public const string DefaultCli = "/var/tmp/.da7be258e003d428/inspect-sandbox-tools";

    /// <summary>The sandbox commands run in.</summary>
    public ISandboxEnvironment Sandbox { get; } = sandbox ?? throw new ArgumentNullException(nameof(sandbox));

    /// <summary>Path of the sandbox tools CLI inside the sandbox.</summary>
    public string Cli { get; } = string.IsNullOrEmpty(cli) ? throw new ArgumentException("cli must not be empty", nameof(cli)) : cli;

    /// <inheritdoc />
    public async Task<string> CallAsync(string method, JsonNode? parameters, bool isNotification, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var request = McpJsonRpc.CreateRequest(method, parameters, isNotification).ToJsonString();
        var result = await Sandbox.ExecAsync([Cli, "exec"], input: request, timeout: timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            var detail = result.Stderr.Length > 0 ? result.Stderr : result.Stdout.Length > 0 ? result.Stdout : "(no output captured)";
            throw new InvalidOperationException($"Sandbox.exec failure executing {McpJsonRpc.RpcCallDescription(method, parameters)}: {detail}");
        }

        return result.Stdout;
    }
}

/// <summary>
/// Port of <c>tool/_mcp/_sandbox.py</c> <c>sandbox_client</c>: an SDK client transport whose server runs inside an
/// Inspect sandbox. Connecting launches the server there (<c>mcp_launch_server</c>); each client request is
/// relayed with <c>mcp_send_request</c> and its response fed back, notifications with
/// <c>mcp_send_notification</c>; disposing kills the server (<c>mcp_kill_server</c>, best effort). Server-initiated
/// messages are not relayed (as in Python), so sampling is unavailable over this transport.
/// </summary>
public sealed class McpSandboxClientTransport : IClientTransport
{
    /// <summary>Default per-request timeout when a sandbox server is created without one (Python's <c>DEFAULT_SANDBOX_TIMEOUT</c>).</summary>
    public static readonly TimeSpan DefaultSandboxTimeout = TimeSpan.FromSeconds(180);

    /// <summary>Bound on the best-effort server shutdown at teardown, independent of the per-request timeout.</summary>
    public static readonly TimeSpan KillServerTimeout = TimeSpan.FromSeconds(30);

    private readonly McpStdioServerParameters _server;
    private readonly Func<CancellationToken, Task<IMcpSandboxRpc>> _rpc;

    /// <summary>Creates the transport.</summary>
    /// <param name="server">The server command to launch in the sandbox.</param>
    /// <param name="rpc">Resolves the carrier on connect (Python resolves the sandbox with injected tools at that point).</param>
    /// <param name="timeout">Per-request timeout; null is <see cref="DefaultSandboxTimeout"/>.</param>
    /// <param name="name">Transport name for diagnostics.</param>
    public McpSandboxClientTransport(McpStdioServerParameters server, Func<CancellationToken, Task<IMcpSandboxRpc>> rpc, TimeSpan? timeout = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(rpc);
        _server = server;
        _rpc = rpc;
        Timeout = timeout ?? DefaultSandboxTimeout;
        Name = name ?? "sandbox";
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Per-request timeout applied to every carrier exec.</summary>
    public TimeSpan Timeout { get; }

    /// <inheritdoc />
    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var rpc = await _rpc(cancellationToken).ConfigureAwait(false);
        var result = await ExecRequestAsync(rpc, "mcp_launch_server", new JsonObject { ["server_params"] = _server.ToJson() }, Timeout, cancellationToken).ConfigureAwait(false);
        if (result is not JsonValue value || !value.TryGetValue<long>(out var sessionId))
        {
            throw new InvalidOperationException($"Expected an integer session id from mcp_launch_server, got {result?.ToJsonString() ?? "null"}");
        }

        return new McpSandboxTransport(rpc, sessionId, Timeout);
    }

    internal static async Task<JsonNode?> ExecRequestAsync(IMcpSandboxRpc rpc, string method, JsonNode? parameters, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var response = await rpc.CallAsync(method, parameters, isNotification: false, timeout, cancellationToken).ConfigureAwait(false);
        return McpJsonRpc.ParseResponse(response, method, parameters, McpJsonRpc.SandboxToolsServerError);
    }

    internal static async Task ExecNotificationAsync(IMcpSandboxRpc rpc, string method, JsonNode? parameters, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var stdout = await rpc.CallAsync(method, parameters, isNotification: true, timeout, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            throw new InvalidOperationException($"Unexpected response to a Notification: {McpJsonRpc.RpcCallDescription(method, parameters)}: {stdout}");
        }
    }
}

/// <summary>The established sandbox session: the read side is a channel the relayed responses are written to.</summary>
internal sealed class McpSandboxTransport(IMcpSandboxRpc rpc, long sessionId, TimeSpan timeout) : ITransport
{
    private readonly Channel<JsonRpcMessage> _incoming = Channel.CreateUnbounded<JsonRpcMessage>(new UnboundedChannelOptions { SingleReader = true });

    // Python relays messages from a single writer task, so requests are serialized here too.
    private readonly SemaphoreSlim _writer = new(1, 1);

    private int _disposed;

    public string? SessionId => sessionId.ToString(CultureInfo.InvariantCulture);

    public ChannelReader<JsonRpcMessage> MessageReader => _incoming.Reader;

    public async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            switch (message)
            {
                case JsonRpcRequest request:
                    await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case JsonRpcNotification notification:
                    await SendNotificationAsync(notification, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new NotSupportedException($"Unexpected message type {message.GetType().Name}: the sandbox transport relays client requests and notifications only.");
            }
        }
        finally
        {
            _writer.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Best-effort shutdown: bounded by its own deadline and never thrown, so a slow or broken carrier cannot
        // stall or mask the failure that is tearing the session down.
        try
        {
            using var cts = new CancellationTokenSource(McpSandboxClientTransport.KillServerTimeout);
            var result = await McpSandboxClientTransport.ExecRequestAsync(rpc, "mcp_kill_server", new JsonObject { ["session_id"] = sessionId }, McpSandboxClientTransport.KillServerTimeout, cts.Token).ConfigureAwait(false);
            if (result is not null)
            {
                throw new InvalidOperationException($"Expected a null result from mcp_kill_server, got {result.ToJsonString()}");
            }
        }
        catch (Exception ex)
        {
            ProviderLogger.Warning($"Sandbox MCP server shutdown failed ({ex.GetType().Name}): {ex.Message}");
        }
        finally
        {
            _incoming.Writer.TryComplete();
        }
    }

    private async Task SendRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        JsonRpcMessage response;
        try
        {
            var result = await McpSandboxClientTransport.ExecRequestAsync(
                rpc,
                "mcp_send_request",
                new JsonObject { ["session_id"] = sessionId, ["request"] = Serialize(request) },
                timeout,
                cancellationToken).ConfigureAwait(false);
            response = JsonSerializer.Deserialize<JsonRpcMessage>(result, McpJsonUtilities.DefaultOptions)
                ?? throw new InvalidOperationException("mcp_send_request returned no JSON-RPC message.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A carrier failure must not tear the session down (the client would then see a bare cancellation);
            // it becomes a JSON-RPC error on the same request id, which the session maps to a ToolError.
            var errorMessage = ex is TimeoutException
                ? "MCP request timed out before completing."
                : $"MCP request failed before completing ({ex.GetType().Name}): {ex.Message}";
            response = new JsonRpcError
            {
                Id = request.Id,
                Error = new JsonRpcErrorDetail { Code = (int)McpErrorCode.InternalError, Message = errorMessage },
            };
        }

        _incoming.Writer.TryWrite(response);
    }

    private async Task SendNotificationAsync(JsonRpcNotification notification, CancellationToken cancellationToken)
    {
        try
        {
            await McpSandboxClientTransport.ExecNotificationAsync(
                rpc,
                "mcp_send_notification",
                new JsonObject { ["session_id"] = sessionId, ["notification"] = Serialize(notification) },
                timeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fire-and-forget per JSON-RPC: no id to attach an error to and the client does not block on it.
            ProviderLogger.Warning($"Sandbox MCP notification dropped after transport failure ({ex.GetType().Name}): {ex.Message}");
        }
    }

    private static JsonNode? Serialize(JsonRpcMessage message) => JsonSerializer.SerializeToNode<JsonRpcMessage>(message, McpJsonUtilities.DefaultOptions);
}

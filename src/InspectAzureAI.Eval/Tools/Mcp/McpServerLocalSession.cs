using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents.Bridge;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace InspectAzureAI.Eval.Tools.Mcp;

/// <summary>
/// Port of <c>tool/_mcp/_local.py</c> <c>MCPServerLocalSession</c>: the connection state of one
/// <see cref="McpServerLocal"/> in one async flow. <see cref="EnterAsync"/> opens a reference-counted connection
/// that <see cref="ToolsAsync"/> and the tools it returns reuse; without one, each listing or call opens a
/// transient connection. The raw tool list is cached while connected and dropped when the connection closes.
/// </summary>
public sealed class McpServerLocalSession : McpServer
{
    private static readonly Implementation ClientInfo = new()
    {
        Name = "InspectAzureAI",
        Version = typeof(McpServerLocalSession).Assembly.GetName().Version?.ToString() ?? "0.0.0",
    };

    private readonly Func<IClientTransport> _transport;

    // Child flows share their parent's session (see McpServerLocal), so parallel tool stages can race
    // EnterAsync/ExitAsync; the gate keeps the refcount and the connection consistent.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private int _refcount;
    private McpClient? _client;
    private IReadOnlyList<Tool>? _cachedToolList;

    /// <summary>Creates a session; see <see cref="McpServerLocal(Func{IClientTransport}, string, bool, TimeSpan?)"/> for the arguments.</summary>
    public McpServerLocalSession(Func<IClientTransport> transport, string name, bool events, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrEmpty(name);
        _transport = transport;
        Name = name;
        Events = events;
        Timeout = timeout;
    }

    /// <summary>Human readable server name.</summary>
    public string Name { get; }

    /// <summary>Whether server-initiated events (sampling) would be answered; carried for parity with Python, see <see cref="McpServerLocal"/>.</summary>
    public bool Events { get; }

    /// <summary>Per-call bound on waiting for a tool result; null is unbounded.</summary>
    public TimeSpan? Timeout { get; }

    /// <summary>Number of open <see cref="EnterAsync"/> references.</summary>
    public int RefCount => _refcount;

    /// <summary>Whether a connection is currently held open by <see cref="EnterAsync"/>.</summary>
    public bool IsConnected => _client is not null;

    /// <summary>Whether the raw tool list is cached (it is dropped when the connection closes).</summary>
    public bool HasCachedToolList => _cachedToolList is not null;

    /// <inheritdoc />
    public override async Task EnterAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                _refcount++;
                return;
            }

            try
            {
                _client = await ConnectAsync(cancellationToken).ConfigureAwait(false);
                _refcount = 1;
            }
            catch
            {
                // Leave the object fully disconnected rather than half-built, so a retry in this flow creates a
                // fresh connection instead of adopting a broken one.
                await CloseAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public override async Task ExitAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_refcount <= 0)
            {
                throw new InvalidOperationException($"ExitAsync on MCP server '{Name}' without a matching EnterAsync.");
            }

            _refcount--;
            if (_refcount == 0)
            {
                await CloseAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<ToolDef>> ToolsAsync(CancellationToken cancellationToken = default)
    {
        var tools = _cachedToolList;
        if (tools is null)
        {
            tools = await WithClientAsync(
                async (client, ct) => (await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false)).Select(t => t.ProtocolTool).ToArray(),
                cancellationToken).ConfigureAwait(false);
            _cachedToolList = tools;
        }

        return tools.Select(ToolDefFromMcpTool).ToArray();
    }

    /// <summary>
    /// Port of <c>_tool_def_from_mcp_tool</c>: the server's <c>inputSchema</c> passes through to
    /// <see cref="Provider.Core.ToolParams"/> (a parameter without a description gets its name as one) and calls
    /// go through this session, bounded by <see cref="Timeout"/>.
    /// </summary>
    internal ToolDef ToolDefFromMcpTool(Tool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var parameters = BridgeJson.ToolParamsFromSchema(JsonObject.Create(tool.InputSchema));
        parameters = parameters with
        {
            Properties = parameters.Properties.ToDictionary(
                kv => kv.Key,
                kv => string.IsNullOrEmpty(kv.Value.Description) ? kv.Value with { Description = kv.Key } : kv.Value,
                StringComparer.Ordinal),
        };
        return new ToolDef(tool.Name, tool.Description ?? "", parameters, (arguments, ct) => ExecuteAsync(tool, arguments, ct));
    }

    private async Task<ToolResult> ExecuteAsync(Tool tool, JsonObject arguments, CancellationToken cancellationToken)
    {
        var args = arguments.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal);
        try
        {
            return await WithClientAsync(
                async (client, ct) =>
                {
                    // Bound the wait on a tool response with the configured timeout, otherwise a lost transport
                    // response (e.g. the sandbox carrier exec timed out and its error never arrived) would hang
                    // the call until the sample's working-time cap.
                    using var timeout = Timeout is { } limit ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
                    if (timeout is not null)
                    {
                        timeout.CancelAfter(Timeout!.Value);
                    }

                    CallToolResult result;
                    try
                    {
                        result = await client.CallToolAsync(tool.Name, args, cancellationToken: timeout?.Token ?? ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException ex) when (timeout is { IsCancellationRequested: true } && !ct.IsCancellationRequested)
                    {
                        throw new TimeoutException($"Timed out while waiting for response to tools/call ({tool.Name}).", ex);
                    }
                    catch (McpProtocolException ex)
                    {
                        // Server errors (e.g. -32603) are converted so they make it back to the model.
                        throw McpJsonRpc.ExceptionForRpcResponseError((int)ex.ErrorCode, ex.Message, tool.Name, arguments, McpJsonRpc.McpServerError, ex);
                    }

                    var content = result.Content ?? [];
                    if (result.IsError == true)
                    {
                        throw new ToolError(McpContent.ToolResultAsText(content));
                    }

                    return ToolResult.FromContents(McpContent.AsInspectContentList(content));
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new ToolError($"Tool '{tool.Name}' timed out before completing.");
        }
    }

    // Port of _client_session: the held connection when entered, otherwise a transient one for this call.
    private async Task<T> WithClientAsync<T>(Func<McpClient, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        if (_client is { } client)
        {
            return await action(client, cancellationToken).ConfigureAwait(false);
        }

        var transient = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using (transient.ConfigureAwait(false))
        {
            return await action(transient, cancellationToken).ConfigureAwait(false);
        }
    }

    // Python installs a sampling callback here when `events` is set and a model is active. The C# SDK (2.2.0)
    // marks the sampling API obsolete (deprecated by the 2026-07-28 spec, SEP-2577), so no handler is wired and
    // `Events` is carried for parity only.
    private async Task<McpClient> ConnectAsync(CancellationToken cancellationToken)
    {
        var options = new McpClientOptions { ClientInfo = ClientInfo };
        return await McpClient.CreateAsync(_transport(), options, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // Drops the connection and the cached tool list, so a session reused across two connection scopes in the
    // same flow re-fetches from the server rather than serving tools bound to a connection that no longer exists.
    private async Task CloseAsync()
    {
        try
        {
            if (_client is { } client)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _client = null;
            _cachedToolList = null;
        }
    }
}

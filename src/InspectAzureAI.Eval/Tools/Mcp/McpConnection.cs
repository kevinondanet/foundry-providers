using System.Runtime.ExceptionServices;

namespace InspectAzureAI.Eval.Tools.Mcp;

/// <summary>
/// Port of <c>tool/_mcp/connection.py</c> <c>mcp_connection</c>: a scope that keeps the MCP servers referenced by a
/// set of tools connected. Every <see cref="McpServer"/> in the tools (directly, or behind a
/// <see cref="McpToolSourceLocal"/> from <see cref="Mcp.McpTools"/>) is entered on <see cref="ConnectAsync(IEnumerable{object}, CancellationToken)"/>
/// and exited on <see cref="DisposeAsync"/>, so a stateful server keeps its state across the calls in between.
/// Use as <c>await using var connection = await McpConnection.ConnectAsync(tools);</c>.
/// </summary>
public sealed class McpConnection : IAsyncDisposable
{
    private readonly IReadOnlyList<McpServer> _entered;
    private int _disposed;

    private McpConnection(IReadOnlyList<McpServer> entered) => _entered = entered;

    /// <summary>The server sessions this connection holds open, in entry order.</summary>
    public IReadOnlyList<McpServer> Servers => _entered;

    /// <summary>
    /// Connects to the servers found in <paramref name="tools"/>: entries may be <see cref="ToolDef"/>s (ignored),
    /// <see cref="IToolSource"/>s (only MCP-backed ones connect) or <see cref="McpServer"/>s; anything else is an
    /// <see cref="ArgumentException"/>. A failure entering one server exits those already entered.
    /// </summary>
    public static Task<McpConnection> ConnectAsync(IEnumerable<object> tools, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tools);

        // Sessions are resolved here, synchronously, so a per-flow session lands in the caller's async flow (see McpServerLocal).
        var sessions = new List<McpServer>();
        foreach (var tool in tools)
        {
            switch (tool)
            {
                case McpServer server:
                    sessions.Add(server.ResolveSession());
                    break;
                case McpToolSourceLocal source:
                    sessions.Add(source.Server.ResolveSession());
                    break;
                case IToolSource or ToolDef:
                    break;
                case null:
                    throw new ArgumentException("tools contains a null entry.", nameof(tools));
                default:
                    throw new ArgumentException($"Unexpected tools entry of type {tool.GetType().Name}: expected ToolDef, IToolSource or McpServer.", nameof(tools));
            }
        }

        return EnterAllAsync(sessions, cancellationToken);
    }

    /// <summary>Connects to the servers behind a single tool source.</summary>
    public static Task<McpConnection> ConnectAsync(IToolSource tools, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return ConnectAsync([tools], cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await ExitAllAsync(_entered).ConfigureAwait(false);
    }

    private static async Task<McpConnection> EnterAllAsync(List<McpServer> sessions, CancellationToken cancellationToken)
    {
        var entered = new List<McpServer>();
        try
        {
            foreach (var session in sessions)
            {
                await session.EnterAsync(cancellationToken).ConfigureAwait(false);
                entered.Add(session);
            }
        }
        catch
        {
            await ExitAllAsync(entered).ConfigureAwait(false);
            throw;
        }

        return new McpConnection(entered);
    }

    // Exits in reverse order (AsyncExitStack semantics); every exit runs even when one throws.
    private static async Task ExitAllAsync(IReadOnlyList<McpServer> entered)
    {
        List<Exception>? errors = null;
        for (var i = entered.Count - 1; i >= 0; i--)
        {
            try
            {
                await entered[i].ExitAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }
        }

        if (errors is { Count: 1 })
        {
            ExceptionDispatchInfo.Capture(errors[0]).Throw();
        }

        if (errors is { Count: > 1 })
        {
            throw new AggregateException("Disconnecting MCP servers failed.", errors);
        }
    }
}

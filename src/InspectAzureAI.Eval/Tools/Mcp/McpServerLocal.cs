using ModelContextProtocol.Client;

namespace InspectAzureAI.Eval.Tools.Mcp;

/// <summary>
/// Port of <c>tool/_mcp/_local.py</c> <c>MCPServerLocal</c>: a server driven from this process over stdio, HTTP,
/// SSE or the sandbox transport. Python keeps one <c>MCPServerLocalSession</c> per running async task; the .NET
/// analogue is one <see cref="McpServerLocalSession"/> per async flow held in an <see cref="AsyncLocal{T}"/>:
/// a session created in a flow is seen by that flow and by the flows it starts afterwards, never by its parent.
/// The entry points below are deliberately not <c>async</c> so the session is resolved in the caller's
/// execution context rather than in a child context that is discarded on return.
/// </summary>
public sealed class McpServerLocal : McpServer
{
    private readonly Func<IClientTransport> _transport;
    private readonly AsyncLocal<McpServerLocalSession?> _sessions = new();

    /// <summary>Creates a server over <paramref name="transport"/>, a factory invoked once per connection.</summary>
    /// <param name="transport">Creates the SDK client transport for a new connection.</param>
    /// <param name="name">Human readable server name (used in messages and logs).</param>
    /// <param name="events">Python's <c>events</c>: whether server-initiated sampling would be answered with the active model (false for the sandbox transport). Sampling is not wired in this port because the C# SDK marks its API obsolete; the flag is carried for parity.</param>
    /// <param name="timeout">Per-call bound on waiting for a tool result (the sandbox server's <c>timeout</c>); null is unbounded.</param>
    public McpServerLocal(Func<IClientTransport> transport, string name, bool events, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (timeout is { } limit && limit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "MCP server timeout must be positive.");
        }

        _transport = transport;
        Name = name;
        Events = events;
        Timeout = timeout;
    }

    /// <summary>Human readable server name.</summary>
    public string Name { get; }

    /// <summary>Python's <c>events</c> flag (sampling is not wired in this port; see the constructor).</summary>
    public bool Events { get; }

    /// <summary>Per-call bound on waiting for a tool result; null is unbounded.</summary>
    public TimeSpan? Timeout { get; }

    /// <summary>The current async flow's session, created on first use.</summary>
    public McpServerLocalSession Session
    {
        get
        {
            var session = _sessions.Value;
            if (session is null)
            {
                session = new McpServerLocalSession(_transport, Name, Events, Timeout);
                _sessions.Value = session;
            }

            return session;
        }
    }

    /// <inheritdoc />
    public override Task<IReadOnlyList<ToolDef>> ToolsAsync(CancellationToken cancellationToken = default) => Session.ToolsAsync(cancellationToken);

    /// <inheritdoc />
    public override Task EnterAsync(CancellationToken cancellationToken = default) => Session.EnterAsync(cancellationToken);

    /// <inheritdoc />
    public override Task ExitAsync() => Session.ExitAsync();

    internal override McpServer ResolveSession() => Session;
}

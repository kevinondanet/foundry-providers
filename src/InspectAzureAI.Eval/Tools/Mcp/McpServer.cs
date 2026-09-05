namespace InspectAzureAI.Eval.Tools.Mcp;

/// <summary>
/// Port of <c>tool/_mcp/_types.py</c> <c>MCPServer</c>: a Model Context Protocol server as a source of tools and
/// an async context (<see cref="EnterAsync"/> / <see cref="ExitAsync"/>, Python's <c>__aenter__</c> /
/// <c>__aexit__</c>) whose connection <see cref="McpConnection"/> keeps open across tool calls. Pass one to
/// <see cref="Mcp.McpTools"/> to select a subset of its tools.
/// </summary>
public abstract class McpServer : IToolSource
{
    /// <summary>List of all tools provided by this server.</summary>
    public abstract Task<IReadOnlyList<ToolDef>> ToolsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of <c>__aenter__</c>: connects (reference counted) so later listings and calls reuse one session.
    /// Pair every call with <see cref="ExitAsync"/>; <see cref="McpConnection"/> does this for a set of tools.
    /// </summary>
    public abstract Task EnterAsync(CancellationToken cancellationToken = default);

    /// <summary>Port of <c>__aexit__</c>: releases one reference; the last release disconnects. Takes no token because cleanup must also run on cancellation.</summary>
    public abstract Task ExitAsync();

    /// <summary>
    /// The object <see cref="McpConnection"/> enters for the current async flow. Called synchronously, before any
    /// await, so a per-flow (AsyncLocal) session installed here persists in the caller's flow.
    /// </summary>
    internal virtual McpServer ResolveSession() => this;
}

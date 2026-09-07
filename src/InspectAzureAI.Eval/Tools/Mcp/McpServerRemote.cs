using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tools.Mcp;

/// <summary>
/// Port of <c>tool/_mcp/_remote.py</c> <c>MCPServerRemote</c>: an HTTP/SSE server the model provider calls itself
/// (<c>execution="remote"</c>). Its only tool is a marker, <c>mcp_server_{name}</c>, whose
/// <see cref="ToolDef.Options"/> carry the server config for providers that support remote MCP (OpenAI and
/// Anthropic in Python; the Azure AI provider never sends tool options). No connection is managed, so
/// <see cref="EnterAsync"/> and <see cref="ExitAsync"/> are no-ops.
/// </summary>
public sealed class McpServerRemote(McpServerConfigHttp config) : McpServer
{
    /// <summary>Name prefix of the marker tool.</summary>
    public const string ToolPrefix = "mcp_server_";

    /// <summary>The server configuration carried in the marker tool's options.</summary>
    public McpServerConfigHttp Config { get; } = config ?? throw new ArgumentNullException(nameof(config));

    /// <inheritdoc />
    public override Task<IReadOnlyList<ToolDef>> ToolsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ToolDef>>([McpServerTool(Config)]);

    /// <inheritdoc />
    public override Task EnterAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task ExitAsync() => Task.CompletedTask;

    /// <summary>Port of <c>mcp_server_tool</c>: the marker tool; executing it directly is an <see cref="InvalidOperationException"/>.</summary>
    public static ToolDef McpServerTool(McpServerConfigHttp config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var name = ToolPrefix + config.Name;
        return new ToolDef(name, name, new ToolParams(), (_, _) => throw new InvalidOperationException("MCPServerTool should not be called directly"))
        {
            Options = config.ToJson(),
        };
    }

    /// <summary>Port of <c>is_mcp_server_tool</c>: whether a tool description is a remote MCP server marker.</summary>
    public static bool IsMcpServerTool(ToolInfo tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return tool.Name.StartsWith(ToolPrefix, StringComparison.Ordinal) && tool.Options is not null;
    }
}

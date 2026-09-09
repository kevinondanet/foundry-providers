namespace InspectAzureAI.Eval.Tools.Mcp;

/// <summary>
/// Port of <c>model/_call_tools.py</c> <c>resolve_tools</c>: flattens a list of tools and tool sources into the
/// <see cref="ToolDef"/>s they currently provide, in list order (a <see cref="ToolDef"/> stands for itself, any
/// other <see cref="IToolSource"/> is asked for its tools). Python resolves on every generate, so an MCP server
/// that changes its tool list is seen on the next turn; callers that need the servers to stay connected across
/// several resolutions wrap them in an <see cref="McpConnection"/>.
/// </summary>
public static class ToolSources
{
    /// <summary>Resolves <paramref name="tools"/> to the tools they currently provide.</summary>
    public static async Task<IReadOnlyList<ToolDef>> ResolveAsync(IEnumerable<IToolSource> tools, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var resolved = new List<ToolDef>();
        foreach (var tool in tools)
        {
            switch (tool)
            {
                case ToolDef def:
                    resolved.Add(def);
                    break;
                case null:
                    throw new ArgumentException("tools contains a null entry.", nameof(tools));
                default:
                    resolved.AddRange(await tool.ToolsAsync(cancellationToken).ConfigureAwait(false));
                    break;
            }
        }

        return resolved;
    }

    /// <summary>Port of the top-level <c>ToolSource</c> case of <c>resolve_tools</c>: the tools a single source currently provides.</summary>
    public static Task<IReadOnlyList<ToolDef>> ResolveAsync(IToolSource tools, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return ResolveAsync([tools], cancellationToken);
    }
}

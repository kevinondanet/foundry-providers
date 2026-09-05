namespace InspectAzureAI.Eval.Tools.Mcp;

/// <summary>
/// Port of <c>tool/_tool.py</c> <c>ToolSource</c>: a dynamic provider of tools. Python's generation entry points
/// accept a <c>ToolSource</c> alongside <c>Tool</c>/<c>ToolDef</c>; here callers resolve one with
/// <see cref="ToolsAsync"/> (inside an <see cref="McpConnection"/> scope when the server keeps state) and pass
/// the resulting <see cref="ToolDef"/>s on to solvers and agents.
/// </summary>
public interface IToolSource
{
    /// <summary>The tools currently provided by this source.</summary>
    Task<IReadOnlyList<ToolDef>> ToolsAsync(CancellationToken cancellationToken = default);
}

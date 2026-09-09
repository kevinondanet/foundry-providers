using InspectAzureAI.Eval.Tools.Mcp;

namespace InspectAzureAI.Eval.Tools;

/// <summary>
/// A <see cref="ToolDef"/> is also an <see cref="IToolSource"/> of itself, so a mixed list of tools and tool
/// sources (Python's <c>Sequence[Tool | ToolDef | ToolSource]</c>, accepted by <c>react()</c> and
/// <c>Model.generate_loop</c>) types as <c>IReadOnlyList&lt;IToolSource&gt;</c> and a plain
/// <c>IReadOnlyList&lt;ToolDef&gt;</c> converts to it covariantly. Resolve such a list with
/// <see cref="ToolSources.ResolveAsync(IEnumerable{IToolSource}, CancellationToken)"/>.
/// </summary>
public sealed partial record ToolDef : IToolSource
{
    /// <inheritdoc />
    Task<IReadOnlyList<ToolDef>> IToolSource.ToolsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ToolDef>>([this]);
    }
}

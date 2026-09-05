using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tools;

/// <summary>Port of the <c>Tool</c> callable of <c>tool/_tool.py</c>: receives the parsed JSON arguments.</summary>
public delegate Task<ToolResult> ToolExecute(JsonObject arguments, CancellationToken cancellationToken);

/// <summary>Port of <c>tool/_tool_def.py</c> <c>ToolDef</c>: a named, described, schema-carrying executable tool.</summary>
public sealed partial record ToolDef(string Name, string Description, ToolParams Parameters, ToolExecute Execute)
{
    /// <summary>Whether calls may run concurrently with other parallel-safe calls (a false call is a barrier).</summary>
    public bool Parallel { get; init; } = true;

    /// <summary>Output byte limit for this tool (wins over the caller's); null defers to the caller, 0 disables truncation.</summary>
    public int? MaxOutput { get; init; }

    public JsonObject? Options { get; init; }

    /// <summary>
    /// Port of a <c>ToolDef</c> wrapping an <c>AgentTool</c>: set by <c>Agents.Handoff</c>, and makes
    /// <see cref="ToolExecutor"/> hand the conversation to the agent instead of calling <see cref="Execute"/>.
    /// </summary>
    public Agents.AgentHandoff? Handoff { get; init; }

    /// <summary>Port of <c>ToolInfo</c> construction from a <c>ToolDef</c>: what the model sees.</summary>
    public ToolInfo ToInfo() => new(Name, Description) { Parameters = Parameters, Options = Options };
}

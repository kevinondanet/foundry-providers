using InspectAzureAI.Eval.Tools;

namespace InspectAzureAI.Eval.Agents.Bridge;

/// <summary>
/// Port of <c>tool/_mcp/_tools_bridge/bridge.py</c> <c>BridgedToolsSpec</c>: host tools exposed to a sandboxed agent
/// as the MCP server <paramref name="Name"/>, served by <see cref="SandboxAgentBridge"/> at <c>/mcp/{Name}</c>.
/// </summary>
/// <param name="Name">Server name: a URL path segment and a CLI config key, so it must match <see cref="NamePattern"/>.</param>
/// <param name="Tools">The host tools, keyed by <see cref="ToolDef.Name"/> (unique within the spec).</param>
public sealed record BridgedToolsSpec(string Name, IReadOnlyList<ToolDef> Tools)
{
    /// <summary>The accepted server names (stricter than Python, which accepts any string).</summary>
    public const string NamePattern = "^[A-Za-z0-9][A-Za-z0-9_-]*$";
}

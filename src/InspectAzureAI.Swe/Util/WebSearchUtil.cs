namespace InspectAzureAI.Swe.Util;

/// <summary>
/// Port of inspect_swe <c>_util/websearch.py</c>. CLI agents implement web search through a nested model request
/// that carries the provider's native tool, so configuring the CLI alone cannot withhold it; each agent turns its
/// effective setting into the bridge's web-search grant, which is where the capability is granted or withheld.
/// </summary>
public static class WebSearchUtil
{
    /// <summary>
    /// Port of <c>web_search_tool_disallowed</c>: whether <paramref name="toolName"/> appears in a
    /// <c>disallowed_tools</c> list, either bare (<c>WebSearch</c>) or in the scoped <c>WebSearch(...)</c> form.
    /// </summary>
    public static bool ToolDisallowed(IReadOnlyList<string>? disallowedTools, string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        if (disallowedTools is null)
        {
            return false;
        }

        var scoped = toolName + "(";
        foreach (var entry in disallowedTools)
        {
            if (entry is not null && (entry == toolName || entry.StartsWith(scoped, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}

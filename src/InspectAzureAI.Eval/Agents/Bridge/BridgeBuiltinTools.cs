using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Agents.Bridge;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Built-in (provider-executed) tools a bridged scaffold declares, and the grants that decide whether the served model
/// receives them. Port of the web-search grant of <c>sandbox_agent_bridge(web_search=...)</c>
/// (<c>agent/_bridge/sandbox/bridge.py</c>) and <c>relax_tool_choice_for_withheld</c> (<c>agent/_bridge/util.py</c>).
/// </summary>
/// <remarks>
/// A dialect parser turns a scaffold's native web search tool into a marker <see cref="ToolInfo"/>
/// (<see cref="WebSearchTool"/>); <see cref="ApplyGrants"/> resolves the markers for each generation. Web search is
/// served only when granted and the served model searches natively (Claude on the Anthropic route); otherwise the tool
/// is withheld with a warning. Python maps an ungranted-native search onto Inspect's own <c>web_search()</c> providers,
/// which this port does not do.
/// </remarks>
public static class BridgeBuiltinTools
{
    /// <summary>The <see cref="ToolInfo.Options"/> key a dialect parser puts on a scaffold-declared web search tool (value: the raw tool JSON).</summary>
    public const string WebSearchMarker = "__bridge_web_search__";

    /// <summary>Warned once when a declared web search is withheld because the bridge was not granted web search.</summary>
    public const string WebSearchNotGrantedWarning = "web search withheld from the bridged agent (not granted)";

    /// <summary>Warned once when a granted web search is withheld because the served model cannot search natively.</summary>
    public const string WebSearchUnsupportedWarning = "web search withheld from the bridged agent (the served model has no native web search on this route)";

    private static readonly string[] AnthropicOptionKeys = ["allowed_domains", "blocked_domains", "user_location", "max_uses"];

    /// <summary>The marker tool for a scaffold's web search tool: <c>web_search</c> with no parameters, carrying a copy of <paramref name="rawTool"/>.</summary>
    public static ToolInfo WebSearchTool(JsonObject rawTool)
    {
        ArgumentNullException.ThrowIfNull(rawTool);
        return new ToolInfo("web_search", "Search the web.")
        {
            Parameters = new ToolParams(),
            Options = new JsonObject { [WebSearchMarker] = rawTool.DeepClone() },
        };
    }

    /// <summary>Whether <paramref name="tool"/> is a web search marker produced by <see cref="WebSearchTool"/>.</summary>
    public static bool IsWebSearchMarker(ToolInfo tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return tool.Options?.ContainsKey(WebSearchMarker) == true;
    }

    /// <summary>
    /// Resolves the web search markers of one generation. When <paramref name="webSearch"/> is granted and the served
    /// model is Claude on an Anthropic API that supports the server tool
    /// (<see cref="AnthropicWebSearch.SupportsWebSearch"/>), each marker's options become <c>{"anthropic": {...}}</c>
    /// (domains, user location and max uses copied from the raw tool), which the Anthropic provider sends as its
    /// <c>web_search</c> server tool. Otherwise the marker is withheld with a one-time warning, and a
    /// <paramref name="toolChoice"/> naming it is relaxed to <c>auto</c>. Without markers the input is returned unchanged.
    /// </summary>
    public static (IReadOnlyList<ToolInfo> Tools, ToolChoice ToolChoice) ApplyGrants(Model model, IReadOnlyList<ToolInfo> tools, ToolChoice toolChoice, bool webSearch)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(toolChoice);
        if (!tools.Any(IsWebSearchMarker))
        {
            return (tools, toolChoice);
        }

        var native = webSearch
            && (model.Api is AnthropicFoundryModelApi or AnthropicModelApi)
            && AnthropicWebSearch.SupportsWebSearch(model.Api.ModelName);
        var granted = new List<ToolInfo>(tools.Count);
        var withheld = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (!IsWebSearchMarker(tool))
            {
                granted.Add(tool);
            }
            else if (native)
            {
                granted.Add(tool with { Options = new JsonObject { ["anthropic"] = AnthropicOptions(tool.Options![WebSearchMarker] as JsonObject) } });
            }
            else
            {
                withheld.Add(tool.Name);
                ProviderLogger.WarnOnce(webSearch ? WebSearchUnsupportedWarning : WebSearchNotGrantedWarning);
            }
        }

        if (toolChoice is ToolFunction function && withheld.Contains(function.Name) && !granted.Any(tool => tool.Name == function.Name))
        {
            toolChoice = ToolChoice.Auto;
        }

        return (granted, toolChoice);
    }

    /// <summary>
    /// The Anthropic server-tool options for a raw web search tool: an Anthropic tool's own fields, or for an OpenAI
    /// Responses tool (<c>web_search</c>, <c>web_search_2025_08_26</c>) its <c>filters.allowed_domains</c> and
    /// <c>user_location</c>.
    /// </summary>
    private static JsonObject AnthropicOptions(JsonObject? raw)
    {
        var options = new JsonObject();
        if (raw is null)
        {
            return options;
        }

        var type = raw["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var text) ? text : null;
        if (type is "web_search" or "web_search_2025_08_26")
        {
            if (raw["filters"] is JsonObject filters && filters["allowed_domains"] is { } allowed)
            {
                options["allowed_domains"] = allowed.DeepClone();
            }

            if (raw["user_location"] is { } location)
            {
                options["user_location"] = location.DeepClone();
            }

            return options;
        }

        foreach (var key in AnthropicOptionKeys)
        {
            if (raw[key] is { } value)
            {
                options[key] = value.DeepClone();
            }
        }

        return options;
    }
}

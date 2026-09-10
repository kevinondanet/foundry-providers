using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Swe.ClaudeCode;

/// <summary>
/// Port of inspect_swe <c>_claude_code/_events/toolview.py</c>: log-viewer renderings for Claude Code's built-in
/// <c>Write</c>, <c>ExitPlanMode</c>, <c>Task</c> and <c>Agent</c> tools. Those tools are not Inspect tool definitions,
/// so nothing else gives them a view. The <c>{{content}}</c>, <c>{{plan}}</c>, <c>{{description}}</c> and
/// <c>{{prompt}}</c> placeholders are written literally; the viewer fills them from the call's arguments.
/// </summary>
public static class ClaudeCodeToolView
{
    /// <summary>Port of <c>_CODE_FENCE_LANGUAGES</c>: file extension to code-fence language (looked up lower-cased, so <c>.R</c> is never hit).</summary>
    public static readonly IReadOnlyDictionary<string, string> CodeFenceLanguages = new OrderedDictionary<string, string>(StringComparer.Ordinal)
    {
        [".py"] = "python",
        [".ts"] = "typescript",
        [".tsx"] = "tsx",
        [".js"] = "javascript",
        [".jsx"] = "jsx",
        [".json"] = "json",
        [".yaml"] = "yaml",
        [".yml"] = "yaml",
        [".toml"] = "toml",
        [".sh"] = "bash",
        [".bash"] = "bash",
        [".zsh"] = "bash",
        [".css"] = "css",
        [".html"] = "html",
        [".sql"] = "sql",
        [".rs"] = "rust",
        [".go"] = "go",
        [".r"] = "r",
        [".R"] = "r",
        [".md"] = "markdown",
        [".qmd"] = "markdown",
    };

    /// <summary>Port of <c>tool_view(tool, arguments)</c>: the view for a built-in tool, or null for any other function.</summary>
    public static ToolCallContent? For(string function, JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(arguments);
        return function switch
        {
            "Write" => WriteView(arguments),
            "ExitPlanMode" => new ToolCallContent("markdown", "``````markdown\n{{plan}}\n``````") { Title = "ExitPlanMode" },
            "Task" or "Agent" => SubagentView(function, arguments),
            _ => null,
        };
    }

    /// <summary>Port of <c>write_tool_view</c>.</summary>
    private static ToolCallContent WriteView(JsonObject arguments)
    {
        // str(arguments.get("file_path", "") or "")
        var filePath = arguments.TryGetPropertyValue("file_path", out var pathNode) && Truthy(pathNode) ? Str(pathNode) : "";

        // str(arguments.get("content", "")): a missing key reads as "", a JSON null as "None"
        var content = arguments.TryGetPropertyValue("content", out var contentNode) ? Str(contentNode) : "";
        var endBody = content.EndsWith('\n') ? "" : "\n";
        var lang = CodeFenceLanguages.TryGetValue(SplitExtension(filePath).ToLowerInvariant(), out var language) ? language : "";
        var body = "``````" + lang + "\n{{content}}" + endBody + "``````";
        return new ToolCallContent("markdown", $"`file_path: {filePath}`\n\n{body}\n") { Title = "Write" };
    }

    /// <summary>
    /// Port of <c>subagent_tool_view</c>. Python's precedence makes the title <c>""</c> when there is no
    /// <c>subagent_type</c> (not the bare tool name), and <c>str(None)</c> makes a JSON null read as <c>"None"</c>.
    /// </summary>
    private static ToolCallContent SubagentView(string function, JsonObject arguments)
    {
        var subagentType = arguments.TryGetPropertyValue("subagent_type", out var typeNode) ? Str(typeNode) : "";
        return new ToolCallContent("markdown", "### {{description}}\n\n{{prompt}}")
        {
            Title = subagentType.Length > 0 ? $"{function}: {subagentType}" : "",
        };
    }

    /// <summary>
    /// Port of <c>posixpath.splitext(path)[1]</c>: the text from the last <c>.</c> after the last <c>/</c>, or empty
    /// when there is none or the basename's dots are all leading (<c>.bashrc</c>, <c>..</c>).
    /// </summary>
    internal static string SplitExtension(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var sepIndex = path.LastIndexOf('/');
        var dotIndex = path.LastIndexOf('.');
        if (dotIndex > sepIndex)
        {
            for (var i = sepIndex + 1; i < dotIndex; i++)
            {
                if (path[i] != '.')
                {
                    return path[dotIndex..];
                }
            }
        }

        return "";
    }

    /// <summary>Python truthiness of a JSON value: null, <c>false</c>, <c>0</c>, <c>""</c>, <c>[]</c> and <c>{}</c> are false.</summary>
    internal static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonObject obj => obj.Count > 0,
        JsonArray array => array.Count > 0,
        _ => node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>().Length > 0,
            JsonValueKind.True => true,
            JsonValueKind.Number => node.ToJsonString().TrimStart('-').Any(c => c is >= '1' and <= '9'),
            _ => false,
        },
    };

    /// <summary>
    /// Python <c>str()</c> of a JSON value: a string as-is, <c>None</c>, <c>True</c>/<c>False</c>, a number's JSON text.
    /// Arrays and objects are written as compact JSON, an approximation of Python's <c>repr</c>.
    /// </summary>
    internal static string Str(JsonNode? node) => node switch
    {
        null => "None",
        JsonObject or JsonArray => node.ToJsonString(),
        _ => node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            JsonValueKind.Null => "None",
            _ => node.ToJsonString(),
        },
    };
}

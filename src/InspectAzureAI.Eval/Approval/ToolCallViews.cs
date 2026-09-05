using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Approval;

/// <summary>Port of <c>tool/_tool_call.py</c> <c>ToolCallViewer</c>: a tool's custom rendering of a call for approval.</summary>
public delegate ToolCallView ToolCallViewer(ToolCall call);

/// <summary>The default view of a call and the placeholder substitution of <c>tool/_tool_call.py</c>.</summary>
public static partial class ToolCallViews
{
    /// <summary>Port of <c>default_tool_call_viewer</c>: the call as a Python-style function call in a markdown code block.</summary>
    public static ToolCallView Default(ToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return new ToolCallView
        {
            Call = new ToolCallContent("markdown", "```python\n" + ModelGraded.FormatFunctionCall(call.Function, call.Arguments) + "\n```\n"),
        };
    }

    /// <summary>
    /// Port of <c>substitute_tool_call_content</c>: replaces <c>{{param}}</c> placeholders in the title and content
    /// with the argument's Python <c>str()</c>; placeholders naming no argument are left as they are.
    /// </summary>
    public static ToolCallContent Substitute(ToolCallContent content, JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(arguments);
        return content with
        {
            Title = content.Title is null ? null : Replace(content.Title, arguments),
            Content = Replace(content.Content, arguments),
        };
    }

    /// <summary>Applies <see cref="Substitute(ToolCallContent, JsonObject)"/> to both parts of a view.</summary>
    public static ToolCallView Substitute(ToolCallView view, JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(arguments);
        return new ToolCallView
        {
            Context = view.Context is null ? null : Substitute(view.Context, arguments),
            Call = view.Call is null ? null : Substitute(view.Call, arguments),
        };
    }

    /// <summary>Python <c>str()</c> of a JSON argument: strings raw, <c>True</c>/<c>False</c>/<c>None</c>, other values as JSON text.</summary>
    internal static string PythonStr(JsonNode? value) => value switch
    {
        null => "None",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "True" : "False",
        _ => value.ToJsonString(),
    };

    private static string Replace(string text, JsonObject arguments) =>
        Placeholder().Replace(text, match => arguments.TryGetPropertyValue(match.Groups[1].Value, out var value) ? PythonStr(value) : match.Value);

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex Placeholder();
}

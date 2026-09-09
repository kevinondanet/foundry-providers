using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;

namespace InspectAzureAI.Eval.Approval;

public static partial class ToolCallViews
{
    /// <summary>
    /// Port of <c>code_viewer(language, code_param, title)</c> (<c>tool/_tools/_execute.py</c>): a viewer that
    /// renders the call's <paramref name="codeParam"/> argument as a fenced <paramref name="language"/> markdown
    /// block whose title is <paramref name="title"/> (the language when null or empty). As in Python
    /// (<c>str(code or tool_call.function).strip()</c>) a missing or falsy argument (null, "", 0, false, an empty
    /// array or object) shows the function name instead, and a non-string argument its Python <c>str()</c>.
    /// </summary>
    public static ToolCallViewer Code(string language, string codeParam, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(codeParam);
        var viewTitle = string.IsNullOrEmpty(title) ? language : title;
        return call =>
        {
            ArgumentNullException.ThrowIfNull(call);
            call.Arguments.TryGetPropertyValue(codeParam, out var value);
            var code = (IsFalsy(value) ? call.Function : PythonStr(value)).Trim();
            return new ToolCallView
            {
                Call = new ToolCallContent("markdown", "```" + language + "\n" + code + "\n```\n") { Title = viewTitle },
            };
        };
    }

    /// <summary>Python truthiness of a JSON argument: null, "", 0, false, [] and {} are falsy.</summary>
    private static bool IsFalsy(JsonNode? value) => value switch
    {
        null => true,
        JsonArray array => array.Count == 0,
        JsonObject obj => obj.Count == 0,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length == 0,
        JsonValue v when v.TryGetValue<bool>(out var b) => !b,
        JsonValue v when v.TryGetValue<double>(out var d) => d == 0,
        _ => false,
    };
}

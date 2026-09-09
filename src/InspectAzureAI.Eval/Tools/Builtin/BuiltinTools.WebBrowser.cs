namespace InspectAzureAI.Eval.Tools.Builtin;

public static partial class BuiltinTools
{
    /// <summary>
    /// Port of <c>web_browser(interactive=, instance=)</c> (<c>tool/_tools/_web_browser/_web_browser.py</c>):
    /// the web browser navigation tools, see <see cref="Tools.WebBrowser.Create"/>. The tool names are
    /// <see cref="Tools.WebBrowser.Names"/> (<c>web_browser_go</c>, <c>web_browser_click</c>,
    /// <c>web_browser_type_submit</c>, <c>web_browser_type</c>, <c>web_browser_scroll</c>,
    /// <c>web_browser_back</c>, <c>web_browser_forward</c>, <c>web_browser_refresh</c>).
    /// </summary>
    /// <param name="interactive">Provide interactive tools (enable clicking, typing, and submitting forms). Defaults to true.</param>
    /// <param name="instance">Instance id (each unique instance id has its own web browser process).</param>
    public static IReadOnlyList<ToolDef> WebBrowser(bool interactive = true, string? instance = null) =>
        global::InspectAzureAI.Eval.Tools.WebBrowser.Create(interactive, instance);
}

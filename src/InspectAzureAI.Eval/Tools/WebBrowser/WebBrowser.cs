using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Tools.Support;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tools;

/// <summary>
/// Port of <c>tool/_tools/_web_browser/_web_browser.py</c>: the <c>web_browser()</c> tool set (go, click,
/// type_submit, type, scroll, back, forward, refresh) driving a headless Chromium inside the sample
/// sandbox. The browser lives in the legacy <c>inspect-tool-support</c> service (the
/// <c>aisiuk/inspect-tool-support</c> image), found on the sandbox's <c>PATH</c> by
/// <see cref="LegacyToolSupport"/>; each call is one JSON-RPC exchange over
/// <see cref="SandboxJsonRpcTransport"/>, a session is created once per sample (per <c>instance</c>) and
/// remembered in <see cref="WebBrowserStore"/>, and every tool answers with the page's web accessibility
/// tree. Names, descriptions, parameters and the <c>parallel=False</c> flag match Python's <c>ToolInfo</c>.
/// </summary>
public static partial class WebBrowser
{
    public const string GoName = "web_browser_go";

    public const string ClickName = "web_browser_click";

    public const string TypeSubmitName = "web_browser_type_submit";

    public const string TypeName = "web_browser_type";

    public const string ScrollName = "web_browser_scroll";

    public const string BackName = "web_browser_back";

    public const string ForwardName = "web_browser_forward";

    public const string RefreshName = "web_browser_refresh";

    /// <summary>The names of the eight tools, in the order <see cref="Create"/> returns them when interactive.</summary>
    public static readonly IReadOnlyList<string> Names = [GoName, ClickName, TypeSubmitName, TypeName, ScrollName, BackName, ForwardName, RefreshName];

    /// <summary>Python: <c>timeout = 180</c> in <c>_web_browser_cmd</c>.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(180);

    /// <summary>The tool name passed to <c>legacy_tool_support_sandbox</c> (and so into its error message).</summary>
    public const string ServiceName = "web browser";

    /// <summary>The <c>web_browser_go</c> description (Python's <c>execute</c> docstring, verbatim).</summary>
    public const string GoDescription =
        "Navigate the web browser to a URL.\n\n"
        + "Once you have navigated to a page, you will be presented with a web accessibility tree of the elements on the page. Each element has an ID, which is displayed in brackets at the beginning of its line. For example:\n\n"
        + "```\n"
        + "[1] RootWebArea \"Google\" [focused: True, url: https://www.google.com/]\n"
        + "  [76] link \"About\" [url: https://about.google/]\n"
        + "  [85] link \"Gmail \" [url: https://mail.google.com/mail/&ogbl]\n"
        + "    [4] StaticText \"Gmail\"\n"
        + "  [91] button \"Google apps\" [expanded: False]\n"
        + "  [21] combobox \"Search\" [editable: plaintext, autocomplete: both, hasPopup: listbox, required: False, expanded: False, controls: Alh6id]\n"
        + "```\n\n"
        + "To execute a Google Search for 'dogs', you would type into the \"Search\" combobox with element ID 21 and then press ENTER using the web_browser_type_submit tool:\n\n"
        + "web_browser_type_submit(21, \"dogs\")\n\n"
        + "You should only attempt to navigate the web browser one page at a time (parallel calls to web browser tools are not permitted).";

    public const string ClickDescription =
        "Click an element on the page currently displayed by the web browser.\n\n"
        + "For example, with the following web accessibility tree:\n\n"
        + "```\n"
        + "[304] RootWebArea \"Poetry Foundation\" [focused: True, url: https://www.poetryfoundation.org/]\n"
        + "  [63] StaticText \"POETRY FOUNDATION\"\n"
        + "  [427] button \"POEMS & POETS\" [expanded: False]\n"
        + "  [434] button \"FEATURES\" [expanded: False]\n"
        + "```\n\n"
        + "You could click on the \"POEMS & POETS\" button with:\n\n"
        + "web_browser_click(427)";

    public const string TypeSubmitDescription =
        "Type text into a form input on a web browser page and press ENTER to submit the form.\n\n"
        + "For example, to execute a search for \"Yeats\" from this page:\n\n"
        + "```\n"
        + "[2] RootWebArea \"Moon - Wikipedia\" [focused: True, url: https://en.wikipedia.org/wiki/Moon]\n"
        + "  [91] StaticText \"Jump to content\"\n"
        + "  [682] button \"Main menu\" [hasPopup: menu]\n"
        + "  [751] searchbox \"Search Wikipedia\" [editable: plaintext, keyshortcuts: Alt+f]\n"
        + "  [759] button \"Search\"\n"
        + "  [796] button \"Personal tools\" [hasPopup: menu]\n"
        + "```\n\n"
        + "You would type into the searchbox and press ENTER using the following tool call:\n\n"
        + "web_browser_type_submit(751, \"Yeats\")";

    public const string TypeDescription =
        "Type text into an input on a web browser page.\n\n"
        + "For example, to type \"Norah\" into the \"First Name\" search box on this page:\n\n"
        + "```\n"
        + "[106] RootWebArea \"My Profile\" [focused: True, url: https://profile.example.com]\n"
        + "  [305] link \"My library\" [url: https://profile.example.com/library]\n"
        + "  [316] textbox \"First Name\" [focused: True, editable: plaintext, required: False]\n"
        + "  [316] textbox \"Last Name\" [focused: True, editable: plaintext, required: False]\n"
        + "```\n\n"
        + "You would use the following command:\n\n"
        + "web_browser_type(316, \"Norah\")\n\n"
        + "Note that the web_browser_type_submit tool is typically much more useful than the web_browser_type tool since it enters input and submits the form. You would typically only need to use the web_browser_type tool to fill out forms with multiple inputs.";

    public const string ScrollDescription =
        "Scroll the web browser up or down by one page.\n\n"
        + "Occasionally some very long pages don't display all of their content at once. To see additional content you can scroll the page down with:\n\n"
        + "web_browser_scroll(\"down\")\n\n"
        + "You can then return to the previous scroll position with:\n\n"
        + "web_browser_scroll(\"up\")";

    public const string BackDescription =
        "Navigate the web browser back in the browser history.\n\n"
        + "If you want to view a page that you have previously browsed (or perhaps just didn't find what you were looking for on a page and want to backtrack) use the web_browser_back tool.";

    public const string ForwardDescription =
        "Navigate the web browser forward in the browser history.\n\n"
        + "If you have navigated back in the browser history and then want to navigate forward use the web_browser_forward tool.";

    public const string RefreshDescription =
        "Refresh the current page of the web browser.\n\n"
        + "If you have interacted with a page by clicking buttons and want to reset it to its original state, use the web_browser_refresh tool.";

    /// <summary>Port of the container's <c>NewSessionResult</c> (the <c>web_new_session</c> reply).</summary>
    public sealed record NewSessionResult
    {
        [JsonPropertyName("session_name")]
        public required string SessionName { get; init; }
    }

    /// <summary>Port of the container's <c>CrawlerResult</c> (the reply of every navigation method).</summary>
    public sealed record CrawlerResult
    {
        [JsonPropertyName("web_url")]
        public required string WebUrl { get; init; }

        [JsonPropertyName("main_content")]
        public string? MainContent { get; init; }

        [JsonPropertyName("web_at")]
        public required string WebAt { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }
    }

    /// <summary>
    /// Port of <c>web_browser(interactive=, instance=)</c>: the tools used for web browser navigation. To
    /// create a separate web browser process for each call, pass a unique <paramref name="instance"/>.
    /// See https://inspect.aisi.org.uk/tools-standard.html#sec-web-browser.
    /// </summary>
    /// <param name="interactive">Provide interactive tools (enable clicking, typing, and submitting forms).</param>
    /// <param name="instance">Instance id (each unique instance id has its own web browser process).</param>
    public static IReadOnlyList<ToolDef> Create(bool interactive = true, string? instance = null)
    {
        // start with go tool (excluding interactive docs if necessary)
        var go = Go(instance);
        if (!interactive)
        {
            go = GoWithoutInteractiveDocs(go);
        }

        var tools = new List<ToolDef> { go };

        // add interactive tools if requested
        if (interactive)
        {
            tools.Add(ToolWithWebAtViewer(Click(instance), instance));
            tools.Add(ToolWithWebAtViewer(TypeSubmit(instance), instance));
            tools.Add(ToolWithWebAtViewer(Type(instance), instance));
        }

        // add navigational tools
        tools.Add(Scroll(instance));
        tools.Add(Back(instance));
        tools.Add(Forward(instance));
        tools.Add(Refresh(instance));
        return tools;
    }

    /// <summary>Port of <c>web_browser_go(instance)</c>: navigation to a URL.</summary>
    public static ToolDef Go(string? instance = null) =>
        Tool(GoName, GoDescription, Params(("url", "string", "URL to navigate to.")), "web_go", instance,
            arguments => new JsonObject { ["url"] = ToolArguments.RequiredString(arguments, "url") });

    /// <summary>Port of <c>web_browser_click(instance)</c>: clicking an element on a web page.</summary>
    public static ToolDef Click(string? instance = null) =>
        Tool(ClickName, ClickDescription, Params(("element_id", "integer", "ID of the element to click.")), "web_click", instance,
            arguments => new JsonObject { ["element_id"] = RequiredInteger(arguments, "element_id") });

    /// <summary>Port of <c>web_browser_type_submit(instance)</c>: typing and submitting input.</summary>
    public static ToolDef TypeSubmit(string? instance = null) =>
        Tool(TypeSubmitName, TypeSubmitDescription, Params(("element_id", "integer", "ID of the element to type text into."), ("text", "string", "Text to type.")), "web_type_submit", instance,
            arguments => new JsonObject { ["element_id"] = RequiredInteger(arguments, "element_id"), ["text"] = ToolArguments.RequiredString(arguments, "text") });

    /// <summary>Port of <c>web_browser_type(instance)</c>: typing into inputs.</summary>
    public static ToolDef Type(string? instance = null) =>
        Tool(TypeName, TypeDescription, Params(("element_id", "integer", "ID of the element to type text into."), ("text", "string", "Text to type.")), "web_type", instance,
            arguments => new JsonObject { ["element_id"] = RequiredInteger(arguments, "element_id"), ["text"] = ToolArguments.RequiredString(arguments, "text") });

    /// <summary>Port of <c>web_browser_scroll(instance)</c>: scrolling up or down one page.</summary>
    public static ToolDef Scroll(string? instance = null) =>
        Tool(ScrollName, ScrollDescription, Params(("direction", "string", "\"up\" or \"down\"")), "web_scroll", instance,
            arguments => new JsonObject { ["direction"] = ToolArguments.RequiredString(arguments, "direction") });

    /// <summary>Port of <c>web_browser_back(instance)</c>: navigating back in the browser history.</summary>
    public static ToolDef Back(string? instance = null) =>
        Tool(BackName, BackDescription, Params(), "web_back", instance, _ => new JsonObject());

    /// <summary>Port of <c>web_browser_forward(instance)</c>: navigating forward in the browser history.</summary>
    public static ToolDef Forward(string? instance = null) =>
        Tool(ForwardName, ForwardDescription, Params(), "web_forward", instance, _ => new JsonObject());

    /// <summary>Port of <c>web_browser_refresh(instance)</c>: refreshing the current page.</summary>
    public static ToolDef Refresh(string? instance = null) =>
        Tool(RefreshName, RefreshDescription, Params(), "web_refresh", instance, _ => new JsonObject());

    /// <summary>Port of <c>go_without_interactive_docs</c>: the go tool with every description line mentioning <c>web_browser_type_submit</c> dropped.</summary>
    public static ToolDef GoWithoutInteractiveDocs(ToolDef tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var lines = tool.Description.Split('\n').Where(line => !line.Contains(TypeSubmitName, StringComparison.Ordinal));
        return ToolDef.ToolWith(tool, description: string.Join("\n", lines));
    }

    /// <summary>Port of <c>tool_with_web_at_viewer</c>: the tool with <see cref="WebAtViewer"/> as its approval viewer.</summary>
    public static ToolDef ToolWithWebAtViewer(ToolDef tool, string? instance = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return tool with { Viewer = WebAtViewer(instance) };
    }

    /// <summary>
    /// Port of the <c>web_at_viewer</c> closure: a viewer for interactive calls that shows the current web
    /// accessibility tree (from the store) truncated around the call's <c>element_id</c>, the element's
    /// line marked with a leading <c>*</c>; an empty view when there is no tree or the element is not in it.
    /// Deviation: outside a sample context (no store) the view is empty rather than an error.
    /// </summary>
    public static ToolCallViewer WebAtViewer(string? instance = null) => call =>
    {
        ArgumentNullException.ThrowIfNull(call);
        if (SampleContext.Current is null)
        {
            return new ToolCallView();
        }

        // get the web accessibility tree, if we have it create a view from it
        var webAt = Store.StoreAs<WebBrowserStore>(instance).WebAt;
        var elementId = call.Arguments.TryGetPropertyValue("element_id", out var node) && node is not null ? ToolCallViews.PythonStr(node) : "0";
        if (webAt.Length > 0 && elementId is not ("0" or ""))
        {
            var lines = webAt.Split('\n');
            var pattern = new Regex(@"^\s+\[" + Regex.Escape(elementId) + @"\] .*$");
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (pattern.IsMatch(line))
                {
                    var snippet = new List<string>();
                    snippet.AddRange(lines.Take(1));
                    snippet.Add("  ...");
                    snippet.AddRange(lines.Skip(Math.Max(i - 2, 1)).Take(Math.Max(i - Math.Max(i - 2, 1), 0)));
                    snippet.Add(ReplaceFirstSpace(line));
                    snippet.AddRange(lines.Skip(i + 1).Take(Math.Min(i + 3, lines.Length) - (i + 1)));
                    snippet.Add("  ...");
                    return new ToolCallView { Context = new ToolCallContent("text", string.Join("\n", snippet)) };
                }
            }
        }

        // no view found
        return new ToolCallView();
    };

    /// <summary>
    /// Port of <c>_web_browser_cmd(tool_name, instance, params)</c>: finds the legacy tool-support sandbox
    /// (falling back to the deprecated <c>aisiuk/inspect-web-browser-tool</c> client when only that image is
    /// present), creates the session on first use, sends the request and returns the main content and
    /// accessibility tree (base64 image data stripped) as content items, or just the tree.
    /// </summary>
    public static async Task<ToolResult> WebBrowserCmdAsync(string toolName, string? instance, JsonObject parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(parameters);
        LegacyToolSupportSandbox legacy;
        try
        {
            legacy = await LegacyToolSupport.LegacyToolSupportSandboxAsync(ServiceName, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (PrerequisiteError e)
        {
            // The user may have the old, incompatible, sandbox. If so, use that and
            // execute the old compatible code.
            try
            {
                var args = parameters.Select(pair => ToolCallViews.PythonStr(pair.Value)).ToArray();
                return await WebBrowserBackCompat.OldWebBrowserCmdAsync(toolName, args, cancellationToken).ConfigureAwait(false);
            }
            catch (PrerequisiteError)
            {
                throw e;
            }
        }

        // bind to store (use instance id if provided)
        var store = Store.StoreAs<WebBrowserStore>(instance);

        // Create transport for all RPC calls
        var transport = legacy.Transport;
        var options = new JsonRpcCallOptions(Timeout);

        if (store.SessionId.Length == 0)
        {
            var session = await JsonRpc.ExecModelRequestAsync<NewSessionResult>(
                "web_new_session",
                new JsonObject { ["headful"] = false },
                transport,
                SandboxToolsErrorMapper.Instance,
                options,
                cancellationToken).ConfigureAwait(false);
            store.SessionId = session.SessionName;
        }

        parameters["session_name"] = store.SessionId;

        var crawlerResult = await JsonRpc.ExecModelRequestAsync<CrawlerResult>(
            toolName,
            parameters,
            transport,
            SandboxToolsErrorMapper.Instance,
            options,
            cancellationToken).ConfigureAwait(false);
        if (crawlerResult.Error is { } error && error.Trim().Length > 0)
        {
            throw new ToolError(error);
        }

        return CrawlerResponse(store, crawlerResult.MainContent, crawlerResult.WebAt);
    }

    /// <summary>The shared tail of both command paths: record the page in the store and shape the tool result (base64 image data removed from the tree).</summary>
    internal static ToolResult CrawlerResponse(WebBrowserStore store, string? mainContent, string? webAt)
    {
        var tree = string.IsNullOrEmpty(webAt) ? "(no web accessibility tree available)" : webAt;

        // Remove base64 data from images.
        var webAtLines = tree.Split('\n').Select(StripImageData);

        store.MainContent = string.IsNullOrEmpty(mainContent) ? "(no main text summary)" : mainContent;
        store.WebAt = tree;

        var stripped = string.Join("\n", webAtLines);
        return string.IsNullOrEmpty(mainContent)
            ? stripped
            : ToolResult.FromContents([new ContentText($"main content:\n{mainContent}\n\n"), new ContentText($"accessibility tree:\n{stripped}")]);
    }

    /// <summary>Python: <c>line.partition("data:image/png;base64")[0]</c>.</summary>
    private static string StripImageData(string line)
    {
        var index = line.IndexOf("data:image/png;base64", StringComparison.Ordinal);
        return index < 0 ? line : line[..index];
    }

    /// <summary>Python: <c>line.replace(" ", "*", 1)</c>.</summary>
    private static string ReplaceFirstSpace(string line)
    {
        var index = line.IndexOf(' ');
        return index < 0 ? line : line[..index] + "*" + line[(index + 1)..];
    }

    private static ToolDef Tool(string name, string description, ToolParams parameters, string method, string? instance, Func<JsonObject, JsonObject> readParameters) =>
        new(name, description, parameters, (arguments, cancellationToken) =>
        {
            Builtin.ToolInputValidator.Validate(arguments, parameters);
            return WebBrowserCmdAsync(method, instance, readParameters(arguments), cancellationToken);
        })
        {
            Parallel = false,
        };

    private static ToolParams Params(params (string Name, string Type, string Description)[] properties) => new()
    {
        Properties = properties.ToDictionary(p => p.Name, p => ToolParam.Of(p.Type, p.Description), StringComparer.Ordinal),
        Required = properties.Select(p => p.Name).ToArray(),
    };

    private static long RequiredInteger(JsonObject arguments, string name) =>
        ToolArguments.OptionalInteger(arguments, name) ?? throw new ToolParsingError($"Required parameter '{name}' not provided.");
}

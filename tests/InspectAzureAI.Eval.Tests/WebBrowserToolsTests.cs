using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Eval.Tools.Builtin;
using InspectAzureAI.Eval.Tools.Support;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// A <see cref="FakeSandboxEnvironment"/> scripted as the <c>aisiuk/inspect-tool-support</c> image (the
/// <c>inspect-tool-support</c> CLI on PATH answering JSON-RPC) and/or the deprecated
/// <c>aisiuk/inspect-web-browser-tool</c> image (the two python client scripts).
/// </summary>
internal sealed class ScriptedBrowserSandbox
{
    public ScriptedBrowserSandbox(bool hasLegacyCli = true, bool hasOldClient = false)
    {
        HasLegacyCli = hasLegacyCli;
        HasOldClient = hasOldClient;
        Sandbox = new FakeSandboxEnvironment { OnExecCall = Handle };
        Handlers["version"] = _ => FakeSandboxEnvironment.Ok(SandboxToolsFixtures.Success("1.2.3"));
        Handlers["web_new_session"] = _ => FakeSandboxEnvironment.Ok(SandboxToolsFixtures.Success(new { session_name = "session-1" }));
    }

    public FakeSandboxEnvironment Sandbox { get; }

    public bool HasLegacyCli { get; set; }

    public bool HasOldClient { get; set; }

    public bool WhichThrowsUnavailable { get; set; }

    /// <summary>What the old <c>web_client.py</c> prints.</summary>
    public string OldClientOutput { get; set; } = "web_url: https://old.example\nweb_at: [1] RootWebArea \"Old\"\n";

    public Dictionary<string, Func<JsonObject, ExecResult>> Handlers { get; } = new(StringComparer.Ordinal);

    /// <summary>Every JSON-RPC request the legacy CLI received, parsed.</summary>
    public List<JsonObject> Requests { get; } = [];

    public List<FakeExecCall> RpcCalls { get; } = [];

    /// <summary>The <c>python3 ...</c> invocations of the old client.</summary>
    public List<FakeExecCall> OldClientCalls { get; } = [];

    public IEnumerable<string> Methods => Requests.Select(r => r["method"]!.GetValue<string>());

    public JsonObject? Params(string method) => Requests.First(r => r["method"]!.GetValue<string>() == method)["params"]?.AsObject();

    public void AnswerCrawler(string method, string webAt, string? mainContent = null, string? error = null, string webUrl = "https://example.com") =>
        Handlers[method] = _ => FakeSandboxEnvironment.Ok(SandboxToolsFixtures.Success(new { web_url = webUrl, main_content = mainContent, web_at = webAt, error }));

    private ExecResult? Handle(FakeExecCall call)
    {
        var cmd = call.Cmd;
        switch (cmd[0])
        {
            case "which":
                if (WhichThrowsUnavailable)
                {
                    throw new SandboxUnavailableException("container gone");
                }

                return cmd[1] == LegacyToolSupport.LegacySandboxCli && HasLegacyCli ? FakeSandboxEnvironment.Ok("/usr/local/bin/inspect-tool-support\n") : FakeSandboxEnvironment.Fail(1);
            case "test":
                return cmd[2] == WebBrowserBackCompat.WebClientRequest && HasOldClient ? FakeSandboxEnvironment.Ok() : FakeSandboxEnvironment.Fail(1);
            case LegacyToolSupport.LegacySandboxCli when cmd[1] == "exec":
                var request = SandboxToolsFixtures.Request(call.Input ?? throw new InvalidOperationException("exec without a request on stdin"));
                Requests.Add(request);
                RpcCalls.Add(call);
                var method = request["method"]!.GetValue<string>();
                return Handlers.TryGetValue(method, out var handler)
                    ? handler(request)
                    : FakeSandboxEnvironment.Ok(SandboxToolsFixtures.Error(-32601, "Method not found"));
            case "python3":
                OldClientCalls.Add(call);
                return cmd[1] == WebBrowserBackCompat.WebClientNewSession
                    ? FakeSandboxEnvironment.Ok("old-session\n")
                    : FakeSandboxEnvironment.Ok(OldClientOutput);
            default:
                return FakeSandboxEnvironment.Ok();
        }
    }
}

/// <summary>Port-level behaviour of <c>tool/_tools/_web_browser/_web_browser.py</c>, <c>_back_compat.py</c> and <c>_sandbox_tools_utils/_legacy_helpers.py</c>.</summary>
public class WebBrowserToolsTests
{
    private const string Tree =
        "[1] RootWebArea \"Google\" [focused: True, url: https://www.google.com/]\n"
        + "  [76] link \"About\" [url: https://about.google/]\n"
        + "  [85] link \"Gmail \" [url: https://mail.google.com/mail/&ogbl]\n"
        + "    [4] StaticText \"Gmail\"\n"
        + "  [91] button \"Google apps\" [expanded: False]\n"
        + "  [21] combobox \"Search\" [editable: plaintext]\n"
        + "  [30] img \"logo\" [url: data:image/png;base64,AAAA]\n"
        + "  [40] button \"Next\"";

    private static ToolDef Find(IReadOnlyList<ToolDef> tools, string name) => tools.Single(t => t.Name == name);

    private static JsonObject Args(object arguments) => SandboxToolsFixtures.Args(arguments);

    private static IDisposable MultiSandbox(params (string Name, ISandboxEnvironment Sandbox)[] sandboxes)
    {
        var context = new SampleContext
        {
            ActiveModel = new Model(new ScriptedModelApi()),
            Sandboxes = SandboxEnvironments.Create(sandboxes.Select(s => new KeyValuePair<string, ISandboxEnvironment>(s.Name, s.Sandbox))),
        };
        return SampleContext.Begin(context);
    }

    // ---- tool set shape ----

    [Fact]
    public void interactive_tool_set_has_pythons_eight_tools_in_order_and_none_run_in_parallel()
    {
        var tools = WebBrowser.Create();

        Assert.Equal(WebBrowser.Names, tools.Select(t => t.Name).ToArray());
        Assert.All(tools, t => Assert.False(t.Parallel));
        Assert.Equal(tools.Select(t => t.Name), BuiltinTools.WebBrowser().Select(t => t.Name));

        // only the interactive tools carry the web_at viewer
        Assert.Null(Find(tools, WebBrowser.GoName).Viewer);
        Assert.NotNull(Find(tools, WebBrowser.ClickName).Viewer);
        Assert.NotNull(Find(tools, WebBrowser.TypeSubmitName).Viewer);
        Assert.NotNull(Find(tools, WebBrowser.TypeName).Viewer);
        Assert.Null(Find(tools, WebBrowser.ScrollName).Viewer);
    }

    [Fact]
    public void non_interactive_tool_set_drops_the_interactive_tools_and_the_type_submit_lines_of_go()
    {
        var tools = WebBrowser.Create(interactive: false);

        Assert.Equal(["web_browser_go", "web_browser_scroll", "web_browser_back", "web_browser_forward", "web_browser_refresh"], tools.Select(t => t.Name).ToArray());
        var go = tools[0];
        Assert.DoesNotContain("web_browser_type_submit", go.Description);
        // Python's line filter leaves the blank lines around the removed lines in place
        Assert.EndsWith("```\n\n\n\nYou should only attempt to navigate the web browser one page at a time (parallel calls to web browser tools are not permitted).", go.Description);
        Assert.StartsWith("Navigate the web browser to a URL.\n\nOnce you have navigated", go.Description);
        Assert.Equal(["url"], go.Parameters.Properties.Keys);
    }

    [Theory]
    [InlineData("web_browser_go", "url:string:URL to navigate to.")]
    [InlineData("web_browser_click", "element_id:integer:ID of the element to click.")]
    [InlineData("web_browser_type_submit", "element_id:integer:ID of the element to type text into.", "text:string:Text to type.")]
    [InlineData("web_browser_type", "element_id:integer:ID of the element to type text into.", "text:string:Text to type.")]
    [InlineData("web_browser_scroll", "direction:string:\"up\" or \"down\"")]
    [InlineData("web_browser_back")]
    [InlineData("web_browser_forward")]
    [InlineData("web_browser_refresh")]
    public void parameters_match_pythons_tool_info(string name, params string[] expected)
    {
        var tool = Find(WebBrowser.Create(), name);

        var actual = tool.Parameters.Properties.Select(p => $"{p.Key}:{p.Value.Type!.Single()}:{p.Value.Description}").ToArray();
        Assert.Equal(expected, actual);
        Assert.Equal(expected.Select(e => e.Split(':')[0]), tool.Parameters.Required);
        Assert.False(Assert.IsType<bool>(tool.Parameters.AdditionalProperties));
        Assert.StartsWith(name switch
        {
            "web_browser_go" => "Navigate the web browser to a URL.\n\n",
            "web_browser_click" => "Click an element on the page currently displayed by the web browser.\n\n",
            "web_browser_type_submit" => "Type text into a form input on a web browser page and press ENTER to submit the form.\n\n",
            "web_browser_type" => "Type text into an input on a web browser page.\n\n",
            "web_browser_scroll" => "Scroll the web browser up or down by one page.\n\n",
            "web_browser_back" => "Navigate the web browser back in the browser history.\n\n",
            "web_browser_forward" => "Navigate the web browser forward in the browser history.\n\n",
            _ => "Refresh the current page of the web browser.\n\n",
        }, tool.Description);
    }

    // ---- session and request/response shape ----

    [Fact]
    public async Task session_is_created_once_per_sample_and_its_name_travels_with_every_request()
    {
        var scripted = new ScriptedBrowserSandbox();
        scripted.AnswerCrawler("web_go", Tree);
        scripted.AnswerCrawler("web_back", Tree);
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);
        var tools = WebBrowser.Create();

        await Find(tools, WebBrowser.GoName).Execute(Args(new { url = "https://www.google.com" }), CancellationToken.None);
        await Find(tools, WebBrowser.BackName).Execute(new JsonObject(), CancellationToken.None);

        // the sandbox discovery is cached per sample, but Python re-reads the service version on every call
        Assert.Equal(["version", "web_new_session", "web_go", "version", "web_back"], scripted.Methods.ToArray());
        Assert.Single(scripted.Sandbox.Calls, call => call.Cmd[0] == "which");
        Assert.Equal("""{"headful":false}""", scripted.Params("web_new_session")!.ToJsonString());
        Assert.Equal("session-1", scripted.Params("web_go")!["session_name"]!.GetValue<string>());
        Assert.Equal("session-1", scripted.Params("web_back")!["session_name"]!.GetValue<string>());
        Assert.Equal("session-1", scope.Context.Store.Get("WebBrowserStore:session_id"));
        Assert.Equal(Tree, scope.Context.Store.Get("WebBrowserStore:web_at"));
        Assert.Equal("(no main text summary)", scope.Context.Store.Get("WebBrowserStore:main_content"));

        // the version probe is bounded by 5s, the browser calls by 180s, and the CLI is invoked as `inspect-tool-support exec`
        var timeouts = scripted.RpcCalls.Select(call => call.Timeout).ToArray();
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(180)], timeouts);
        Assert.All(scripted.RpcCalls, call => Assert.Equal(["inspect-tool-support", "exec"], call.Cmd));
        Assert.All(scripted.RpcCalls, call => Assert.Null(call.User));
    }

    [Fact]
    public async Task each_instance_has_its_own_session_under_its_own_store_keys()
    {
        var scripted = new ScriptedBrowserSandbox();
        var sessions = 0;
        scripted.Handlers["web_new_session"] = _ => FakeSandboxEnvironment.Ok(SandboxToolsFixtures.Success(new { session_name = $"session-{++sessions}" }));
        scripted.AnswerCrawler("web_go", Tree);
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);

        await WebBrowser.Go("a").Execute(Args(new { url = "https://a" }), CancellationToken.None);
        await WebBrowser.Go("b").Execute(Args(new { url = "https://b" }), CancellationToken.None);
        await WebBrowser.Go("a").Execute(Args(new { url = "https://a/2" }), CancellationToken.None);

        Assert.Equal(2, sessions);
        Assert.Equal("session-1", scope.Context.Store.Get("WebBrowserStore:a:session_id"));
        Assert.Equal("session-2", scope.Context.Store.Get("WebBrowserStore:b:session_id"));
        var goParams = scripted.Requests.Where(r => r["method"]!.GetValue<string>() == "web_go").Select(r => r["params"]!["session_name"]!.GetValue<string>()).ToArray();
        Assert.Equal(["session-1", "session-2", "session-1"], goParams);
    }

    [Theory]
    [InlineData("web_browser_go", "web_go", """{"url":"https://x"}""", """{"url":"https://x","session_name":"session-1"}""")]
    [InlineData("web_browser_click", "web_click", """{"element_id":427}""", """{"element_id":427,"session_name":"session-1"}""")]
    [InlineData("web_browser_type_submit", "web_type_submit", """{"element_id":751,"text":"Yeats"}""", """{"element_id":751,"text":"Yeats","session_name":"session-1"}""")]
    [InlineData("web_browser_type", "web_type", """{"element_id":316,"text":"Norah"}""", """{"element_id":316,"text":"Norah","session_name":"session-1"}""")]
    [InlineData("web_browser_scroll", "web_scroll", """{"direction":"down"}""", """{"direction":"down","session_name":"session-1"}""")]
    [InlineData("web_browser_back", "web_back", "{}", """{"session_name":"session-1"}""")]
    [InlineData("web_browser_forward", "web_forward", "{}", """{"session_name":"session-1"}""")]
    [InlineData("web_browser_refresh", "web_refresh", "{}", """{"session_name":"session-1"}""")]
    public async Task every_tool_sends_its_arguments_plus_the_session_name_to_its_rpc_method(string toolName, string method, string arguments, string expectedParams)
    {
        var scripted = new ScriptedBrowserSandbox();
        scripted.AnswerCrawler(method, Tree);
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);

        var result = await Find(WebBrowser.Create(), toolName).Execute(SandboxToolsFixtures.Request(arguments), CancellationToken.None);

        Assert.Equal(expectedParams, scripted.Params(method)!.ToJsonString());
        Assert.Equal(method, scripted.Methods.Last());
        Assert.Contains("[21] combobox \"Search\"", result.AsText());
    }

    [Fact]
    public async Task tree_only_result_is_the_tree_with_image_data_stripped_while_the_store_keeps_it()
    {
        var scripted = new ScriptedBrowserSandbox();
        scripted.AnswerCrawler("web_go", Tree);
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);

        var result = await WebBrowser.Go().Execute(Args(new { url = "https://www.google.com" }), CancellationToken.None);

        Assert.Null(result.Contents);
        Assert.Equal(Tree.Replace("data:image/png;base64,AAAA]", "", StringComparison.Ordinal), result.Text);
        Assert.Equal(Tree, Store.StoreAs<WebBrowserStore>().WebAt);
    }

    [Fact]
    public async Task main_content_result_is_two_text_contents()
    {
        var scripted = new ScriptedBrowserSandbox();
        scripted.AnswerCrawler("web_go", Tree, mainContent: "Google search page");
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);

        var result = await WebBrowser.Go().Execute(Args(new { url = "https://www.google.com" }), CancellationToken.None);

        var contents = Assert.IsAssignableFrom<IReadOnlyList<Content>>(result.Contents);
        Assert.Equal(2, contents.Count);
        Assert.Equal("main content:\nGoogle search page\n\n", Assert.IsType<ContentText>(contents[0]).Text);
        Assert.Equal("accessibility tree:\n" + Tree.Replace("data:image/png;base64,AAAA]", "", StringComparison.Ordinal), Assert.IsType<ContentText>(contents[1]).Text);
        Assert.Equal("Google search page", Store.StoreAs<WebBrowserStore>().MainContent);
    }

    [Fact]
    public async Task empty_tree_is_reported_as_unavailable()
    {
        var scripted = new ScriptedBrowserSandbox();
        scripted.AnswerCrawler("web_refresh", "");
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);

        var result = await WebBrowser.Refresh().Execute(new JsonObject(), CancellationToken.None);

        Assert.Equal("(no web accessibility tree available)", result.Text);
        Assert.Equal("(no web accessibility tree available)", Store.StoreAs<WebBrowserStore>().WebAt);
    }

    // ---- error mapping ----

    [Fact]
    public async Task crawler_error_field_is_a_tool_error()
    {
        var scripted = new ScriptedBrowserSandbox();
        scripted.AnswerCrawler("web_click", "", error: "Element 999 not found");
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);

        var ex = await Assert.ThrowsAsync<ToolError>(() => WebBrowser.Click().Execute(Args(new { element_id = 999 }), CancellationToken.None));

        Assert.Equal("Element 999 not found", ex.Message);
    }

    [Fact]
    public async Task blank_crawler_error_is_ignored()
    {
        var scripted = new ScriptedBrowserSandbox();
        scripted.AnswerCrawler("web_click", Tree, error: "   ");
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);

        var result = await WebBrowser.Click().Execute(Args(new { element_id = 76 }), CancellationToken.None);

        Assert.StartsWith("[1] RootWebArea", result.Text);
    }

    [Theory]
    [InlineData(-32099, typeof(ToolError))]
    [InlineData(-32602, typeof(ToolParsingError))]
    [InlineData(-32603, typeof(ToolError))]
    [InlineData(-32098, typeof(InvalidOperationException))]
    public async Task json_rpc_errors_map_through_the_sandbox_tools_error_mapper(int code, Type expected)
    {
        var scripted = new ScriptedBrowserSandbox();
        scripted.Handlers["web_go"] = _ => FakeSandboxEnvironment.Ok(SandboxToolsFixtures.Error(code, "browser said no"));
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);

        var ex = await Assert.ThrowsAsync(expected, () => WebBrowser.Go().Execute(Args(new { url = "https://x" }), CancellationToken.None));

        Assert.Equal("browser said no", ex.Message);
    }

    [Fact]
    public async Task a_failed_cli_exec_fails_the_sample()
    {
        var scripted = new ScriptedBrowserSandbox();
        scripted.Handlers["web_go"] = _ => FakeSandboxEnvironment.Fail(1, "Traceback: playwright crashed");
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => WebBrowser.Go().Execute(Args(new { url = "https://x" }), CancellationToken.None));

        Assert.Contains("Sandbox.exec failure executing web_go", ex.Message);
        Assert.Contains("playwright crashed", ex.Message);
    }

    [Fact]
    public async Task invalid_arguments_are_parsing_errors_before_any_sandbox_call()
    {
        var scripted = new ScriptedBrowserSandbox();
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);

        await Assert.ThrowsAsync<ToolParsingError>(() => WebBrowser.Click().Execute(Args(new { element_id = "seven" }), CancellationToken.None));
        await Assert.ThrowsAsync<ToolParsingError>(() => WebBrowser.Go().Execute(new JsonObject(), CancellationToken.None));

        Assert.Empty(scripted.Sandbox.Calls);
    }

    [Fact]
    public async Task missing_service_is_a_prerequisite_error_with_pythons_compose_guidance()
    {
        var scripted = new ScriptedBrowserSandbox(hasLegacyCli: false);
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);

        var ex = await Assert.ThrowsAsync<PrerequisiteError>(() => WebBrowser.Go().Execute(Args(new { url = "https://x" }), CancellationToken.None));

        Assert.StartsWith("The web browser service was not found in any of the sandboxes for this sample. Please add the web browser to your configuration.\n\nFor example, the following Docker compose file uses the aisiuk/inspect-tool-support reference image as its default sandbox:\n\nservices:\n  default:\n    image: \"aisiuk/inspect-tool-support\"\n    init: true\n\nAlternatively, you can include the service into your own Dockerfile:\n\nENV PATH=\"$PATH:/opt/inspect_tool_support/bin\"\nRUN python -m venv /opt/inspect_tool_support && \\\n    /opt/inspect_tool_support/bin/pip install inspect-tool-support && \\\n    /opt/inspect_tool_support/bin/inspect-tool-support post-install", ex.Message);
        Assert.Equal(ex.Message, LegacyToolSupport.NotFoundMessage("web browser"));
        Assert.Empty(scripted.Requests);
    }

    [Fact]
    public async Task no_sandbox_at_all_is_the_no_sandbox_error()
    {
        using var scope = new SampleContextScope();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => WebBrowser.Go().Execute(Args(new { url = "https://x" }), CancellationToken.None));

        Assert.StartsWith("No sandbox environment has been provided for the current sample or task.", ex.Message);
    }

    // ---- legacy tool support helper ----

    [Fact]
    public async Task legacy_helper_finds_the_sandbox_with_the_cli_on_path_and_reads_its_version()
    {
        var plain = new ScriptedBrowserSandbox(hasLegacyCli: false);
        var browser = new ScriptedBrowserSandbox();
        browser.Handlers["version"] = _ => FakeSandboxEnvironment.Ok(SandboxToolsFixtures.Success("1.4.0-rc.1+build.7"));
        using var scope = MultiSandbox(("default", plain.Sandbox), ("web_browser", browser.Sandbox));

        var legacy = await LegacyToolSupport.LegacyToolSupportSandboxAsync("web browser");

        Assert.Same(browser.Sandbox, legacy.Sandbox);
        Assert.Equal(new Version(1, 4, 0), legacy.Version);
        Assert.Equal(["which", "inspect-tool-support"], plain.Sandbox.Calls.Single().Cmd);
        Assert.Equal(["version"], browser.Methods.ToArray());
        Assert.Equal("inspect-tool-support", legacy.Transport.Cli);
    }

    [Fact]
    public async Task legacy_helper_assumes_the_first_published_version_when_the_cli_has_no_version_method()
    {
        var scripted = new ScriptedBrowserSandbox();
        scripted.Handlers.Remove("version");
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);

        var legacy = await LegacyToolSupport.LegacyToolSupportSandboxAsync("web browser");

        Assert.Equal(new Version(0, 1, 6), legacy.Version);
        Assert.Equal(LegacyToolSupport.FirstPublishedVersion, legacy.Version);
    }

    [Fact]
    public async Task legacy_helper_restricts_the_lookup_to_a_named_sandbox()
    {
        var plain = new ScriptedBrowserSandbox(hasLegacyCli: false);
        var browser = new ScriptedBrowserSandbox();
        using var scope = MultiSandbox(("default", plain.Sandbox), ("web_browser", browser.Sandbox));

        var found = await LegacyToolSupport.LegacyToolSupportSandboxAsync("web browser", sandboxName: "web_browser");
        Assert.Same(browser.Sandbox, found.Sandbox);

        var ex = await Assert.ThrowsAsync<PrerequisiteError>(() => LegacyToolSupport.LegacyToolSupportSandboxAsync("web browser", sandboxName: "default"));
        Assert.StartsWith("The web browser service was not found in the sandbox 'default' for this sample.", ex.Message);

        // an unknown name is simply not found
        await Assert.ThrowsAsync<PrerequisiteError>(() => LegacyToolSupport.LegacyToolSupportSandboxAsync("web browser", sandboxName: "nope"));
    }

    [Fact]
    public async Task sandbox_with_caches_the_discovered_sandbox_per_sample_and_skips_unavailable_sandboxes()
    {
        var gone = new ScriptedBrowserSandbox { WhichThrowsUnavailable = true };
        var browser = new ScriptedBrowserSandbox();
        using var scope = MultiSandbox(("default", gone.Sandbox), ("web_browser", browser.Sandbox));

        var first = await SandboxWith.FindAsync("inspect-tool-support", onPath: true);
        var second = await SandboxWith.FindAsync("inspect-tool-support", onPath: true);

        Assert.Same(browser.Sandbox, first);
        Assert.Same(first, second);
        Assert.Single(gone.Sandbox.Calls);
        Assert.Single(browser.Sandbox.Calls);
        Assert.Null(await SandboxWith.FindAsync("/app/web_browser/web_client.py"));
    }

    [Theory]
    [InlineData("0.1.6", 0, 1, 6)]
    [InlineData("2.10.3-beta+exp", 2, 10, 3)]
    public void semver_parse_keeps_the_numeric_core(string text, int major, int minor, int patch) =>
        Assert.Equal(new Version(major, minor, patch), LegacyToolSupport.ParseSemver(text));

    [Fact]
    public void invalid_semver_is_an_error() =>
        Assert.Throws<InvalidOperationException>(() => LegacyToolSupport.ParseSemver("1.2"));

    // ---- viewer ----

    [Fact]
    public async Task web_at_viewer_snips_the_tree_around_the_element_and_marks_its_line()
    {
        var scripted = new ScriptedBrowserSandbox();
        scripted.AnswerCrawler("web_go", Tree);
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);
        var tools = WebBrowser.Create();
        await Find(tools, WebBrowser.GoName).Execute(Args(new { url = "https://www.google.com" }), CancellationToken.None);

        var view = Find(tools, WebBrowser.ClickName).Viewer!(new ToolCall("1", "web_browser_click", Args(new { element_id = 91 })));

        Assert.Null(view.Call);
        Assert.Equal("text", view.Context!.Format);
        Assert.Equal(
            "[1] RootWebArea \"Google\" [focused: True, url: https://www.google.com/]\n"
            + "  ...\n"
            + "  [85] link \"Gmail \" [url: https://mail.google.com/mail/&ogbl]\n"
            + "    [4] StaticText \"Gmail\"\n"
            + "*" + " [91] button \"Google apps\" [expanded: False]\n"
            + "  [21] combobox \"Search\" [editable: plaintext]\n"
            + "  [30] img \"logo\" [url: data:image/png;base64,AAAA]\n"
            + "  ...",
            view.Context.Content);
    }

    [Fact]
    public async Task web_at_viewer_is_empty_without_a_tree_or_a_matching_element()
    {
        var scripted = new ScriptedBrowserSandbox();
        scripted.AnswerCrawler("web_go", Tree);
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);
        var viewer = WebBrowser.WebAtViewer();

        // no tree yet
        Assert.Null(viewer(new ToolCall("1", "web_browser_click", Args(new { element_id = 91 }))).Context);

        await WebBrowser.Go().Execute(Args(new { url = "https://www.google.com" }), CancellationToken.None);
        Assert.Null(viewer(new ToolCall("1", "web_browser_click", Args(new { element_id = 999 }))).Context);
        Assert.Null(viewer(new ToolCall("1", "web_browser_click", Args(new { element_id = 0 }))).Context);
        Assert.Null(viewer(new ToolCall("1", "web_browser_click", new JsonObject())).Context);
        // the instance-bound viewer reads its own instance's tree
        Assert.Null(WebBrowser.WebAtViewer("other")(new ToolCall("1", "web_browser_click", Args(new { element_id = 91 }))).Context);
    }

    // ---- deprecated image fallback ----

    [Fact]
    public async Task falls_back_to_the_deprecated_image_client_when_only_that_image_is_present()
    {
        ProviderLogger.Reset();
        var scripted = new ScriptedBrowserSandbox(hasLegacyCli: false, hasOldClient: true)
        {
            OldClientOutput = "web_url: https://old.example\nmain_content: Old page\nsecond line\nweb_at: [1] RootWebArea \"Old\"\n  [2] link \"x\" [url: data:image/png;base64,QQ==]\ninfo: done\n",
        };
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);

        var result = await WebBrowser.Click().Execute(Args(new { element_id = 427 }), CancellationToken.None);
        await WebBrowser.Back().Execute(new JsonObject(), CancellationToken.None);

        Assert.Equal(3, scripted.OldClientCalls.Count);
        Assert.Equal(["python3", "/app/web_browser/web_client_new_session.py"], scripted.OldClientCalls[0].Cmd);
        Assert.Equal(["python3", "/app/web_browser/web_client.py", "--session_name=old-session", "web_click", "427"], scripted.OldClientCalls[1].Cmd);
        Assert.Equal(["python3", "/app/web_browser/web_client.py", "--session_name=old-session", "web_back"], scripted.OldClientCalls[2].Cmd);
        Assert.All(scripted.OldClientCalls, call => Assert.Equal(TimeSpan.FromSeconds(180), call.Timeout));
        Assert.Empty(scripted.Requests);

        var contents = result.Contents!;
        Assert.Equal("main content:\nOld page\nsecond line\n\n", Assert.IsType<ContentText>(contents[0]).Text);
        Assert.Equal("accessibility tree:\n[1] RootWebArea \"Old\"\n  [2] link \"x\" [url: ", Assert.IsType<ContentText>(contents[1]).Text);
        Assert.Equal("old-session", scope.Context.Store.Get("WebBrowserStore:session_id"));
        Assert.Contains(WebBrowserBackCompat.DeprecationWarning, ProviderLogger.Warnings);
        Assert.Single(ProviderLogger.Warnings, w => w == WebBrowserBackCompat.DeprecationWarning);
    }

    [Fact]
    public async Task deprecated_client_errors_are_tool_errors_and_malformed_output_fails_the_sample()
    {
        var scripted = new ScriptedBrowserSandbox(hasLegacyCli: false, hasOldClient: true) { OldClientOutput = "error: no such element\n" };
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);

        var ex = await Assert.ThrowsAsync<ToolError>(() => WebBrowser.Click().Execute(Args(new { element_id = 1 }), CancellationToken.None));
        Assert.Equal("no such element", ex.Message);

        scripted.OldClientOutput = "info: nothing useful\n";
        var parsed = WebBrowserBackCompat.ParseWebBrowserOutput(scripted.OldClientOutput);
        Assert.Equal("nothing useful", parsed["info"]);
        Assert.Equal(["web_url", "main_content", "web_at", "info", "error"], parsed.Keys);
    }

    [Fact]
    public async Task when_neither_image_is_present_the_new_images_guidance_wins()
    {
        var scripted = new ScriptedBrowserSandbox(hasLegacyCli: false, hasOldClient: false);
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);

        var ex = await Assert.ThrowsAsync<PrerequisiteError>(() => WebBrowser.Scroll().Execute(Args(new { direction = "down" }), CancellationToken.None));

        Assert.Contains("aisiuk/inspect-tool-support", ex.Message);
        Assert.DoesNotContain("aisiuk/inspect-web-browser-tool", ex.Message);
        Assert.Contains("aisiuk/inspect-web-browser-tool", WebBrowserBackCompat.NotFoundMessage);
    }
}

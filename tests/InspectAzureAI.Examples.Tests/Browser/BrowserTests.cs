using System.Reflection;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Examples.Browser;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.Browser;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Tests for the port of <c>examples/browser/browser.py</c> <c>browser</c> (<see cref="BrowserExample"/>): the task's
/// shape, the scripted <c>inspect-tool-support</c> service of <see cref="FakeBrowserSandbox"/>, the scripted model's
/// link picking and summary, and the example end to end without a network or Docker (the fake model against the
/// fake sandbox, through <c>Eval.RunAsync</c> and the examples runner).
/// </summary>
public sealed class BrowserTests : IDisposable
{
    private static readonly string ExampleDirectory = Path.Combine(AppContext.BaseDirectory, "browser");

    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "browser-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_logDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static ExampleContext Context(SandboxSpec? sandbox = null, bool fake = true) =>
        new(ExampleDirectory, sandbox, fake, new Dictionary<string, string>(), null, null, TextWriter.Null);

    // ----------------------------------------------------------------------------------------------------------
    // the task (port of @task def browser)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_task_is_named_and_shaped_like_the_python_browser_task()
    {
        var task = BrowserExample.Build(new SandboxSpec("docker", "compose.yaml"));

        Assert.Equal("browser", task.Name);
        var sample = Assert.Single(task.Dataset);
        Assert.Equal(
            "Use the web browser tool to navigate to https://www.aisi.gov.uk/. Then, see if you can find a page on the site that describes the work of the UK AISI. Then, summarize this work in two paragraphs.",
            sample.Input.Text);
        Assert.DoesNotContain(sample.Target.Values, value => value.Length > 0);
        Assert.Equal("includes", Assert.Single(task.Scorers).Name);
        Assert.Equal(new SandboxSpec("docker", "compose.yaml"), task.Sandbox);
        Assert.Null(task.MessageLimit);
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli_with_the_example_compose_file()
    {
        var method = typeof(BrowserExample).GetMethod(nameof(BrowserExample.BrowserTask), BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.Equal("browser", method.GetCustomAttribute<TaskAttribute>()?.Name);
        var task = BrowserExample.BrowserTask();
        Assert.Equal("docker", task.Sandbox?.Type);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "browser", "compose.yaml"), task.Sandbox?.Config);
        Assert.True(File.Exists(task.Sandbox!.Config!), $"compose.yaml was not copied next to the assembly: {task.Sandbox.Config}");
        Assert.Contains("image: aisiuk/inspect-tool-support", File.ReadAllText(task.Sandbox.Config));
    }

    [Fact]
    public void the_tools_are_the_eight_web_browser_tools()
    {
        var tools = WebBrowser.Create();

        Assert.Equal(["web_browser_go", "web_browser_click", "web_browser_type_submit", "web_browser_type", "web_browser_scroll", "web_browser_back", "web_browser_forward", "web_browser_refresh"], tools.Select(tool => tool.Name));
        Assert.All(tools, tool => Assert.False(tool.Parallel));
    }

    // ----------------------------------------------------------------------------------------------------------
    // the example
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_example_declares_the_python_task_a_docker_sandbox_and_a_fake_sandbox()
    {
        var example = Assert.IsType<BrowserExample>(ExampleRegistry.Default.Get("browser"));

        Assert.Equal("browser", example.Name);
        Assert.Equal(["browser"], example.Tasks.Select(task => task.Name));
        Assert.Equal(new ExampleDefaults("docker", "compose.yaml", null, true, example.Defaults.ModelHint), example.Defaults);
        Assert.NotNull(example.FakeSandbox(Context()));
        Assert.NotEmpty(example.Deviations);
        Assert.Equal("browser-scripted", example.CreateFakeModel(Context()).Name);
    }

    [Fact]
    public void building_without_a_sandbox_is_a_prerequisite_error_and_a_fake_build_caps_the_messages()
    {
        var example = new BrowserExample();

        Assert.Throws<PrerequisiteError>(() => example.Tasks[0].Build(Context(sandbox: null)));
        var fake = example.Tasks[0].Build(Context(new SandboxSpec("fake"), fake: true));
        Assert.Equal(BrowserExample.FakeMessageLimit, fake.MessageLimit);
        var live = example.Tasks[0].Build(Context(new SandboxSpec("docker", "compose.yaml"), fake: false));
        Assert.Null(live.MessageLimit);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the fake sandbox and the fake model
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_fake_sandbox_plays_the_inspect_tool_support_service()
    {
        var script = FakeBrowserSandbox.Create();
        using var environment = new ScriptedSandboxEnvironment(script);

        var which = await environment.ExecAsync(["which", "inspect-tool-support"]);
        Assert.True(which.Success);

        var version = await environment.ExecAsync(["inspect-tool-support", "exec"], input: """{"jsonrpc":"2.0","method":"version","params":{},"id":7}""");
        Assert.Equal("1.2.3", JsonNode.Parse(version.Stdout)!["result"]!.GetValue<string>());
        Assert.Equal(7, JsonNode.Parse(version.Stdout)!["id"]!.GetValue<int>());

        var session = await environment.ExecAsync(["inspect-tool-support", "exec"], input: """{"jsonrpc":"2.0","method":"web_new_session","params":{"headful":false},"id":8}""");
        Assert.Equal(FakeBrowserSandbox.SessionName, JsonNode.Parse(session.Stdout)!["result"]!["session_name"]!.GetValue<string>());

        var go = await environment.ExecAsync(["inspect-tool-support", "exec"], input: """{"jsonrpc":"2.0","method":"web_go","params":{"url":"https://www.aisi.gov.uk/","session_name":"aisi-session"},"id":9}""");
        var home = JsonNode.Parse(go.Stdout)!["result"]!;
        Assert.Equal(FakeBrowserSandbox.HomeUrl, home["web_url"]!.GetValue<string>());
        Assert.Null(home["main_content"]?.GetValue<string>());
        Assert.Contains($"[{FakeBrowserSandbox.AboutLinkId}] link \"About\"", home["web_at"]!.GetValue<string>());

        var click = await environment.ExecAsync(["inspect-tool-support", "exec"], input: $$"""{"jsonrpc":"2.0","method":"web_click","params":{"element_id":{{FakeBrowserSandbox.AboutLinkId}},"session_name":"aisi-session"},"id":10}""");
        var about = JsonNode.Parse(click.Stdout)!["result"]!;
        Assert.Equal(FakeBrowserSandbox.AboutUrl, about["web_url"]!.GetValue<string>());
        Assert.Equal(FakeBrowserSandbox.AboutMainContent, about["main_content"]!.GetValue<string>());

        var unknown = await environment.ExecAsync(["inspect-tool-support", "exec"], input: """{"jsonrpc":"2.0","method":"nope","params":{},"id":11}""");
        Assert.Equal(-32601, JsonNode.Parse(unknown.Stdout)!["error"]!["code"]!.GetValue<int>());

        var other = await environment.ExecAsync(["ls"]);
        Assert.False(other.Success);
        Assert.Equal(["version", "web_new_session", "web_go", "web_click", "nope"], FakeBrowserSandbox.Methods(script));
    }

    [Fact]
    public void the_fake_model_picks_the_about_link_and_writes_two_paragraphs()
    {
        Assert.Equal(FakeBrowserSandbox.AboutLinkId, FakeBrowserModel.LinkId(FakeBrowserSandbox.HomeTree));
        Assert.Equal(5, FakeBrowserModel.LinkId("[1] RootWebArea \"x\"\n  [5] link \"Home\" [url: https://x/]\n  [9] link \"Research\""));
        Assert.Null(FakeBrowserModel.LinkId("[1] RootWebArea \"x\"\n  [22] heading \"No links\""));

        var summary = FakeBrowserModel.Summary($"main content:\n{FakeBrowserSandbox.AboutMainContent}\n\naccessibility tree:\n{FakeBrowserSandbox.AboutTree}");
        Assert.Equal(2, summary.Split("\n\n").Length);
        Assert.Contains("pre-deployment evaluations of frontier AI systems", summary);

        var fromTree = FakeBrowserModel.Summary($"accessibility tree:\n{FakeBrowserSandbox.AboutTree}");
        Assert.Contains("research organisation within the UK government", fromTree);
    }

    // ----------------------------------------------------------------------------------------------------------
    // end to end, offline
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_scripted_run_goes_to_the_site_clicks_about_and_summarises_it()
    {
        var script = FakeBrowserSandbox.Create();
        var sandbox = ScriptedSandboxProvider.Register(script);
        var api = new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(FakeBrowserModel.Respond), 6), FakeBrowserModel.ModelName);
        var task = BrowserExample.Build(sandbox) with { MessageLimit = BrowserExample.FakeMessageLimit };

        var log = await Eval.RunAsync(task, new EvalOptions { Model = new Model(api), LogDir = _logDir, LogFormat = LogFormat.Eval });

        Assert.True(log.Status == EvalStatus.Success, $"status {log.Status}: {log.Error?.Message ?? "(no error)"}");
        var sample = Assert.Single(log.Samples!);
        Assert.Equal("C", sample.Scores!["includes"].Text);

        // go, then click on the About link, then the answer
        var calls = sample.Messages.OfType<ChatMessageAssistant>().SelectMany(message => message.ToolCalls ?? []).ToList();
        Assert.Equal(["web_browser_go", "web_browser_click"], calls.Select(call => call.Function));
        Assert.Equal(FakeBrowserSandbox.HomeUrl, calls[0].Arguments["url"]!.GetValue<string>());
        Assert.Equal(FakeBrowserSandbox.AboutLinkId, calls[1].Arguments["element_id"]!.GetValue<int>());
        var results = sample.Messages.OfType<ChatMessageTool>().ToList();
        Assert.All(results, result => Assert.Null(result.Error));
        Assert.DoesNotContain("data:image/png;base64", results[0].Text);
        Assert.StartsWith("main content:\n", results[1].Text);
        var answer = Assert.IsType<ChatMessageAssistant>(sample.Messages[^1]).Text;
        Assert.Equal(2, answer.Split("\n\n").Length);
        Assert.Contains("pre-deployment evaluations", answer);

        // the service saw the session created once and the two navigations, each preceded by a version probe
        Assert.Equal(["version", "web_new_session", "web_go", "version", "web_click"], FakeBrowserSandbox.Methods(script));
        Assert.Single(script.Calls, call => call.Cmd[0] == "which");
        Assert.Equal(3, api.Requests.Count);
    }

    [Fact]
    public async Task the_runner_runs_the_example_offline_with_the_fake_sandbox()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["browser", "--fake", "--sandbox", "fake", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        Assert.True(exit == 0, output.ToString());
        Assert.Contains("status    : success", output.ToString());
        Assert.Contains("includes", output.ToString());
        Assert.Single(Directory.EnumerateFiles(_logDir, "*.eval"));
    }

    [Fact]
    public async Task the_runner_refuses_a_run_without_a_sandbox()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["browser", "--fake", "--sandbox", "none", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        Assert.Equal(2, exit);
        Assert.Contains("needs a sandbox", output.ToString());
    }
}

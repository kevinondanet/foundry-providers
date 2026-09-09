using System.Reflection;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Examples.McpTools;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.McpTools;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Tests for the port of <c>examples/mcp_tools.py</c> <c>mcp_git_tools</c> (<see cref="McpGitTools"/>): the task's
/// shape, the <c>mcp_tools(..., tools=["git_log", "git_status"])</c> filter, and the example end to end without a
/// network or a python process — the scripted model of <see cref="McpToolsExample"/> against the in-process
/// <see cref="FakeGitServer"/>.
/// </summary>
public sealed class McpToolsTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "mcp-tools-" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public void the_task_is_named_and_shaped_like_the_python_mcp_git_tools()
    {
        var task = McpGitTools.Build(FakeGitServer.Create());

        Assert.Equal("mcp_git_tools", task.Name);
        Assert.Single(task.Dataset);
        Assert.Equal(
            "What is the status of the git working tree for the current directory?. Additionally, could you summarise recent commits that have been made to the reposiotry?",
            task.Dataset[0].Input.Text);
        Assert.False(task.Config.ParallelToolCalls);
        Assert.Null(task.Sandbox);
        Assert.Empty(task.Scorers);
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli_as_mcp_git_tools()
    {
        var method = typeof(McpGitTools).GetMethod(nameof(McpGitTools.McpGitToolsTask), BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.Equal("mcp_git_tools", method.GetCustomAttribute<TaskAttribute>()?.Name);
        // Building the task only describes the stdio server (python3 -m mcp_server_git --repository .); no process starts.
        var task = McpGitTools.McpGitToolsTask();
        Assert.Equal("mcp_git_tools", task.Name);
    }

    [Fact]
    public void the_git_server_is_the_python_stdio_command_line()
    {
        var server = Assert.IsType<McpServerLocal>(McpGitTools.GitServer());

        Assert.Equal("python3 -m mcp_server_git --repository .", server.Name);
        Assert.True(server.Events);
    }

    [Fact]
    public async Task mcp_tools_filters_the_server_to_git_log_and_git_status()
    {
        var transport = FakeGitServer.CreateTransport();
        var source = Mcp.McpTools(transport.AsMcpServer(), McpGitTools.ToolNames);

        await using var connection = await McpConnection.ConnectAsync(source);
        var tools = await source.ToolsAsync();
        var all = await transport.AsMcpServer().ToolsAsync();

        Assert.Equal(["git_log", "git_status"], tools.Select(tool => tool.Name).Order());
        Assert.Contains("git_diff_unstaged", all.Select(tool => tool.Name));
    }

    [Fact]
    public void the_example_declares_the_python_task_and_no_sandbox()
    {
        var example = new McpToolsExample();

        Assert.Equal("mcp_tools", example.Name);
        Assert.Equal(["mcp_git_tools"], example.Tasks.Select(task => task.Name));
        Assert.Equal("none", example.Defaults.Sandbox);
        Assert.Null(example.FakeSandbox(Context(fake: true)));
        Assert.NotEmpty(example.Deviations);
    }

    [Fact]
    public async Task the_scripted_run_calls_status_then_log_over_one_session_and_submits()
    {
        var transport = FakeGitServer.CreateTransport();
        var task = McpGitTools.Build(transport.AsMcpServer()) with { MessageLimit = McpToolsExample.FakeMessageLimit };
        var api = new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(McpToolsExample.Respond), 8), McpToolsExample.FakeModelName);

        var log = await Eval.RunAsync(task, new EvalOptions { Model = new Model(api), LogDir = _logDir, LogFormat = LogFormat.Eval });

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        var calls = sample.Messages.OfType<ChatMessageAssistant>().SelectMany(message => message.ToolCalls ?? []).Select(call => call.Function).ToList();
        // react strips the submit call from the messages and leaves its answer as the completion.
        Assert.Equal(["git_status", "git_log"], calls);
        var results = sample.Messages.OfType<ChatMessageTool>().ToList();
        Assert.Equal(FakeGitServer.Status, results[0].Text);
        Assert.Equal(FakeGitServer.Log, results[1].Text);
        Assert.All(results, result => Assert.Null(result.Error));
        // The model only ever saw the two selected tools (plus submit), and the server session was held for the whole loop.
        Assert.All(api.Requests, request => Assert.Equal(["git_log", "git_status", "submit"], request.Tools.Select(tool => tool.Name).Order()));
        Assert.Equal(1, transport.Connects);
        Assert.Contains("Recent commits: the 3 most recent commits are", sample.Output.Completion);
    }

    [Fact]
    public async Task the_runner_runs_the_example_offline()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(
            ["mcp_tools", "--fake", "--sandbox", "none", "--log-dir", _logDir],
            ExampleRegistry.Of(new McpToolsExample()),
            output,
            TextWriter.Null);

        Assert.Equal(0, exit);
        Assert.Contains("status    : success", output.ToString());
    }

    private static ExampleContext Context(bool fake) =>
        new(Path.Combine(AppContext.BaseDirectory, "mcp_tools"), null, fake, new Dictionary<string, string>(), null, null, TextWriter.Null);
}

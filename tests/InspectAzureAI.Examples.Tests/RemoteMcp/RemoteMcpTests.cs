using System.Reflection;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Examples.RemoteMcp;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.RemoteMcp;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Tests for the port of <c>examples/remotemcp.py</c> <c>remote_mcp</c> (<see cref="Examples.RemoteMcp.RemoteMcp"/>):
/// the task's shape, the remote server marker, and the example end to end without a network — the scripted model of
/// <see cref="RemoteMcpExample"/> answering the marker with an <c>mcp_call</c> block, and, under local execution,
/// calling the in-process <see cref="FakeDeepWikiServer"/>.
/// </summary>
public sealed class RemoteMcpTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "remotemcp-" + Guid.NewGuid().ToString("N"));

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
    public void the_task_is_named_and_shaped_like_the_python_remote_mcp()
    {
        var task = Examples.RemoteMcp.RemoteMcp.RemoteMcpTask();

        Assert.Equal("remote_mcp", task.Name);
        Assert.Single(task.Dataset);
        Assert.Equal("What transport protocols are supported in the 2025-03-26 version of the MCP spec?", task.Dataset[0].Input.Text);
        Assert.Null(task.Sandbox);
        Assert.Empty(task.Scorers);
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli_as_remote_mcp()
    {
        var method = typeof(Examples.RemoteMcp.RemoteMcp).GetMethod(nameof(Examples.RemoteMcp.RemoteMcp.RemoteMcpTask), BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.Equal("remote_mcp", method.GetCustomAttribute<TaskAttribute>()?.Name);
    }

    [Fact]
    public async Task deepwiki_is_a_remote_http_server_whose_only_tool_is_the_marker()
    {
        var server = Assert.IsType<McpServerRemote>(Examples.RemoteMcp.RemoteMcp.DeepWiki());

        Assert.Equal("deepwiki", server.Config.Name);
        Assert.Equal("https://mcp.deepwiki.com/mcp", server.Config.Url);
        Assert.Equal("http", server.Config.Type);
        var tool = Assert.Single(await server.ToolsAsync());
        Assert.Equal("mcp_server_deepwiki", tool.Name);
        Assert.True(McpServerRemote.IsMcpServerTool(tool.ToInfo()));
    }

    [Fact]
    public void local_execution_is_a_local_server_named_deepwiki()
    {
        var server = Assert.IsType<McpServerLocal>(Examples.RemoteMcp.RemoteMcp.DeepWiki(McpExecution.Local));

        Assert.Equal("deepwiki", server.Name);
    }

    [Theory]
    [InlineData(null, McpExecution.Remote)]
    [InlineData("remote", McpExecution.Remote)]
    [InlineData("Local", McpExecution.Local)]
    public void the_execution_task_arg_defaults_to_remote(string? value, McpExecution expected) => Assert.Equal(expected, RemoteMcpExample.ParseExecution(value));

    [Fact]
    public void a_bad_execution_task_arg_is_an_argument_exception() => Assert.Throws<ArgumentException>(() => RemoteMcpExample.ParseExecution("sandbox"));

    [Fact]
    public async Task the_scripted_remote_run_answers_the_marker_with_an_mcp_call_block_and_submits()
    {
        var task = Examples.RemoteMcp.RemoteMcp.Build(Examples.RemoteMcp.RemoteMcp.DeepWiki()) with { MessageLimit = RemoteMcpExample.FakeMessageLimit };
        var api = new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(RemoteMcpExample.Respond), 8), RemoteMcpExample.FakeModelName) { SupportsRemoteMcp = true };

        var log = await Eval.RunAsync(task, new EvalOptions { Model = new Model(api), LogDir = _logDir, LogFormat = LogFormat.Eval });

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal(["mcp_server_deepwiki", "submit"], api.Requests[0].Tools.Select(tool => tool.Name).Order());
        var sample = Assert.Single(log.Samples!);
        var assistant = sample.Messages.OfType<ChatMessageAssistant>().ToList();
        var call = Assert.Single((assistant[0].Content.Items ?? []).OfType<ContentToolUse>());
        Assert.Equal("mcp_call", call.ToolType);
        Assert.Equal("ask_question", call.Name);
        Assert.Equal("deepwiki", call.Context);
        Assert.Contains("modelcontextprotocol/modelcontextprotocol", call.Arguments);
        // react strips the submit call from the messages and leaves its answer as the completion.
        Assert.Empty(assistant[^1].ToolCalls ?? []);
        Assert.Contains("stdio and Streamable HTTP", sample.Output.Completion);
    }

    [Fact]
    public async Task the_scripted_local_run_calls_ask_question_on_the_in_process_server()
    {
        var transport = FakeDeepWikiServer.CreateTransport();
        var task = Examples.RemoteMcp.RemoteMcp.Build(transport.AsMcpServer()) with { MessageLimit = RemoteMcpExample.FakeMessageLimit };
        var api = new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(RemoteMcpExample.Respond), 8), RemoteMcpExample.FakeModelName);

        var log = await Eval.RunAsync(task, new EvalOptions { Model = new Model(api), LogDir = _logDir, LogFormat = LogFormat.Eval });

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal(["ask_question", "read_wiki_contents", "read_wiki_structure", "submit"], api.Requests[0].Tools.Select(tool => tool.Name).Order());
        var sample = Assert.Single(log.Samples!);
        var calls = sample.Messages.OfType<ChatMessageAssistant>().SelectMany(message => message.ToolCalls ?? []).Select(call => call.Function).ToList();
        // react strips the submit call from the messages and leaves its answer as the completion.
        Assert.Equal(["ask_question"], calls);
        var result = Assert.Single(sample.Messages.OfType<ChatMessageTool>(), message => message.Function == "ask_question");
        Assert.Equal(FakeDeepWikiServer.TransportsAnswer, result.Text);
        Assert.Equal(1, transport.Connects);
    }

    [Fact]
    public async Task a_model_without_remote_mcp_support_rejects_the_marker_with_the_python_message()
    {
        var task = Examples.RemoteMcp.RemoteMcp.Build(Examples.RemoteMcp.RemoteMcp.DeepWiki()) with { MessageLimit = RemoteMcpExample.FakeMessageLimit };
        var api = new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(RemoteMcpExample.Respond), 8), RemoteMcpExample.FakeModelName);

        var log = await Eval.RunAsync(task, new EvalOptions { Model = new Model(api), LogDir = _logDir, LogFormat = LogFormat.Eval });

        Assert.NotEqual(EvalStatus.Success, log.Status);
        Assert.Contains("Remote MCP execution is not supported for", log.Error?.Message ?? string.Join("\n", log.Samples?.Select(sample => sample.Error?.Message) ?? []));
    }

    [Fact]
    public void the_example_declares_the_python_task_and_no_sandbox()
    {
        var example = new RemoteMcpExample();

        Assert.Equal("remotemcp", example.Name);
        Assert.Equal(["remote_mcp"], example.Tasks.Select(task => task.Name));
        Assert.Equal("none", example.Defaults.Sandbox);
        Assert.NotEmpty(example.Deviations);
    }

    [Theory]
    [InlineData("remote")]
    [InlineData("local")]
    public async Task the_runner_runs_the_example_offline(string execution)
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(
            ["remotemcp", "--fake", "--sandbox", "none", "--log-dir", _logDir, "-T", $"execution={execution}"],
            ExampleRegistry.Of(new RemoteMcpExample()),
            output,
            TextWriter.Null);

        Assert.Equal(0, exit);
        Assert.Contains("status    : success", output.ToString());
    }
}

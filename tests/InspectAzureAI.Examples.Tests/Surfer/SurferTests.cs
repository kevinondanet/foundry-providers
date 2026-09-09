using System.Reflection;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Examples.Surfer;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.Surfer;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Tests for the port of <c>examples/surfer.py</c>: the <c>surfer</c> task's shape (<see cref="Examples.Surfer.Surfer"/>),
/// the <c>web_surfer</c> tool's description, parameters and store-backed history (<see cref="WebSurfer"/>,
/// <see cref="WebSurferState"/>), and the example end to end without a network — the scripted model of
/// <see cref="SurferExample"/> over the canned Tavily transport of <see cref="FakeWebSearch"/>.
/// </summary>
public sealed class SurferTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "surfer-" + Guid.NewGuid().ToString("N"));

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
    public void the_task_is_named_and_shaped_like_the_python_surfer()
    {
        var task = Examples.Surfer.Surfer.SurferTask();

        Assert.Equal("surfer", task.Name);
        Assert.Single(task.Dataset);
        Assert.Equal("What were the scores of last night's NHL games?", task.Dataset[0].Input.Text);
        Assert.Null(task.Sandbox);
        Assert.Empty(task.Scorers);
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli_as_surfer()
    {
        var method = typeof(Examples.Surfer.Surfer).GetMethod(nameof(Examples.Surfer.Surfer.SurferTask), BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.Equal("surfer", method.GetCustomAttribute<TaskAttribute>()?.Name);
    }

    [Fact]
    public void web_surfer_describes_itself_like_the_python_tool()
    {
        var tool = WebSurfer.Create("test");

        Assert.Equal("web_surfer", tool.Name);
        Assert.StartsWith("Use the web to research a topic.\n\nYou may ask the web surfer any question.", tool.Description, StringComparison.Ordinal);
        Assert.Equal(["input"], tool.Parameters.Required);
        Assert.Equal(["string"], tool.Parameters.Properties["input"].Type);
        Assert.Equal(WebSurfer.InputDescription, tool.Parameters.Properties["input"].Description);
        Assert.Equal(["boolean"], tool.Parameters.Properties["clear_history"].Type);
        Assert.Equal(WebSurfer.ClearHistoryDescription, tool.Parameters.Properties["clear_history"].Description);
        Assert.False(tool.Parameters.Properties["clear_history"].Default!.GetValue<bool>());
    }

    [Fact]
    public async Task web_surfer_keeps_its_conversation_in_the_store_per_instance_and_clears_it_on_request()
    {
        var handler = new FakeWebSearch.CannedTavilyHandler();
        var api = new ScriptedModelApi(
            ScriptedTurn.ToolCall("web_search", new { query = "NHL scores" }, text: "Searching."),
            ScriptedTurn.Text(FakeWebSearch.Answer),
            ScriptedTurn.Text("Boston beat Toronto 4-2."),
            ScriptedTurn.ToolCall("web_search", new { query = "NHL scores" }),
            ScriptedTurn.Text(FakeWebSearch.Answer));
        var context = new SampleContext { ActiveModel = new Model(api) };
        var tool = WebSurfer.Create("surfer-1", () => FakeWebSearch.Tool(handler));

        using var scope = SampleContext.Begin(context);
        var first = await tool.Execute(new JsonObject { ["input"] = "What were last night's NHL scores?" }, CancellationToken.None);
        var state = context.Store.As<WebSurferState>("surfer-1");

        Assert.Equal(FakeWebSearch.Answer, first.Text);
        // system + user + assistant (tool call) + tool + assistant (answer)
        Assert.Equal(5, state.Messages.Count);
        Assert.Equal(WebSurfer.SystemPrompt, Assert.IsType<ChatMessageSystem>(state.Messages[0]).Text);
        Assert.Equal(["NHL scores"], handler.Queries);

        var second = await tool.Execute(new JsonObject { ["input"] = "Who won the Boston game?" }, CancellationToken.None);

        Assert.Equal("Boston beat Toronto 4-2.", second.Text);
        Assert.Equal(7, state.Messages.Count);
        Assert.Equal(["NHL scores"], handler.Queries);

        var third = await tool.Execute(new JsonObject { ["input"] = "Start over: last night's NHL scores?", ["clear_history"] = true }, CancellationToken.None);

        Assert.Equal(FakeWebSearch.Answer, third.Text);
        Assert.Equal(5, state.Messages.Count);
        Assert.Equal("Start over: last night's NHL scores?", Assert.IsType<ChatMessageUser>(state.Messages[1]).Text);
        Assert.Equal(2, handler.Queries.Count);
        // Another instance has its own history.
        Assert.Empty(context.Store.As<WebSurferState>("surfer-2").Messages);
    }

    [Fact]
    public async Task the_scripted_run_searches_once_and_submits_the_answer()
    {
        var handler = new FakeWebSearch.CannedTavilyHandler();
        var task = Examples.Surfer.Surfer.Build(FakeWebSearch.Tool(handler)) with { MessageLimit = SurferExample.FakeMessageLimit };
        var api = new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(SurferExample.Respond), 8), SurferExample.FakeModelName);

        var log = await Eval.RunAsync(task, new EvalOptions { Model = new Model(api), LogDir = _logDir, LogFormat = LogFormat.Eval });

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal(["submit", "web_search"], api.Requests[0].Tools.Select(tool => tool.Name).Order());
        Assert.Equal([SurferExample.FakeQuery], handler.Queries);
        var sample = Assert.Single(log.Samples!);
        var calls = sample.Messages.OfType<ChatMessageAssistant>().SelectMany(message => message.ToolCalls ?? []).Select(call => call.Function).ToList();
        // react strips the submit call from the messages and leaves its answer as the completion.
        Assert.Equal(["web_search"], calls);
        var result = Assert.Single(sample.Messages.OfType<ChatMessageTool>(), message => message.Function == "web_search");
        Assert.Equal(FakeWebSearch.Answer, result.Text.Trim());
        var text = Assert.IsType<ContentText>(Assert.Single(result.Content.Items ?? []));
        Assert.Equal(FakeWebSearch.Results.Count, text.Citations?.Count);
        Assert.EndsWith(FakeWebSearch.Answer, sample.Output.Completion, StringComparison.Ordinal);
    }

    [Fact]
    public void the_example_declares_the_python_task_and_no_sandbox()
    {
        var example = new SurferExample();

        Assert.Equal("surfer", example.Name);
        Assert.Equal(["surfer"], example.Tasks.Select(task => task.Name));
        Assert.Equal("none", example.Defaults.Sandbox);
        Assert.NotEmpty(example.Deviations);
    }

    [Fact]
    public async Task the_runner_runs_the_example_offline()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(
            ["surfer", "--fake", "--sandbox", "none", "--log-dir", _logDir],
            ExampleRegistry.Of(new SurferExample()),
            output,
            TextWriter.Null);

        Assert.Equal(0, exit);
        Assert.Contains("status    : success", output.ToString());
    }
}

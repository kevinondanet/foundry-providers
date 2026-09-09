using System.Reflection;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.ResponsesBridge;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Examples.Tests.Bridge;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.ResponsesBridge;

/// <summary>
/// Tests for the port of <c>examples/responses-bridge.py</c> (<see cref="ResponsesAgent"/>, <see cref="BridgedTask"/>,
/// <see cref="ResponsesBridgeExample"/>): the task's shape, the agent's single bridged call, and the offline run
/// through <c>Eval.RunAsync</c> and the examples runner.
/// </summary>
public sealed class ResponsesBridgeTests : IDisposable
{
    private readonly string _logDir = BridgeTestSupport.NewLogDir("responses-bridge");

    public void Dispose() => BridgeTestSupport.DeleteLogDir(_logDir);

    [Fact]
    public void the_task_is_shaped_like_the_python_bridged_task()
    {
        var task = BridgedTask.Build();

        Assert.Equal("bridged_task", task.Name);
        var sample = Assert.Single(task.Dataset);
        Assert.Equal("Please print the word 'hello'?", sample.Input.Text);
        Assert.Equal("hello", sample.Target.Text);
        Assert.Equal("includes", Assert.Single(task.Scorers).Name);
        Assert.Null(task.Sandbox);
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli()
    {
        var method = typeof(BridgedTask).GetMethod(nameof(BridgedTask.Build));

        Assert.NotNull(method);
        Assert.True(method!.IsStatic);
        Assert.Equal("bridged_task", method.GetCustomAttribute<TaskAttribute>()!.Name);
    }

    [Fact]
    public void user_prompt_is_the_last_user_message()
    {
        var first = new ChatMessageUser("first");
        var last = new ChatMessageUser("last");

        Assert.Same(last, ResponsesAgent.UserPrompt([new ChatMessageSystem("sys"), first, new ChatMessageAssistant("a"), last]));
        Assert.Throws<InvalidOperationException>(() => ResponsesAgent.UserPrompt([new ChatMessageSystem("sys")]));
    }

    [Fact]
    public void the_example_describes_itself()
    {
        var example = new ResponsesBridgeExample();

        Assert.Equal("responses-bridge", example.Name);
        Assert.Equal(["bridged_task"], example.Tasks.Select(task => task.Name));
        Assert.Equal("none", example.Defaults.Sandbox);
        Assert.Null(example.FakeSandbox(BridgeTestSupport.FakeContext("responses-bridge")));
        Assert.NotEmpty(example.Deviations);
        Assert.IsType<ResponsesBridgeExample>(ExampleRegistry.Default.Find("responses-bridge"));
    }

    [Fact]
    public async Task the_agent_makes_one_bridged_call_with_the_user_prompt_and_returns_the_bridge_state()
    {
        var example = new ResponsesBridgeExample();
        var ctx = BridgeTestSupport.FakeContext("responses-bridge");
        var model = example.CreateFakeModel(ctx);
        var api = BridgeTestSupport.Scripted(model);

        var log = await BridgeTestSupport.RunAsync(example.Tasks[0].Build(ctx), model, _logDir);

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Equal("C", sample.Scores!["includes"].Text);
        Assert.Equal("hello", sample.Output.Completion);
        // the bridge tracked the one request and its reply into the state
        Assert.Collection(
            sample.Messages,
            message => Assert.Equal(BridgedTask.Input, Assert.IsType<ChatMessageUser>(message).Text),
            message => Assert.Equal("hello", Assert.IsType<ChatMessageAssistant>(message).Text));
        Assert.Single(sample.Events.OfType<ModelEvent>());
        // client.responses.create(model="inspect", input=user_prompt(state.messages).text): the prompt text alone, no tools
        var request = Assert.Single(api.Requests);
        Assert.Equal(BridgedTask.Input, Assert.IsType<ChatMessageUser>(Assert.Single(request.Input)).Text);
        Assert.Empty(request.Tools);
    }

    [Fact]
    public async Task the_runner_runs_the_example_offline_and_exits_0()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["responses-bridge", "--fake", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("status    : success (1/1 samples completed)", text);
        Assert.Contains("includes", text);
        Assert.Single(Directory.GetFiles(_logDir, "*.eval"));
    }
}

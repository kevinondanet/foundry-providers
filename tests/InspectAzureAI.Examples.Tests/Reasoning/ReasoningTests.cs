using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Reasoning;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.Reasoning;

// Declared inside the namespace so that `Eval` names the runner rather than the InspectAzureAI.Eval namespace.
using Eval = InspectAzureAI.Eval.Runner.Eval;

/// <summary>
/// Tests for the port of <c>examples/reasoning.py</c> (<see cref="ReasoningTask"/>, <see cref="ReasoningExample"/>):
/// the task's shape and reasoning config, the <c>validate</c> tool's schema and result, and the offline run with the
/// scripted reasoning model through <c>Eval.RunAsync</c> (two validate tool events, reasoning content in the model
/// events and messages, the reasoning config on every request) and the examples runner with the conversation display.
/// </summary>
public sealed class ReasoningTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "reasoning-" + Guid.NewGuid().ToString("N"));

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

    // ----------------------------------------------------------------------------------------------------------
    // the task (port of @task def reasoning)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_task_is_named_and_shaped_like_the_python_reasoning()
    {
        var task = ReasoningTask.Reasoning();

        Assert.Equal("reasoning", task.Name);
        var sample = Assert.Single(task.Dataset);
        Assert.Equal(ReasoningTask.Input, sample.Input.Text);
        Assert.StartsWith("Solve 3*x^3-5*x=1, then call the validate() tool validate your answer.", sample.Input.Text);
        Assert.Equal(Target.Empty, sample.Target);
        Assert.Equal("medium", task.Config.ReasoningEffort);
        Assert.Equal(8192, task.Config.ReasoningTokens);
        Assert.Equal(16384, task.Config.MaxTokens);
        // As in Python the task has no scorer and no sandbox.
        Assert.Empty(task.Scorers);
        Assert.Null(task.Sandbox);
        Assert.NotNull(task.Solver);
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli_as_reasoning()
    {
        var method = typeof(ReasoningTask).GetMethod(nameof(ReasoningTask.Reasoning))!;

        var attribute = Assert.IsType<TaskAttribute>(Assert.Single(method.GetCustomAttributes(typeof(TaskAttribute), inherit: false)));
        Assert.Equal("reasoning", attribute.Name);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the validate tool (port of @tool def validate)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task validate_has_the_python_tools_name_description_and_schema_and_always_returns_true()
    {
        var tool = ReasoningTask.Validate();

        Assert.Equal("validate", tool.Name);
        Assert.Equal("Validate the answer to a mathematical question.", tool.Description);
        var answer = Assert.Single(tool.Parameters.Properties);
        Assert.Equal("answer", answer.Key);
        Assert.Equal(["string"], answer.Value.Type);
        Assert.Equal("Answer to validate", answer.Value.Description);
        Assert.Equal(["answer"], tool.Parameters.Required);
        Assert.True(tool.Parallel);

        var result = await tool.Execute(new JsonObject { ["answer"] = "x = 2 or x = 3" }, CancellationToken.None);

        Assert.Equal("True", result.AsText());
    }

    // ----------------------------------------------------------------------------------------------------------
    // end to end, offline
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_scripted_model_reasons_validates_twice_and_submits()
    {
        var model = FakeReasoningModel.Create();
        var api = (ScriptedModelApi)model.Api;

        var log = await Eval.RunAsync(ReasoningTask.Reasoning(), new EvalOptions { Model = model, LogDir = _logDir, LogFormat = LogFormat.Eval });

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);
        Assert.Empty(sample.Scores ?? new Dictionary<string, Score>());
        Assert.Empty(log.Results!.Scores);

        // Two validate calls, each answered True, then the submit that ends the react loop.
        var toolEvents = sample.Events.OfType<ToolEvent>().ToList();
        Assert.Equal(["validate", "validate", "submit"], toolEvents.Select(toolEvent => toolEvent.Function));
        Assert.All(toolEvents.Where(toolEvent => toolEvent.Function == "validate"), toolEvent =>
        {
            Assert.Equal("True", toolEvent.Result);
            Assert.Null(toolEvent.Error);
        });
        Assert.Equal(FakeReasoningModel.Script[0].Answer, toolEvents[0].Arguments["answer"]!.GetValue<string>());
        Assert.Equal(FakeReasoningModel.Script[1].Answer, toolEvents[1].Arguments["answer"]!.GetValue<string>());

        // Every generation carried the task's reasoning config and produced reasoning content.
        var modelEvents = sample.Events.OfType<ModelEvent>().ToList();
        Assert.Equal(3, modelEvents.Count);
        Assert.All(modelEvents, modelEvent =>
        {
            Assert.Equal("medium", modelEvent.Config.ReasoningEffort);
            Assert.Equal(8192, modelEvent.Config.ReasoningTokens);
            Assert.Equal(16384, modelEvent.Config.MaxTokens);
            Assert.Contains(modelEvent.Output.Message.ContentList, item => item is ContentReasoning);
            Assert.NotNull(modelEvent.Output.Usage?.ReasoningTokens);
        });

        // The reasoning of both problems survives in the final conversation (react drops only the submit turn's).
        var reasoning = sample.Messages.OfType<ChatMessageAssistant>().SelectMany(message => message.ContentList.OfType<ContentReasoning>()).Select(item => item.Reasoning).ToList();
        Assert.Contains(FakeReasoningModel.Script[0].Reasoning, reasoning);
        Assert.Contains(FakeReasoningModel.Script[1].Reasoning, reasoning);
        Assert.True(sample.ModelUsage.Values.Sum(usage => usage.ReasoningTokens ?? 0) > 0, "reasoning tokens were not reported");

        // The react agent put the prompt in the system message and offered validate and submit.
        Assert.Equal(3, api.Requests.Count);
        var first = api.Requests[0];
        var system = Assert.IsType<ChatMessageSystem>(first.Input[0]);
        Assert.StartsWith(ReasoningTask.Prompt, system.Text);
        Assert.Equal(ReasoningTask.Input, Assert.IsType<ChatMessageUser>(first.Input[1]).Text);
        Assert.Equal(["validate", "submit"], first.Tools.Select(tool => tool.Name));
        Assert.Equal("medium", first.Config.ReasoningEffort);
        // Later turns replay the earlier reasoning items to the model.
        Assert.Contains(api.Requests[1].Input.OfType<ChatMessageAssistant>().SelectMany(message => message.ContentList), item => item is ContentReasoning);
    }

    [Fact]
    public async Task the_runner_runs_reasoning_offline_and_shows_the_reasoning_in_the_conversation_display()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["reasoning", "--fake", "--display", "conversation", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("task      : reasoning", text);
        Assert.Contains("model     : reasoning-scripted (scripted, offline)", text);
        Assert.Contains("sandbox   : none", text);
        Assert.Contains("display   : conversation", text);
        Assert.Contains("<think>", text);
        Assert.Contains(FakeReasoningModel.Script[1].Reasoning, text);
        Assert.Contains("validate(answer=", text);
        Assert.Contains("── Tool Output: validate ──", text);
        Assert.Contains("submit(", text);
        Assert.Contains("status    : success (1/1 samples completed)", text);
        Assert.Single(Directory.GetFiles(_logDir, "*.eval"));
    }

    [Fact]
    public void the_example_is_registered_with_its_python_name_and_no_sandbox()
    {
        var example = Assert.IsType<ReasoningExample>(ExampleRegistry.Default.Find("reasoning"));
        var context = new ExampleContext("/x", null, true, new Dictionary<string, string>(), null, null, TextWriter.Null);

        Assert.Equal(["reasoning"], example.Tasks.Select(task => task.Name));
        Assert.Equal("none", example.Defaults.Sandbox);
        Assert.False(example.Defaults.NeedsDocker);
        Assert.NotNull(example.Defaults.ModelHint);
        Assert.NotEmpty(example.Deviations);
        Assert.Null(example.FakeSandbox(context));
        Assert.Equal("reasoning-scripted", example.CreateFakeModel(context).Name);
        Assert.Equal("reasoning", example.Tasks[0].Build(context).Name);
    }
}

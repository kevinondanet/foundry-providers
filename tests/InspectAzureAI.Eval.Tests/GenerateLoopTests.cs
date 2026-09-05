using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

/// <summary>Port-level behaviour of <c>task_generate</c> (<c>_eval/task/generate.py</c>): the model + tool loop behind the <c>Generate</c> delegate.</summary>
public class GenerateLoopTests
{
    private static readonly ToolDef Add = new(
        "add",
        "Adds two numbers.",
        new ToolParams
        {
            Properties = new Dictionary<string, ToolParam> { ["a"] = ToolParam.Of("integer"), ["b"] = ToolParam.Of("integer") },
            Required = ["a", "b"],
        },
        (args, _) => Task.FromResult<ToolResult>((args["a"]!.GetValue<int>() + args["b"]!.GetValue<int>()).ToString()));

    private static TaskState State(params ToolDef[] tools)
    {
        var state = new TaskState("scripted", 1, 1, "sum", [new ChatMessageUser("sum")]);
        state.Tools.AddRange(tools);
        return state;
    }

    [Fact]
    public async Task loop_executes_tool_calls_and_stops_when_the_model_stops_calling_tools()
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(
            ScriptedTurn.ToolCall("add", new { a = 1, b = 2 }, id: "c1"),
            ScriptedTurn.ToolCall("add", new { a = 3, b = 4 }, id: "c2"),
            ScriptedTurn.Text("The answers are 3 and 7.")));
        var state = State(Add);

        await GenerateLoop.Create(scope.Model)(state);

        Assert.Equal(["user", "assistant", "tool", "assistant", "tool", "assistant"], state.Messages.Select(m => m.Role));
        Assert.Equal(["3", "7"], state.Messages.OfType<ChatMessageTool>().Select(m => m.Text));
        Assert.Equal(["c1", "c2"], state.Messages.OfType<ChatMessageTool>().Select(m => m.ToolCallId));
        Assert.Equal("The answers are 3 and 7.", state.Output.Completion);
        Assert.Equal(3, scope.Api.Requests.Count);
        Assert.Equal(["add"], scope.Api.Requests[0].Tools.Select(t => t.Name));
        Assert.Equal(5, scope.Api.Requests[2].Input.Count);
    }

    [Fact]
    public async Task single_mode_resolves_one_round_of_tool_calls_and_returns()
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(
            ScriptedTurn.ToolCall("add", new { a = 1, b = 2 }),
            ScriptedTurn.Text("never asked")));
        var state = State(Add);

        await GenerateLoop.Create(scope.Model)(state, ToolCallsMode.Single);

        Assert.Equal(["user", "assistant", "tool"], state.Messages.Select(m => m.Role));
        Assert.Single(scope.Api.Requests);
        Assert.Equal(StopReason.ToolCalls, state.Output.StopReason);
    }

    [Fact]
    public async Task none_mode_appends_the_assistant_message_without_executing_tools()
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.ToolCall("add", new { a = 1, b = 2 })));
        var state = State(Add);

        await GenerateLoop.Create(scope.Model)(state, ToolCallsMode.None);

        Assert.Equal(["user", "assistant"], state.Messages.Select(m => m.Role));
        Assert.Single(scope.Api.Requests);
        Assert.Single(Assert.IsType<ChatMessageAssistant>(state.Messages[^1]).ToolCalls!);
    }

    [Fact]
    public async Task a_forced_tool_choice_applies_to_the_first_call_only()
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(
            ScriptedTurn.ToolCall("add", new { a = 1, b = 1 }),
            ScriptedTurn.Text("2")));
        var state = State(Add);
        state.ToolChoice = new ToolFunction("add");

        await GenerateLoop.Create(scope.Model)(state);

        Assert.Equal(new ToolFunction("add"), scope.Api.Requests[0].ToolChoice);
        Assert.Same(ToolChoice.Auto, scope.Api.Requests[1].ToolChoice);
        Assert.Equal(new ToolFunction("add"), state.ToolChoice);
    }

    [Fact]
    public async Task config_is_forwarded_to_the_model()
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("ok")));
        var state = State();

        await GenerateLoop.Create(scope.Model)(state, config: new GenerateConfig { Temperature = 0.25 });

        Assert.Equal(0.25, Assert.Single(scope.Api.Requests).Config.Temperature);
    }

    [Fact]
    public async Task a_tool_that_completes_the_state_ends_the_loop()
    {
        TaskState? state = null;
        var finish = new ToolDef("finish", "Finishes.", new ToolParams(), (_, _) =>
        {
            state!.Completed = true;
            return Task.FromResult<ToolResult>("finished");
        });
        using var scope = new SampleContextScope(new ScriptedModelApi(
            ScriptedTurn.ToolCall("finish", new JsonObject()),
            ScriptedTurn.Text("never asked")));
        state = State(finish);

        await GenerateLoop.Create(scope.Model)(state);

        Assert.Equal(["user", "assistant", "tool"], state.Messages.Select(m => m.Role));
        Assert.Single(scope.Api.Requests);
    }

    [Fact]
    public async Task the_state_message_limit_stops_the_loop_before_the_next_call()
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(
            ScriptedTurn.ToolCall("add", new { a = 1, b = 2 }),
            ScriptedTurn.ToolCall("add", new { a = 3, b = 4 }),
            ScriptedTurn.Text("never asked")));
        var state = State(Add);
        state.MessageLimit = 3;

        var ex = await Assert.ThrowsAsync<LimitExceededException>(() => GenerateLoop.Create(scope.Model)(state));

        Assert.Equal("message", ex.Type);
        Assert.Equal("Message limit reached. count: 3; limit: 3", ex.Message);
        Assert.Equal(["user", "assistant", "tool"], state.Messages.Select(m => m.Role));
        Assert.Single(scope.Api.Requests);
    }

    [Fact]
    public async Task the_state_token_limit_is_checked_after_the_call_without_appending_the_message()
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("ok", new ModelUsage(10, 5, 15))));
        var state = State();
        state.TokenLimit = 10;

        var ex = await Assert.ThrowsAsync<LimitExceededException>(() => GenerateLoop.Create(scope.Model)(state));

        Assert.Equal("token", ex.Type);
        Assert.Equal("Token limit exceeded. value: 15; limit: 10", ex.Message);
        Assert.Equal(["user"], state.Messages.Select(m => m.Role));
        Assert.Equal(15, state.TokenUsage);
    }

    [Fact]
    public async Task the_ambient_limit_exception_propagates_out_of_the_loop()
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("ok")), limits: new Limits { MessageLimit = 1 });
        var state = State();

        var ex = await Assert.ThrowsAsync<LimitExceededException>(() => GenerateLoop.Create(scope.Model)(state));

        Assert.Equal("message", ex.Type);
        Assert.Empty(scope.Api.Requests);
    }
}

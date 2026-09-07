using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Model = InspectAzureAI.Eval.Model.Model;
using Solvers = InspectAzureAI.Eval.Solvers.Solvers;

/// <summary>Port-level behaviour of <c>basic_agent</c> (<c>solver/_basic_agent.py</c>): submit, attempts, continue message and limits.</summary>
public class BasicAgentTests
{
    private static readonly ToolDef Calc = new(
        "calc",
        "Evaluates a sum.",
        new ToolParams { Properties = new Dictionary<string, ToolParam> { ["expr"] = ToolParam.Of("string") }, Required = ["expr"] },
        (args, _) => Task.FromResult<ToolResult>(args["expr"]!.GetValue<string>() == "6*7" ? "42" : "?"));

    /// <summary>A sample context whose scorer marks a "42" completion correct and records every scoring call.</summary>
    private sealed class AgentScope : IDisposable
    {
        private readonly IDisposable _scope;

        public AgentScope(ScriptedModelApi api, Limits? limits = null)
        {
            Api = api;
            Model = new Model(api);
            Context = new SampleContext
            {
                ActiveModel = Model,
                Limits = limits ?? new Limits(),
                Scorer = state =>
                {
                    ScoredCompletions.Add(state.Output.Completion);
                    return Task.FromResult<IReadOnlyList<Score>>([new Score(state.Output.Completion == "42" ? "C" : "I")]);
                },
            };
            _scope = SampleContext.Begin(Context);
        }

        public ScriptedModelApi Api { get; }

        public Model Model { get; }

        public SampleContext Context { get; }

        public List<string> ScoredCompletions { get; } = [];

        public void Dispose() => _scope.Dispose();
    }

    private static TaskState State() => new("scripted", 1, 1, "What is 6 times 7?", [new ChatMessageUser("What is 6 times 7?")]);

    private static Task<TaskState> Run(Solver solver, AgentScope scope, TaskState? state = null) =>
        solver(state ?? State(), GenerateLoop.Create(scope.Model), CancellationToken.None);

    [Fact]
    public async Task the_default_system_message_and_submit_tool_are_installed()
    {
        using var scope = new AgentScope(new ScriptedModelApi(ScriptedTurn.ToolCall("submit", new { answer = "42" })));

        var state = await Run(Solvers.BasicAgent(tools: [Calc]), scope);

        var system = Assert.IsType<ChatMessageSystem>(state.Messages[0]);
        Assert.StartsWith("You are a helpful assistant attempting to submit the correct answer.", system.Text);
        Assert.EndsWith("call the submit() function to report it.", system.Text);
        Assert.Equal(["calc", "submit"], state.Tools.Select(t => t.Name));
        var submit = state.Tools[^1];
        Assert.Equal("Submit an answer for evaluation.", submit.Description);
        Assert.Equal(["answer"], submit.Parameters.Required);
        Assert.Equal(0, submit.MaxOutput);
        Assert.Equal(["calc", "submit"], scope.Api.Requests[0].Tools.Select(t => t.Name));
    }

    [Fact]
    public async Task submitting_on_the_first_try_sets_the_completion_and_completes_the_state()
    {
        using var scope = new AgentScope(new ScriptedModelApi(
            ScriptedTurn.ToolCall("calc", new { expr = "6*7" }, text: "Let me compute."),
            ScriptedTurn.ToolCall("submit", new { answer = "42" }, text: "The answer is 42.")));

        var state = await Run(Solvers.BasicAgent(tools: [Calc]), scope);

        Assert.Equal("42", state.Output.Completion);
        Assert.Equal("The answer is 42.", state.Output.Message.Text);
        Assert.True(state.Completed);
        Assert.Equal(["system", "user", "assistant", "tool", "assistant", "tool"], state.Messages.Select(m => m.Role));
        Assert.Equal("42", Assert.IsType<ChatMessageTool>(state.Messages[^1]).Text);
        Assert.Equal(2, scope.Api.Requests.Count);
        Assert.Empty(scope.ScoredCompletions);
    }

    [Fact]
    public async Task an_incorrect_submission_is_scored_and_the_model_is_told_to_try_again()
    {
        using var scope = new AgentScope(new ScriptedModelApi(
            ScriptedTurn.ToolCall("submit", new { answer = "41" }),
            ScriptedTurn.ToolCall("submit", new { answer = "42" })));

        var state = await Run(Solvers.BasicAgent(tools: [Calc], maxAttempts: 3), scope);

        Assert.Equal("42", state.Output.Completion);
        Assert.True(state.Completed);
        Assert.Equal(["41", "42"], scope.ScoredCompletions);
        Assert.Equal(["system", "user", "assistant", "tool", "user", "assistant", "tool"], state.Messages.Select(m => m.Role));
        Assert.Equal(Solvers.BasicAgentIncorrectMessage, state.Messages[4].Text);
        Assert.Equal(2, scope.Api.Requests.Count);
    }

    [Fact]
    public async Task the_last_allowed_attempt_is_accepted_without_scoring()
    {
        using var scope = new AgentScope(new ScriptedModelApi(
            ScriptedTurn.ToolCall("submit", new { answer = "40" }),
            ScriptedTurn.ToolCall("submit", new { answer = "41" }),
            ScriptedTurn.Text("never asked")));

        var state = await Run(Solvers.BasicAgent(maxAttempts: 2, incorrectMessage: "Nope."), scope);

        Assert.Equal("41", state.Output.Completion);
        Assert.Equal(["40"], scope.ScoredCompletions);
        Assert.Equal("Nope.", state.Messages[4].Text);
        Assert.Equal(2, scope.Api.Requests.Count);
    }

    [Fact]
    public async Task a_custom_score_value_decides_correctness()
    {
        using var scope = new AgentScope(new ScriptedModelApi(
            ScriptedTurn.ToolCall("submit", new { answer = "41" }),
            ScriptedTurn.ToolCall("submit", new { answer = "42" })));

        var state = await Run(Solvers.BasicAgent(maxAttempts: 5, scoreValue: _ => 1.0), scope);

        Assert.Equal("41", state.Output.Completion);
        Assert.Single(scope.Api.Requests);
    }

    [Fact]
    public async Task a_reply_without_tool_calls_gets_the_continue_message()
    {
        using var scope = new AgentScope(new ScriptedModelApi(
            ScriptedTurn.Text("I think it is 42."),
            ScriptedTurn.ToolCall("submit", new { answer = "42" })));

        var state = await Run(Solvers.BasicAgent(), scope);

        Assert.Equal(["system", "user", "assistant", "user", "assistant", "tool"], state.Messages.Select(m => m.Role));
        Assert.Equal(Solvers.BasicAgentContinueMessage, state.Messages[3].Text);
        Assert.Equal("42", state.Output.Completion);
    }

    [Fact]
    public async Task the_message_limit_ends_the_loop_with_a_limit_exception_for_the_runner()
    {
        using var scope = new AgentScope(new ScriptedModelApi(
            ScriptedTurn.Text("thinking"),
            ScriptedTurn.Text("still thinking"),
            ScriptedTurn.Text("never asked")));
        var state = State();

        var ex = await Assert.ThrowsAsync<LimitExceededException>(() => Run(Solvers.BasicAgent(messageLimit: 6), scope, state));

        Assert.Equal("message", ex.Type);
        Assert.Equal(6, state.MessageLimit);
        Assert.Equal(6, state.Messages.Count);
        Assert.Equal(2, scope.Api.Requests.Count);
        Assert.Equal("still thinking", state.Output.Completion);
        Assert.False(state.Completed);
    }

    [Fact]
    public async Task a_default_message_limit_of_50_applies_when_no_limit_is_set()
    {
        using var scope = new AgentScope(new ScriptedModelApi(ScriptedTurn.ToolCall("submit", new { answer = "42" })));

        var state = await Run(Solvers.BasicAgent(), scope);

        Assert.Equal(50, state.MessageLimit);
    }

    [Fact]
    public async Task the_task_message_limit_is_kept_and_a_token_limit_suppresses_the_default()
    {
        using var scope = new AgentScope(new ScriptedModelApi(
            ScriptedTurn.ToolCall("submit", new { answer = "42" }),
            ScriptedTurn.ToolCall("submit", new { answer = "42" })));
        var fromTask = State();
        fromTask.MessageLimit = 20;
        var withTokens = State();

        await Run(Solvers.BasicAgent(), scope, fromTask);
        await Run(Solvers.BasicAgent(tokenLimit: 1000), scope, withTokens);

        Assert.Equal(20, fromTask.MessageLimit);
        Assert.Null(withTokens.MessageLimit);
    }

    [Fact]
    public async Task the_agent_token_limit_counts_tokens_used_by_the_loop()
    {
        using var scope = new AgentScope(new ScriptedModelApi(
            ScriptedTurn.Text("one", new ModelUsage(4, 4, 8)),
            ScriptedTurn.Text("two", new ModelUsage(4, 4, 8)),
            ScriptedTurn.Text("never asked")));

        var ex = await Assert.ThrowsAsync<LimitExceededException>(() => Run(Solvers.BasicAgent(tokenLimit: 10), scope));

        Assert.Equal("token", ex.Type);
        Assert.Equal("10", ex.LimitStr);
        Assert.Equal(16, ex.Value);
        Assert.Equal(2, scope.Api.Requests.Count);
    }

    [Fact]
    public async Task a_context_window_overflow_terminates_the_agent_with_an_info_event()
    {
        var overflow = ModelOutput.FromContent(ScriptedModelApi.DefaultModelName, "too long", StopReason.ModelLength);
        using var scope = new AgentScope(new ScriptedModelApi(ScriptedTurn.From(overflow), ScriptedTurn.Text("never asked")));

        var state = await Run(Solvers.BasicAgent(), scope);

        Assert.Single(scope.Api.Requests);
        Assert.Equal("too long", state.Output.Completion);
        Assert.False(state.Completed);
        var info = Assert.Single(scope.Context.Transcript.Events.OfType<InfoEvent>());
        Assert.Equal("Agent terminated: model context window exceeded", info.Data!.GetValue<string>());
    }

    [Fact]
    public async Task a_custom_init_and_submit_name_are_honoured()
    {
        using var scope = new AgentScope(new ScriptedModelApi(ScriptedTurn.ToolCall("answer_now", new { answer = "42" })));

        var state = await Run(Solvers.BasicAgent(init: Solvers.SystemMessage("Be brief."), submitName: "answer_now", submitDescription: "Answer."), scope);

        Assert.Equal("Be brief.", state.Messages[0].Text);
        Assert.Equal(["answer_now"], state.Tools.Select(t => t.Name));
        Assert.Equal("42", state.Output.Completion);
    }

    [Fact]
    public async Task scoring_without_a_task_scorer_is_an_error()
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.ToolCall("submit", new { answer = "42" })));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Solvers.BasicAgent(maxAttempts: 2)(State(), GenerateLoop.Create(scope.Model), CancellationToken.None));

        Assert.Equal("The score() function can only be called while executing a task with a scorer.", ex.Message);
    }

    [Theory]
    [InlineData("C", 1.0)]
    [InlineData("P", 0.5)]
    [InlineData("I", 0.0)]
    [InlineData("N", 0.0)]
    [InlineData("yes", 1.0)]
    [InlineData("FALSE", 0.0)]
    [InlineData("0.75", 0.75)]
    [InlineData("maybe", 0.0)]
    public void the_default_score_value_matches_value_to_float(string value, double expected)
    {
        Assert.Equal(expected, Solvers.DefaultScoreValue(value));
        Assert.Equal(1.0, Solvers.DefaultScoreValue(true));
        Assert.Equal(0.25, Solvers.DefaultScoreValue(0.25));
    }
}

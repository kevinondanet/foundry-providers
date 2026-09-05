using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Agents = InspectAzureAI.Eval.Agents.Agents;
using Model = InspectAzureAI.Eval.Model.Model;
using Solvers = InspectAzureAI.Eval.Solvers.Solvers;

/// <summary>Port of <c>tests/agent/test_agent_react.py</c>: prompt assembly, the submit tool, attempts, on_continue, truncation, refusals.</summary>
public class ReactAgentTests
{
    private const string Question = "What is 1 + 1?";

    private static readonly string[] Targets = ["2", "2.0", "Two"];

    private static readonly ToolDef Addition = new(
        "addition",
        "Add two numbers.",
        new ToolParams
        {
            Properties = new Dictionary<string, ToolParam> { ["x"] = ToolParam.Of("integer", "First number to add."), ["y"] = ToolParam.Of("integer", "Second number to add.") },
            Required = ["x", "y"],
        },
        (args, _) => Task.FromResult<ToolResult>((args["x"]!.GetValue<int>() + args["y"]!.GetValue<int>()).ToString()));

    private static readonly AgentDef Searcher = new("searcher", "Searcher that computes max searches.", (state, _) =>
    {
        state.Messages.Add(new ChatMessageUser("The maximum searches is 5"));
        return Task.FromResult(state);
    });

    /// <summary>Port of the <c>includes()</c> scorer over the sample targets.</summary>
    private static Score Includes(TaskState state) =>
        new(state.Target.Values.Any(t => state.Output.Completion.Contains(t, StringComparison.OrdinalIgnoreCase)) ? "C" : "I");

    /// <summary>Port of the test file's <c>compare_quantities()</c> scorer.</summary>
    private static Score CompareQuantities(TaskState state)
    {
        var match = Regex.Match(state.Output.Completion, @".*?(\d+)$", RegexOptions.Singleline);
        Assert.True(match.Success, $"no trailing number in '{state.Output.Completion}'");
        var answer = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var target = double.Parse(state.Target.Text, System.Globalization.CultureInfo.InvariantCulture);
        return answer == target
            ? new Score(1.0) { Answer = state.Output.Completion }
            : new Score(0.0) { Answer = state.Output.Completion, Explanation = answer > target ? "Answer is too high" : "Answer is too low" };
    }

    /// <summary>A sample context around a scripted model, with a sample state and scorer like a running task.</summary>
    private sealed class AgentScope : IDisposable
    {
        private readonly IDisposable _scope;

        public AgentScope(ScriptedModelApi api, Func<TaskState, Score>? scorer = null, int? messageLimit = 30, string question = Question, string[]? targets = null)
        {
            Api = api;
            Model = new Model(api);
            SampleState = new TaskState("scripted", 1, 1, question, [new ChatMessageUser(question) { Source = "input" }], new Target(targets ?? Targets));
            Context = new SampleContext
            {
                ActiveModel = Model,
                Limits = new Limits { MessageLimit = messageLimit },
                SampleState = SampleState,
                Scorer = scorer is null ? null : state =>
                {
                    var score = scorer(state);
                    Scores.Add(score);
                    return Task.FromResult<IReadOnlyList<Score>>([score]);
                },
            };
            _scope = SampleContext.Begin(Context);
        }

        public ScriptedModelApi Api { get; }

        public Model Model { get; }

        public TaskState SampleState { get; }

        public SampleContext Context { get; }

        public List<Score> Scores { get; } = [];

        /// <summary>Scores the final state the way the runner would after the solver.</summary>
        public Score FinalScore(AgentState state, Func<TaskState, Score> scorer) => scorer(SampleState.WithMessages(state.Messages, state.Output));

        public void Dispose() => _scope.Dispose();
    }

    private static Task<AgentState> RunAsync(AgentDef agent, string question = Question) =>
        agent.Execute(new AgentState([new ChatMessageUser(question) { Source = "input" }]), CancellationToken.None);

    private static ScriptedTurn Submit(string answer, string tool = "submit", string text = "") => ScriptedTurn.ToolCall(tool, new { answer }, text: text);

    private static ScriptedTurn Refusal(string text) => ScriptedTurn.From(ModelOutput.FromContent(ScriptedModelApi.DefaultModelName, text, StopReason.ContentFilter));

    private static ChatMessageSystem? SystemMessage(AgentState state) => state.Messages.OfType<ChatMessageSystem>().FirstOrDefault();

    // ---- prompt assembly -------------------------------------------------------------------------------------

    [Fact]
    public async Task no_prompt_adds_no_system_message()
    {
        using var scope = new AgentScope(new ScriptedModelApi(Submit("2")));

        var state = await RunAsync(Agents.React(prompt: AgentPrompt.None, tools: [Addition]));

        Assert.Null(SystemMessage(state));
        Assert.Equal("user", state.Messages[0].Role);
    }

    [Fact]
    public async Task instructions_are_combined_with_the_default_assistant_prompt()
    {
        using var scope = new AgentScope(new ScriptedModelApi(Submit("2")));

        var state = await RunAsync(Agents.React(prompt: "You are a ninja", tools: [Addition]));

        var system = Assert.IsType<ChatMessageSystem>(state.Messages[0]);
        Assert.Contains("ninja", system.Text);
        Assert.Contains("best possible answer", system.Text);
        Assert.Contains("call the submit() tool to report it.", system.Text);
        Assert.StartsWith("You are a ninja\n\n", system.Text);
    }

    [Fact]
    public async Task a_custom_assistant_prompt_replaces_the_default()
    {
        using var scope = new AgentScope(new ScriptedModelApi(Submit("2")));

        var state = await RunAsync(Agents.React(prompt: new AgentPrompt(AssistantPrompt: "Try to do your best"), tools: [Addition]));

        var system = SystemMessage(state)!;
        Assert.Contains("do your best", system.Text);
        Assert.DoesNotContain("best possible answer", system.Text);
    }

    [Fact]
    public async Task the_handoff_prompt_is_included_only_when_a_handoff_tool_is_present()
    {
        var prompt = new AgentPrompt(HandoffPrompt: "Make the handoff right!");
        using (var scope = new AgentScope(new ScriptedModelApi(Submit("2"))))
        {
            var state = await RunAsync(Agents.React(prompt: prompt, tools: [Agents.Handoff(Searcher)]));
            Assert.Contains("handoff right!", SystemMessage(state)!.Text);
            Assert.Contains("best possible answer", SystemMessage(state)!.Text);
        }

        using (var scope = new AgentScope(new ScriptedModelApi(Submit("2"))))
        {
            var state = await RunAsync(Agents.React(prompt: prompt, tools: [Addition]));
            Assert.DoesNotContain("handoff right!", SystemMessage(state)!.Text);
        }
    }

    [Fact]
    public async Task the_default_prompt_encourages_parallel_tool_calls()
    {
        using var scope = new AgentScope(new ScriptedModelApi(Submit("2")));

        var state = await RunAsync(Agents.React(tools: [Addition]));

        var system = SystemMessage(state)!.Text;
        Assert.Contains(AgentPrompt.ParallelToolsPrompt, system);
        Assert.DoesNotContain("send more messages with additional tool calls", system);
    }

    [Fact]
    public async Task an_assistant_prompt_with_literal_braces_only_substitutes_the_submit_placeholder()
    {
        var submit = new AgentSubmit { Name = "agent_submit", Description = "Submit an answer." };
        using (var scope = new AgentScope(new ScriptedModelApi(Submit("2", "agent_submit"))))
        {
            var state = await RunAsync(Agents.React(prompt: new AgentPrompt(AssistantPrompt: "You are helpful. Output {\"json\": true}. Use {submit} to finish."), tools: [Addition], submit: submit));
            var system = SystemMessage(state)!.Text;
            Assert.Contains("{\"json\": true}", system);
            Assert.Contains("Use agent_submit to finish.", system);
            Assert.DoesNotContain("call the agent_submit() tool", system);
        }

        using (var scope = new AgentScope(new ScriptedModelApi(Submit("2", "agent_submit"))))
        {
            var state = await RunAsync(Agents.React(
                prompt: new AgentPrompt(AssistantPrompt: "Respond with {\"done\": false} until ready.", SubmitPrompt: "When ready call {submit}() with {\"done\": true}."),
                tools: [Addition],
                submit: submit));
            var system = SystemMessage(state)!.Text;
            Assert.Contains("{\"done\": false}", system);
            Assert.Contains("call agent_submit() with {\"done\": true}.", system);
            Assert.Equal("Respond with {\"done\": false} until ready.\nWhen ready call agent_submit() with {\"done\": true}.", system);
        }
    }

    [Fact]
    public void prompt_to_system_message_matches_python_for_every_piece()
    {
        Assert.Null(Agents.PromptToSystemMessage(null, [], "submit"));
        Assert.Null(Agents.PromptToSystemMessage(AgentPrompt.None, [], "submit"));
        Assert.Equal("", Agents.PromptToSystemMessage(new AgentPrompt(null, null, null, null), [], "submit")!.Text);
        var full = Agents.PromptToSystemMessage(new AgentPrompt("Do it.", "Hand off.", "Assist.", "Use {submit}."), [Agents.Handoff(Searcher)], "finish")!.Text;
        Assert.Equal("Do it.\n\nHand off.\n\nAssist.\nUse finish.", full);
        Assert.Equal("Do it.\n\nAssist.\nUse finish.", Agents.PromptToSystemMessage(new AgentPrompt("Do it.", "Hand off.", "Assist.", "Use {submit}."), [Addition], "finish")!.Text);
        Assert.Equal("Assist with submit.", Agents.PromptToSystemMessage(new AgentPrompt(AssistantPrompt: "Assist with {submit}."), [], null)!.Text);
    }

    // ---- the submit tool -------------------------------------------------------------------------------------

    [Fact]
    public async Task the_submit_tool_is_appended_after_the_tools_and_seen_by_the_model()
    {
        using var scope = new AgentScope(new ScriptedModelApi(Submit("2", "agent_submit")));

        var state = await RunAsync(Agents.React(tools: [Addition], submit: new AgentSubmit { Name = "agent_submit", Description = "Submit an answer." }));

        var tools = scope.Api.Requests[0].Tools;
        Assert.Equal(["addition", "agent_submit"], tools.Select(t => t.Name));
        Assert.Equal("Submit an answer.", tools[1].Description);
        Assert.Equal(["answer"], tools[1].Parameters.Required);
        Assert.EndsWith("2", state.Output.Completion);
    }

    [Fact]
    public async Task a_custom_submit_tool_keeps_its_own_name_unless_overridden()
    {
        var custom = new ToolDef(
            "custom_submit",
            "The tool used to submit.",
            new ToolParams { Properties = new Dictionary<string, ToolParam> { ["answer"] = ToolParam.Of("string", "The submitted answer.") }, Required = ["answer"] },
            (args, _) => Task.FromResult<ToolResult>(args["answer"]!.GetValue<string>()));

        using (var scope = new AgentScope(new ScriptedModelApi(Submit("2", "custom_submit"))))
        {
            var state = await RunAsync(Agents.React(tools: [Addition], submit: new AgentSubmit { Tool = custom }));
            var tool = scope.Api.Requests[0].Tools[1];
            Assert.Equal("custom_submit", tool.Name);
            Assert.Equal("The tool used to submit.", tool.Description);
            Assert.EndsWith("2", state.Output.Completion);
        }

        using (var scope = new AgentScope(new ScriptedModelApi(Submit("2", "submit_it"))))
        {
            await RunAsync(Agents.React(tools: [Addition], submit: new AgentSubmit { Tool = custom, Name = "submit_it", Description = "tool to submit it" }));
            var tool = scope.Api.Requests[0].Tools[1];
            Assert.Equal("submit_it", tool.Name);
            Assert.Equal("tool to submit it", tool.Description);
        }
    }

    [Fact]
    public async Task a_long_submission_is_not_truncated_unless_the_submit_tool_asks_for_it()
    {
        var answer = new string('A', ToolExecutor.DefaultMaxOutput + 1000);
        using (var scope = new AgentScope(new ScriptedModelApi(Submit(answer))))
        {
            var state = await RunAsync(Agents.React());
            Assert.EndsWith(answer, state.Output.Completion);
        }

        var custom = new ToolDef(
            "submit",
            "The tool used to submit.",
            new ToolParams { Properties = new Dictionary<string, ToolParam> { ["answer"] = ToolParam.Of("string") }, Required = ["answer"] },
            (args, _) => Task.FromResult<ToolResult>(args["answer"]!.GetValue<string>()));

        using (var scope = new AgentScope(new ScriptedModelApi(Submit(answer))))
        {
            var state = await RunAsync(Agents.React(submit: new AgentSubmit { Tool = custom }));
            Assert.EndsWith(answer, state.Output.Completion);
        }

        using (var scope = new AgentScope(new ScriptedModelApi(Submit(answer))))
        {
            var state = await RunAsync(Agents.React(submit: new AgentSubmit { Tool = custom with { MaxOutput = 500 } }));
            Assert.DoesNotContain(answer, state.Output.Completion);
            Assert.True(state.Output.Completion.Length < answer.Length);
            Assert.Contains("was too long to be displayed", state.Output.Completion);
        }
    }

    [Fact]
    public async Task the_answer_is_appended_to_the_model_content_and_the_submit_call_removed()
    {
        using (var scope = new AgentScope(new ScriptedModelApi(Submit("2", text: "The answer is"))))
        {
            var state = await RunAsync(Agents.React(tools: [Addition]));
            Assert.Equal("The answer is\n\n2", state.Output.Completion);
            var final = Assert.IsType<ChatMessageAssistant>(state.Messages[^1]);
            Assert.Equal("The answer is\n\n2", final.Text);
            Assert.Empty(final.ToolCalls!);
            Assert.DoesNotContain(state.Messages, m => m is ChatMessageTool);
            Assert.Equal(["system", "user", "assistant"], state.Messages.Select(m => m.Role));
        }

        using (var scope = new AgentScope(new ScriptedModelApi(Submit("2", text: "The answer is"))))
        {
            var state = await RunAsync(Agents.React(tools: [Addition], submit: new AgentSubmit { AnswerOnly = true, AnswerDelimiter = " -> " }));
            Assert.Equal("2", state.Output.Completion);
            Assert.Equal("The answer is -> 2", state.Messages[^1].Text);
        }

        using (var scope = new AgentScope(new ScriptedModelApi(Submit("2", text: "The answer is"))))
        {
            var state = await RunAsync(Agents.React(tools: [Addition], submit: new AgentSubmit { KeepInMessages = true }));
            Assert.Equal("The answer is\n\n2", state.Output.Completion);
            Assert.Equal(["system", "user", "assistant", "tool"], state.Messages.Select(m => m.Role));
            var call = Assert.IsType<ChatMessageAssistant>(state.Messages[2]);
            Assert.Equal("The answer is", call.Text);
            Assert.Equal("submit", Assert.Single(call.ToolCalls!).Function);
        }
    }

    [Fact]
    public async Task a_submit_call_that_errors_is_not_a_submission()
    {
        using var scope = new AgentScope(new ScriptedModelApi(
            ScriptedTurn.ToolCall("submit", new { wrong = "2" }),
            Submit("2")));

        var state = await RunAsync(Agents.React(tools: [Addition]));

        Assert.Equal(2, scope.Api.Requests.Count);
        Assert.EndsWith("2", state.Output.Completion);
    }

    // ---- no submit tool --------------------------------------------------------------------------------------

    [Fact]
    public async Task without_a_submit_tool_the_agent_stops_when_the_model_stops_calling_tools()
    {
        using var scope = new AgentScope(new ScriptedModelApi(
            ScriptedTurn.ToolCall("addition", new { x = 1, y = 1 }),
            ScriptedTurn.Text("2")), Includes);

        var state = await RunAsync(Agents.React(tools: [Addition], submit: AgentSubmit.Disabled));

        Assert.Equal("2", state.Output.Completion);
        Assert.Equal(["addition"], scope.Api.Requests[0].Tools.Select(t => t.Name));
        Assert.Equal(["system", "user", "assistant", "tool", "assistant"], state.Messages.Select(m => m.Role));
        Assert.Equal("C", scope.FinalScore(state, Includes).Value.Text);
        Assert.DoesNotContain("call the submit() tool", SystemMessage(state)!.Text);
        Assert.DoesNotContain("`submit()`", SystemMessage(state)!.Text);
    }

    [Fact]
    public void a_string_on_continue_without_a_submit_tool_is_rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => Agents.React(submit: AgentSubmit.Disabled, onContinue: "Keep going"));
        Assert.Contains("no submit tool", ex.Message);
    }

    [Fact]
    public async Task without_a_submit_tool_the_on_continue_function_drives_the_loop()
    {
        using var scope = new AgentScope(new ScriptedModelApi(ScriptedTurn.Text("first"), ScriptedTurn.Text("second"), ScriptedTurn.Text("third")));
        var calls = 0;

        var state = await RunAsync(Agents.React(submit: AgentSubmit.Disabled, onContinueFn: (s, _) => Task.FromResult<AgentContinueResult>(++calls switch
        {
            1 => true,
            2 => "Once more {submit}.",
            _ => false,
        })));

        Assert.Equal(3, scope.Api.Requests.Count);
        Assert.Equal(AgentPrompt.DefaultContinuePromptNoSubmit, state.Messages[3].Text);
        Assert.Equal("Once more {submit}.", state.Messages[5].Text);
        Assert.Equal("third", state.Messages[^1].Text);
    }

    // ---- attempts --------------------------------------------------------------------------------------------

    [Fact]
    public async Task an_incorrect_answer_with_a_single_attempt_is_accepted_without_scoring()
    {
        using var scope = new AgentScope(new ScriptedModelApi(Submit("5")), Includes);

        var state = await RunAsync(Agents.React(tools: [Addition]));

        Assert.EndsWith("5", state.Output.Completion);
        Assert.Empty(scope.Scores);
        Assert.Single(scope.Api.Requests);
        Assert.Equal("I", scope.FinalScore(state, Includes).Value.Text);
    }

    [Fact]
    public async Task the_agent_retries_until_a_submission_scores_correct()
    {
        using var scope = new AgentScope(new ScriptedModelApi(Submit("5"), Submit("4"), Submit("2")), Includes);

        var state = await RunAsync(Agents.React(tools: [Addition], attempts: new AgentAttempts(3)));

        Assert.Equal(3, scope.Api.Requests.Count);
        Assert.EndsWith("2", state.Output.Completion);
        Assert.Equal(["I", "I"], scope.Scores.Select(s => s.Value.Text));
        Assert.Equal([Question, new AgentAttempts().IncorrectMessage, new AgentAttempts().IncorrectMessage], state.Messages.OfType<ChatMessageUser>().Select(m => m.Text));
        Assert.Equal("C", scope.FinalScore(state, Includes).Value.Text);
    }

    [Fact]
    public async Task the_last_allowed_attempt_is_not_scored()
    {
        using var scope = new AgentScope(new ScriptedModelApi(Submit("5"), Submit("4"), ScriptedTurn.Text("never asked")), Includes);

        var state = await RunAsync(Agents.React(tools: [Addition], attempts: new AgentAttempts(2, "Nope.")));

        Assert.Equal(2, scope.Api.Requests.Count);
        Assert.EndsWith("4", state.Output.Completion);
        Assert.Single(scope.Scores);
        Assert.Equal("Nope.", state.Messages.OfType<ChatMessageUser>().Last().Text);
    }

    [Fact]
    public async Task a_custom_score_value_decides_correctness()
    {
        using var scope = new AgentScope(new ScriptedModelApi(Submit("5"), Submit("2")), Includes);

        var state = await RunAsync(Agents.React(attempts: new AgentAttempts(5, ScoreValue: _ => 1.0)));

        Assert.EndsWith("5", state.Output.Completion);
        Assert.Single(scope.Api.Requests);
    }

    [Fact]
    public async Task an_incorrect_message_function_sees_the_state_and_scores()
    {
        using var scope = new AgentScope(new ScriptedModelApi(Submit("5"), Submit("1"), Submit("2")), CompareQuantities, targets: ["2"]);
        var attempts = new AgentAttempts(3)
        {
            IncorrectMessageFn = (state, scores, _) => Task.FromResult($"Your response to the input was incorrect: {scores[0].Explanation}"),
        };

        var state = await RunAsync(Agents.React(tools: [Addition], attempts: attempts));

        Assert.Equal(
            [Question, "Your response to the input was incorrect: Answer is too high", "Your response to the input was incorrect: Answer is too low"],
            state.Messages.OfType<ChatMessageUser>().Select(m => m.Text));
        Assert.Equal(1.0, scope.FinalScore(state, CompareQuantities).AsFloat());
    }

    [Fact]
    public async Task scoring_an_attempt_requires_a_scorer()
    {
        using var scope = new AgentScope(new ScriptedModelApi(Submit("5"), Submit("2")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(Agents.React(attempts: new AgentAttempts(2))));

        Assert.Contains("with a scorer", ex.Message);
    }

    [Fact]
    public async Task score_outside_a_task_is_an_error()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Agents.ScoreAsync(new AgentState([]), CancellationToken.None));
        Assert.Contains("executing a task", ex.Message);
    }

    // ---- on_continue -----------------------------------------------------------------------------------------

    [Fact]
    public async Task an_on_continue_string_is_played_back_when_the_model_makes_no_tool_call()
    {
        const string onContinue = "Please keep going!";
        using var scope = new AgentScope(new ScriptedModelApi(
            ScriptedTurn.ToolCall("addition", new { x = 1, y = 1 }),
            ScriptedTurn.Text("I give up!"),
            Submit("2")), CompareQuantities, targets: ["2"]);

        var state = await RunAsync(Agents.React(tools: [Addition], onContinue: onContinue));

        Assert.Equal(7, state.Messages.Count);
        Assert.Equal(onContinue, state.Messages[^2].Text);
        Assert.Equal(["system", "user", "assistant", "tool", "assistant", "user", "assistant"], state.Messages.Select(m => m.Role));
    }

    [Fact]
    public async Task the_default_continue_prompt_names_the_submit_tool()
    {
        using var scope = new AgentScope(new ScriptedModelApi(
            ScriptedTurn.ToolCall("addition", new { x = 1, y = 1 }),
            ScriptedTurn.Text("I give up!"),
            Submit("2")), CompareQuantities, targets: ["2"]);

        var state = await RunAsync(Agents.React(tools: [Addition]));

        Assert.Equal(7, state.Messages.Count);
        Assert.Equal(AgentPrompt.DefaultContinuePrompt.Replace("{submit}", "submit"), state.Messages[^2].Text);
        Assert.Contains("call the `submit()` tool", state.Messages[^2].Text);
    }

    [Fact]
    public async Task an_on_continue_string_with_literal_braces_is_passed_through_verbatim()
    {
        using var scope = new AgentScope(new ScriptedModelApi(ScriptedTurn.Text("I think it is 2."), Submit("2")), CompareQuantities, targets: ["2"]);

        var state = await RunAsync(Agents.React(tools: [Addition], onContinue: "Call a tool, e.g. {\"name\": \"{submit}\", \"answer\": \"...\"}"));

        Assert.Equal("Call a tool, e.g. {\"name\": \"submit\", \"answer\": \"...\"}", state.Messages[^2].Text);
    }

    [Fact]
    public async Task an_on_continue_function_may_echo_model_output_with_braces()
    {
        using var scope = new AgentScope(new ScriptedModelApi(ScriptedTurn.Text("I refuse. {pwned}"), Submit("2")), CompareQuantities, targets: ["2"]);

        var state = await RunAsync(Agents.React(tools: [Addition], onContinueFn: (s, _) => Task.FromResult<AgentContinueResult>(
            s.Output.Message.ToolCalls is { Count: > 0 } ? true : $"You said \"{s.Output.Completion}\". Please call {{submit}}.")));

        Assert.Equal("You said \"I refuse. {pwned}\". Please call submit.", state.Messages[^2].Text);
    }

    [Fact]
    public async Task an_on_continue_function_decides_each_turn()
    {
        using var scope = new AgentScope(new ScriptedModelApi(ScriptedTurn.Text("5"), ScriptedTurn.Text("1"), ScriptedTurn.Text("2")), CompareQuantities, targets: ["2"]);

        var state = await RunAsync(Agents.React(tools: [Addition], attempts: new AgentAttempts(3), onContinueFn: (s, _) => Task.FromResult<AgentContinueResult>(s.Output.Completion switch
        {
            "5" => "You should definitely continue!",
            "1" => false,
            _ => true,
        })));

        Assert.Contains(state.Messages, m => m.Text == "You should definitely continue!");
        Assert.Equal("1", state.Messages[^1].Text);
        Assert.Equal(2, scope.Api.Requests.Count);
    }

    [Fact]
    public async Task an_on_continue_function_can_replace_the_state()
    {
        using var scope = new AgentScope(new ScriptedModelApi(ScriptedTurn.Text("draft"), Submit("2")), CompareQuantities, targets: ["2"]);
        var replacement = new AgentState([new ChatMessageUser("Start over: what is 1 + 1?")]);

        var state = await RunAsync(Agents.React(tools: [Addition], onContinueFn: (s, _) => Task.FromResult<AgentContinueResult>(
            s.Output.Completion == "draft" ? replacement : true)));

        Assert.Equal("Start over: what is 1 + 1?", state.Messages[0].Text);
        Assert.Equal("user", scope.Api.Requests[1].Input[0].Role);
        Assert.Single(scope.Api.Requests[1].Input);
        Assert.EndsWith("2", state.Output.Completion);
    }

    [Fact]
    public async Task a_null_on_continue_result_is_an_error()
    {
        using var scope = new AgentScope(new ScriptedModelApi(ScriptedTurn.Text("hmm")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(Agents.React(onContinueFn: (_, _) => Task.FromResult<AgentContinueResult>(null!))));
    }

    [Fact]
    public void on_continue_and_on_continue_fn_are_mutually_exclusive()
    {
        Assert.Throws<ArgumentException>(() => Agents.React(onContinue: "x", onContinueFn: (_, _) => Task.FromResult(AgentContinueResult.Continue)));
        Assert.Throws<ArgumentException>(() => Agents.React(model: new Model(new ScriptedModelApi()), modelAgent: (s, _, _) => Task.FromResult(s)));
    }

    // ---- remove_submit_tool ------------------------------------------------------------------------------------

    [Fact]
    public void remove_submit_tool_filters_submit_calls_and_the_reasoning_that_led_to_them()
    {
        var submitCall = new ToolCall("call_123", "submit", new System.Text.Json.Nodes.JsonObject { ["answer"] = "42" });
        var regularCall = new ToolCall("call_456", "addition", new System.Text.Json.Nodes.JsonObject { ["x"] = 1, ["y"] = 2 });
        var withReasoning = new ChatMessageAssistant(
            new Content[] { new ContentReasoning("I need to think about this problem..."), new ContentText("Let me solve this step by step.") },
            toolCalls: [regularCall, submitCall]);
        var withoutReasoning = new ChatMessageAssistant("Just a regular message", toolCalls: [submitCall]);
        var regularOnly = new ChatMessageAssistant(
            new Content[] { new ContentReasoning("This reasoning should stay"), new ContentText("Regular message") },
            toolCalls: [regularCall]);
        var submitResult = new ChatMessageTool("42", "call_123", "submit");

        var filtered = Agents.RemoveSubmitTool([withReasoning, withoutReasoning, regularOnly, submitResult], "submit");

        Assert.Equal(3, filtered.Count);
        var first = Assert.IsType<ChatMessageAssistant>(filtered[0]);
        Assert.Equal("addition", Assert.Single(first.ToolCalls!).Function);
        Assert.DoesNotContain(first.Content.Items!, c => c is ContentReasoning);
        Assert.Equal("Let me solve this step by step.", Assert.IsType<ContentText>(Assert.Single(first.Content.Items!)).Text);
        Assert.Equal(withReasoning.Id, first.Id);

        var second = Assert.IsType<ChatMessageAssistant>(filtered[1]);
        Assert.Empty(second.ToolCalls!);
        Assert.Equal("Just a regular message", second.Content.Text);

        var third = Assert.IsType<ChatMessageAssistant>(filtered[2]);
        Assert.Same(regularOnly, third);
        Assert.Equal("This reasoning should stay", Assert.IsType<ContentReasoning>(third.Content.Items![0]).Reasoning);
    }

    // ---- truncation ------------------------------------------------------------------------------------------

    private static ScriptedModelApi OverflowScript()
    {
        var turns = Enumerable.Range(0, 99).Select(_ => ScriptedTurn.Text("This is a repeated message")).ToList();
        turns.Add(ScriptedTurn.From(ModelOutput.FromContent(ScriptedModelApi.DefaultModelName, "Final message with stop_reason=model_length", StopReason.ModelLength)));
        turns.Add(ScriptedTurn.ToolCall("submit", new { answer = "Copenhagen" }, text: "tool call for tool submit"));
        return new ScriptedModelApi(turns);
    }

    [Fact]
    public async Task auto_truncation_trims_the_conversation_and_continues()
    {
        using var scope = new AgentScope(OverflowScript(), Includes, messageLimit: null, question: "What is the capital of Denmark?", targets: ["Copenhagen"]);

        var state = await RunAsync(Agents.React(truncation: MessageFilters.TrimMessages), "What is the capital of Denmark?");

        Assert.Equal(142, state.Messages.Count);
        Assert.Equal("tool call for tool submit\n\nCopenhagen", state.Messages[^1].Text);
        Assert.Equal("C", scope.FinalScore(state, Includes).Value.Text);
        Assert.Contains(scope.Context.Transcript.Events, e => e is InfoEvent info && info.Data?.ToString() == "Agent exceeded model context window, truncating messages and continuing.");
        Assert.Equal("system", state.Messages[0].Role);
        Assert.Equal("input", state.Messages[1].Source);
    }

    [Fact]
    public async Task disabled_truncation_terminates_the_agent_on_overflow()
    {
        using var scope = new AgentScope(OverflowScript(), Includes, messageLimit: null, question: "What is the capital of Denmark?", targets: ["Copenhagen"]);

        var state = await RunAsync(Agents.React(), "What is the capital of Denmark?");

        Assert.Equal(201, state.Messages.Count);
        Assert.Equal("Final message with stop_reason=model_length", state.Messages[^1].Text);
        Assert.Equal("I", scope.FinalScore(state, Includes).Value.Text);
        Assert.Contains(scope.Context.Transcript.Events, e => e is InfoEvent info && info.Data?.ToString() == "Agent terminated: model context window exceeded");
        Assert.Equal(100, scope.Api.Requests.Count);
    }

    [Fact]
    public async Task a_custom_truncation_filter_that_does_not_shorten_the_conversation_terminates_the_agent()
    {
        using var scope = new AgentScope(OverflowScript(), messageLimit: null);

        var state = await RunAsync(Agents.React(truncation: MessageFilters.Identity));

        Assert.Equal(200, state.Messages.Count);
        Assert.Equal(100, scope.Api.Requests.Count);
    }

    // ---- refusals --------------------------------------------------------------------------------------------

    [Fact]
    public async Task three_consecutive_refusals_stop_the_loop()
    {
        using var scope = new AgentScope(new ScriptedModelApi(
            Refusal("I cannot help with that."),
            Refusal("I still cannot help."),
            Refusal("I really cannot help."),
            Submit("2")), Includes);

        var state = await RunAsync(Agents.React(tools: [Addition]));

        Assert.Equal(3, scope.Api.Requests.Count);
        Assert.Equal("I really cannot help.", state.Output.Completion);
        Assert.Equal("I", scope.FinalScore(state, Includes).Value.Text);
    }

    [Fact]
    public async Task retry_refusals_retries_within_a_single_generate_and_drops_the_refusal()
    {
        using var scope = new AgentScope(new ScriptedModelApi(Refusal("I cannot help with that."), Submit("2")), Includes);

        var state = await RunAsync(Agents.React(tools: [Addition], retryRefusals: 2));

        Assert.EndsWith("2", state.Output.Completion);
        var assistants = state.Messages.OfType<ChatMessageAssistant>().ToList();
        Assert.Single(assistants);
        Assert.DoesNotContain(assistants, m => m.Text.Contains("cannot help"));
        Assert.Equal(2, scope.Api.Requests.Count);
    }

    [Fact]
    public async Task exhausted_refusal_retries_still_stop_after_three_consecutive_refusals()
    {
        using var scope = new AgentScope(new ScriptedModelApi(Enumerable.Range(0, 9).Select(i => Refusal($"I cannot help ({i})."))), Includes);

        var state = await RunAsync(Agents.React(tools: [Addition], retryRefusals: 2));

        Assert.Equal(9, scope.Api.Requests.Count);
        Assert.Equal(3, state.Messages.OfType<ChatMessageAssistant>().Count());
        Assert.Equal("I cannot help (8).", state.Output.Completion);
    }

    [Fact]
    public async Task retry_refusals_works_without_a_submit_tool()
    {
        using var scope = new AgentScope(new ScriptedModelApi(
            Refusal("I cannot help."),
            ScriptedTurn.ToolCall("addition", new { x = 1, y = 1 }),
            ScriptedTurn.Text("2")), Includes);

        var state = await RunAsync(Agents.React(tools: [Addition], submit: AgentSubmit.Disabled, retryRefusals: 1));

        Assert.Equal("2", state.Output.Completion);
        Assert.Equal("C", scope.FinalScore(state, Includes).Value.Text);
    }

    [Fact]
    public async Task a_transient_refusal_resets_the_counter()
    {
        using var scope = new AgentScope(new ScriptedModelApi(Refusal("I cannot help with that."), Submit("2")), Includes);

        var state = await RunAsync(Agents.React(tools: [Addition]));

        Assert.EndsWith("2", state.Output.Completion);
        Assert.Equal(2, scope.Api.Requests.Count);
        Assert.Equal(2, state.Messages.OfType<ChatMessageAssistant>().Count());
    }

    // ---- model agent, compaction seam, cancellation, limits ----------------------------------------------------

    [Fact]
    public async Task a_model_agent_generates_in_place_of_the_model()
    {
        using var scope = new AgentScope(new ScriptedModelApi(ScriptedTurn.Text("never used")));
        IReadOnlyList<ToolDef>? seenTools = null;

        var state = await RunAsync(Agents.React(tools: [Addition], modelAgent: (s, tools, _) =>
        {
            seenTools = tools;
            var call = new ToolCall("c1", "submit", new System.Text.Json.Nodes.JsonObject { ["answer"] = "2" });
            var message = new ChatMessageAssistant("Done.", toolCalls: [call]);
            s.Output = new ModelOutput { Model = "custom", Choices = [new ChatCompletionChoice(message, StopReason.ToolCalls)] };
            s.Messages.Add(message);
            return Task.FromResult(s);
        }));

        Assert.Empty(scope.Api.Requests);
        Assert.Equal(["addition", "submit"], seenTools!.Select(t => t.Name));
        Assert.Equal("Done.\n\n2", state.Output.Completion);
    }

    [Fact]
    public async Task the_compaction_seam_is_called_before_each_generate_and_forced_on_overflow()
    {
        var turns = new List<ScriptedTurn>
        {
            ScriptedTurn.Text("thinking"),
            ScriptedTurn.From(ModelOutput.FromContent(ScriptedModelApi.DefaultModelName, "overflow", StopReason.ModelLength)),
            Submit("2"),
        };
        using var scope = new AgentScope(new ScriptedModelApi(turns));
        var forces = new List<bool>();
        var recorded = 0;
        IReadOnlyList<ChatMessage>? prefix = null;
        var notice = new ChatMessageUser("[compacted]") { Metadata = new Dictionary<string, object?> { ["summary"] = true } };

        var agent = Agents.React(tools: [Addition], compaction: (p, tools, model) =>
        {
            prefix = p;
            Assert.Equal(["addition", "submit"], tools.Select(t => t.Name));
            Assert.Null(model);
            return new AgentCompaction(
                (messages, force, _) =>
                {
                    forces.Add(force);
                    return Task.FromResult(force ? new CompactedInput([.. messages.Take(2), notice], notice) : new CompactedInput(messages));
                },
                (_, _, _) =>
                {
                    recorded++;
                    return Task.CompletedTask;
                });
        });

        var state = await RunAsync(agent);

        Assert.Equal([false, false, true, false], forces);
        Assert.Equal(3, recorded);
        Assert.Equal(["system", "user"], prefix!.Select(m => m.Role));
        Assert.Contains(state.Messages, m => ReferenceEquals(m, notice));
        Assert.Single(state.Messages, m => ReferenceEquals(m, notice));
        Assert.EndsWith("2", state.Output.Completion);
        Assert.Equal(3, scope.Api.Requests.Count);
    }

    [Fact]
    public async Task a_failing_forced_compaction_falls_back_to_the_truncation_filter()
    {
        using var scope = new AgentScope(OverflowScript(), messageLimit: null);
        Provider.Util.ProviderLogger.Reset();

        var state = await RunAsync(Agents.React(
            truncation: MessageFilters.TrimMessages,
            compaction: (_, _, _) => new AgentCompaction((messages, force, _) => force ? throw new InvalidOperationException("boom") : Task.FromResult(new CompactedInput(messages)))));

        Assert.EndsWith("Copenhagen", state.Output.Completion);
        Assert.Contains(Provider.Util.ProviderLogger.Warnings, w => w.Contains("Forced compaction failed during overflow recovery: boom"));
        Assert.Equal(142, state.Messages.Count);
    }

    [Fact]
    public async Task cancellation_stops_the_loop()
    {
        using var cts = new CancellationTokenSource();
        using var scope = new AgentScope(new ScriptedModelApi(ScriptedTurn.From((_, _) =>
        {
            cts.Cancel();
            return ModelOutput.FromContent(ScriptedModelApi.DefaultModelName, "still going");
        })));

        var agent = Agents.React(tools: [Addition]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.Execute(new AgentState([new ChatMessageUser(Question)]), cts.Token));
        Assert.Single(scope.Api.Requests);
    }

    [Fact]
    public async Task the_sample_message_limit_ends_the_loop()
    {
        using var scope = new AgentScope(new ScriptedModelApi(Enumerable.Range(0, 20).Select(_ => ScriptedTurn.Text("no tools"))), messageLimit: 6);

        var ex = await Assert.ThrowsAsync<LimitExceededException>(() => RunAsync(Agents.React(tools: [Addition])));

        Assert.Equal("message", ex.Type);
        Assert.Equal("6", ex.LimitStr);
        Assert.Null(ex.LimitSource);
    }

    [Fact]
    public async Task react_runs_as_a_solver_and_carries_its_name()
    {
        Assert.Equal("react", Agents.React().Name);
        Assert.Equal("", Agents.React().Description);
        var named = Agents.React(name: "My Researcher", description: "A researcher", prompt: "x");
        Assert.Equal("My Researcher", named.Name);
        Assert.Equal("A researcher", named.Description);
        Assert.Equal("transfer_to_my_researcher", Agents.Handoff(named).Name);
        Assert.Equal("my_researcher", Agents.AsTool(named).Name);

        using var scope = new AgentScope(new ScriptedModelApi(Submit("2")), Includes);
        var state = await Solvers.Chain(Agents.AsSolver(Agents.React(tools: [Addition])))(scope.SampleState, GenerateLoop.Create(scope.Model), CancellationToken.None);
        Assert.EndsWith("2", state.Output.Completion);
        Assert.Contains(scope.Context.Transcript.Events, e => e is SpanBeginEvent { Name: "react", Type: "agent" });
    }
}

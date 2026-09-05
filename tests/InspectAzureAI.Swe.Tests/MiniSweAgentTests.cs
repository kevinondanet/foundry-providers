using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Sandbox.Local;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Swe.MiniSwe;

namespace InspectAzureAI.Swe.Tests;

using Model = InspectAzureAI.Eval.Model.Model;
using MiniSweFactory = InspectAzureAI.Swe.MiniSwe.MiniSwe;

/// <summary>
/// The mini-swe-agent loop (upstream <c>DefaultAgent</c> plus inspect_swe attempts and resume) driven by a
/// scripted model against the local sandbox, i.e. real bash on this machine.
/// </summary>
public class MiniSweAgentTests
{
    private const string Incorrect = "Your submission was incorrect. Please proceed and attempt to find the correct answer.";

    private const string NotExecuted = "{\n  \"returncode\": -1,\n  \"output\": \"\", \"exception_info\": \"action was not executed\"\n}";

    private static ScriptedTurn Bash(string command, string id) => ScriptedTurn.ToolCall("bash", new { command }, id);

    private static ScriptedTurn Submit(string answer, string id) => Bash($"printf '{MiniSweTemplates.SubmitMarker}\\n{answer}\\n'", id);

    private static AgentState State(params ChatMessage[] messages) => new(messages);

    private static Task<AgentState> RunAsync(MiniSweAgentOptions? options = null, AgentState? state = null) =>
        MiniSweFactory.Agent(options).Execute(state ?? State(new ChatMessageUser("Say hello")), CancellationToken.None);

    private static string Guidance(string error) => MiniSweTemplates.RenderFormatError(error, hasToolCalls: true, "tool_calls");

    private static ChatMessageTool Tool(ChatMessage message) => Assert.IsType<ChatMessageTool>(message);

    private static ChatMessageAssistant Assistant(ChatMessage message) => Assert.IsType<ChatMessageAssistant>(message);

    [Fact]
    public async Task a_two_step_run_submits_with_the_expected_message_sequence()
    {
        using var scope = new Scope(new ScriptedModelApi(Bash("ls", "c1"), Submit("my answer", "c2")));
        await File.WriteAllTextAsync(Path.Combine(scope.Sandbox.WorkingDirectory, "marker.txt"), "x");

        var state = await RunAsync();

        Assert.Equal("Submitted", scope.ExitStatus);
        Assert.Equal("my answer\n", scope.Submission);
        Assert.Equal("my answer\n", state.Output.Completion);
        Assert.Collection(
            state.Messages,
            m => Assert.Equal("You are a helpful assistant that can interact with a computer.", Assert.IsType<ChatMessageSystem>(m).Text),
            m =>
            {
                var instance = Assert.IsType<ChatMessageUser>(m).Text;
                Assert.StartsWith("Please solve this issue: Say hello\n", instance);
                Assert.DoesNotContain("{{", instance);
                Assert.Matches("<system_information>\n(Darwin|Linux) ", instance);
            },
            m => Assert.Equal("c1", Assert.Single(Assistant(m).ToolCalls!).Id),
            m =>
            {
                Assert.Equal("c1", Tool(m).ToolCallId);
                Assert.Equal("bash", Tool(m).Function);
                Assert.StartsWith("{\n  \"returncode\": 0,\n  \"output\": \"", Tool(m).Text);
                Assert.Contains("marker.txt", Tool(m).Text);
            },
            m => Assert.Equal("c2", Assert.Single(Assistant(m).ToolCalls!).Id),
            m =>
            {
                Assert.Equal("c2", Tool(m).ToolCallId);
                Assert.Equal("{\n  \"returncode\": 0,\n  \"output\": \"COMPLETE_TASK_AND_SUBMIT_FINAL_OUTPUT\\nmy answer\\n\"\n}", Tool(m).Text);
            });

        Assert.Equal(2, scope.Api.Requests.Count);
        Assert.Equal(["bash"], scope.Api.Requests[0].Tools.Select(t => t.Name));
        Assert.Equal("Execute a bash command", scope.Api.Requests[0].Tools[0].Description);
        Assert.Equal(["command"], scope.Api.Requests[0].Tools[0].Parameters.Required);
        Assert.Same(ToolChoice.Auto, scope.Api.Requests[0].ToolChoice);
        Assert.Equal(2, scope.Api.Requests[0].Input.Count);
        Assert.Equal(4, scope.Api.Requests[1].Input.Count);

        var infos = scope.Infos;
        Assert.Equal("ls", infos[0]["command"]!.GetValue<string>());
        Assert.Equal(0, infos[0]["returncode"]!.GetValue<int>());
        Assert.Equal("Submitted", infos[^1]["exit_status"]!.GetValue<string>());
        Assert.Equal("my answer\n", infos[^1]["submission"]!.GetValue<string>());
        Assert.Equal(6, ((IReadOnlyList<ChatMessage>)scope.Store.Get(MiniSweAgent.TrajectoryKey)!).Count);
        Assert.Equal(2, (int)scope.Store.Get(MiniSweAgent.ApiCallsKey)!);
    }

    [Fact]
    public async Task a_response_without_tool_calls_gets_the_format_error_and_the_loop_continues()
    {
        using var scope = new Scope(new ScriptedModelApi(ScriptedTurn.Text("Let me think."), Submit("done", "c1")));

        var state = await RunAsync();

        Assert.Equal("Submitted", scope.ExitStatus);
        Assert.Equal(["system", "user", "user", "assistant", "tool"], state.Messages.Select(m => m.Role));
        Assert.Equal(MiniSweTemplates.RenderFormatError(MiniSweTemplates.NoToolCallsError, hasToolCalls: false, "stop"), state.Messages[2].Text);
        Assert.StartsWith("Tool call error:\n\n<error>\nNo tool calls found in the response.", state.Messages[2].Text);
        Assert.Equal(3, scope.Api.Requests[1].Input.Count);
    }

    [Fact]
    public async Task a_truncated_response_gets_the_token_limit_format_error()
    {
        var truncated = new ModelOutput
        {
            Model = ScriptedModelApi.DefaultModelName,
            Choices = [new ChatCompletionChoice(new ChatMessageAssistant("I will now"), StopReason.MaxTokens)],
        };
        using var scope = new Scope(new ScriptedModelApi(ScriptedTurn.From(truncated), Submit("done", "c1")));

        var state = await RunAsync();

        Assert.StartsWith("Your previous response reached the output token limit (finish_reason=length)", state.Messages[2].Text);
        Assert.Equal("Submitted", scope.ExitStatus);
    }

    [Fact]
    public async Task unknown_tools_and_unparseable_arguments_are_format_errors()
    {
        var unparseable = new ModelOutput
        {
            Model = ScriptedModelApi.DefaultModelName,
            Choices =
            [
                new ChatCompletionChoice(
                    new ChatMessageAssistant("", toolCalls: [new ToolCall("c2", "bash", new JsonObject()) { ParseError = "Expecting value: line 1 column 1 (char 0)" }]),
                    StopReason.ToolCalls),
            ],
        };
        using var scope = new Scope(new ScriptedModelApi(
            ScriptedTurn.ToolCall("ls", new { path = "." }, "c1"),
            ScriptedTurn.From(unparseable),
            Bash("true", "c3"),
            Submit("done", "c4")));

        var state = await RunAsync();

        Assert.Equal("Submitted", scope.ExitStatus);
        Assert.Equal(Guidance("Unknown tool 'ls'.Missing 'command' argument in bash tool call."), state.Messages[2].Text);
        Assert.Equal(Guidance("Error parsing tool call arguments: Expecting value: line 1 column 1 (char 0).Missing 'command' argument in bash tool call."), state.Messages[3].Text);
        Assert.Equal(["system", "user", "user", "user", "assistant", "tool", "assistant", "tool"], state.Messages.Select(m => m.Role));
    }

    [Fact]
    public async Task three_consecutive_format_errors_end_the_run()
    {
        using var scope = new Scope(new ScriptedModelApi(ScriptedTurn.Text("a"), ScriptedTurn.Text("b"), ScriptedTurn.Text("c"), Submit("never", "c1")));

        var state = await RunAsync();

        Assert.Equal("RepeatedFormatError", scope.ExitStatus);
        Assert.Equal("", scope.Submission);
        Assert.Equal(["system", "user", "user", "user", "user"], state.Messages.Select(m => m.Role));
        Assert.Equal(3, scope.Api.Requests.Count);
        Assert.Equal(1, scope.Api.Remaining);
        Assert.Equal("c", state.Output.Completion);
    }

    [Fact]
    public async Task the_format_error_counter_resets_after_a_clean_step()
    {
        using var scope = new Scope(new ScriptedModelApi(
            ScriptedTurn.Text("a"), ScriptedTurn.Text("b"), Bash("true", "c1"), ScriptedTurn.Text("c"), ScriptedTurn.Text("d"), Submit("done", "c2")));

        await RunAsync();

        Assert.Equal("Submitted", scope.ExitStatus);
        Assert.Equal(6, scope.Api.Requests.Count);
    }

    [Fact]
    public async Task a_format_error_limit_of_zero_means_no_limit()
    {
        using var scope = new Scope(new ScriptedModelApi(ScriptedTurn.Text("a"), ScriptedTurn.Text("b"), ScriptedTurn.Text("c"), ScriptedTurn.Text("d"), Submit("done", "c1")));

        await RunAsync(new MiniSweAgentOptions { MaxConsecutiveFormatErrors = 0 });

        Assert.Equal("Submitted", scope.ExitStatus);
        Assert.Equal(5, scope.Api.Requests.Count);
    }

    [Fact]
    public async Task the_step_limit_ends_the_run_before_the_next_model_call()
    {
        using var scope = new Scope(new ScriptedModelApi(Bash("true", "c1"), Bash("true", "c2")));

        var state = await RunAsync(new MiniSweAgentOptions { StepLimit = 1 });

        Assert.Equal("LimitsExceeded", scope.ExitStatus);
        Assert.Equal(["system", "user", "assistant", "tool"], state.Messages.Select(m => m.Role));
        Assert.Single(scope.Api.Requests);
        Assert.Equal("LimitsExceeded", scope.Infos[^1]["exit_status"]!.GetValue<string>());
    }

    [Fact]
    public async Task the_wall_time_limit_ends_the_run()
    {
        using var scope = new Scope(new ScriptedModelApi(Bash("sleep 1.1", "c1"), Bash("true", "c2")));

        await RunAsync(new MiniSweAgentOptions { WallTimeLimitSeconds = 1 });

        Assert.Equal("TimeExceeded", scope.ExitStatus);
        Assert.Single(scope.Api.Requests);
    }

    [Fact]
    public async Task a_command_timeout_becomes_an_observation_with_exception_info()
    {
        using var scope = new Scope(new ScriptedModelApi(Bash("sleep 5", "c1"), Submit("done", "c2")));

        var state = await RunAsync(new MiniSweAgentOptions { CommandTimeout = TimeSpan.FromMilliseconds(300) });

        Assert.Equal("Submitted", scope.ExitStatus);
        Assert.Equal(
            "{\n  \"returncode\": -1,\n  \"output\": \"\", \"exception_info\": \"An error occurred while executing the command: Command \\u0027sleep 5\\u0027 timed out after 0.3 seconds\"\n}",
            Tool(state.Messages[3]).Text);
        Assert.Equal(-1, scope.Infos[0]["returncode"]!.GetValue<int>());
    }

    [Fact]
    public async Task output_beyond_ten_thousand_chars_is_shown_as_head_and_tail()
    {
        using var scope = new Scope(new ScriptedModelApi(Bash("printf '%*s' 12000 '' | tr ' ' x", "c1"), Submit("done", "c2")));

        var state = await RunAsync();

        var run = new string('x', 5000);
        Assert.Equal(
            "{\n  \"returncode\": 0,\n  \"output_head\": \"" + run + "\",\n  \"output_tail\": \"" + run + "\",\n  \"elided_chars\": 2000,\n  \"warning\": \"Output too long.\"\n}",
            Tool(state.Messages[3]).Text);
    }

    [Fact]
    public async Task stderr_is_merged_into_the_output_and_the_return_code_is_reported()
    {
        using var scope = new Scope(new ScriptedModelApi(Bash("echo out; echo err >&2; exit 3", "c1"), Submit("done", "c2")));

        var state = await RunAsync();

        Assert.Equal("{\n  \"returncode\": 3,\n  \"output\": \"out\\nerr\\n\"\n}", Tool(state.Messages[3]).Text);
    }

    [Fact]
    public async Task commands_run_in_the_agent_cwd_with_the_default_and_custom_environment()
    {
        using var scope = new Scope(new ScriptedModelApi(Bash("basename \"$(pwd)\"; echo \"$FOO $PAGER $TQDM_DISABLE\"", "c1"), Submit("done", "c2")));
        var cwd = Path.Combine(scope.Sandbox.WorkingDirectory, "project");
        Directory.CreateDirectory(cwd);

        var state = await RunAsync(new MiniSweAgentOptions { Cwd = cwd, Env = new Dictionary<string, string> { ["FOO"] = "bar", ["PAGER"] = "less" } });

        Assert.Equal("{\n  \"returncode\": 0,\n  \"output\": \"project\\nbar less 1\\n\"\n}", Tool(state.Messages[3]).Text);
    }

    [Fact]
    public async Task a_submission_stops_the_remaining_actions_of_the_same_step()
    {
        var two = new ModelOutput
        {
            Model = ScriptedModelApi.DefaultModelName,
            Choices =
            [
                new ChatCompletionChoice(
                    new ChatMessageAssistant(
                        "",
                        toolCalls:
                        [
                            new ToolCall("c1", "bash", new JsonObject { ["command"] = $"echo {MiniSweTemplates.SubmitMarker}" }),
                            new ToolCall("c2", "bash", new JsonObject { ["command"] = "echo later" }),
                        ]),
                    StopReason.ToolCalls),
            ],
        };
        using var scope = new Scope(new ScriptedModelApi(ScriptedTurn.From(two)));

        var state = await RunAsync();

        Assert.Equal("Submitted", scope.ExitStatus);
        Assert.Equal("", scope.Submission);
        Assert.Equal(["system", "user", "assistant", "tool", "tool"], state.Messages.Select(m => m.Role));
        Assert.Equal("c2", Tool(state.Messages[4]).ToolCallId);
        Assert.Equal(NotExecuted, Tool(state.Messages[4]).Text);
    }

    [Fact]
    public async Task a_failed_submit_command_is_not_a_submission()
    {
        using var scope = new Scope(new ScriptedModelApi(Bash($"echo {MiniSweTemplates.SubmitMarker}; exit 1", "c1"), Submit("real", "c2")));

        await RunAsync();

        Assert.Equal("real\n", scope.Submission);
        Assert.Equal(2, scope.Api.Requests.Count);
    }

    [Fact]
    public async Task attempts_score_the_submission_and_continue_after_an_incorrect_one()
    {
        var scored = new List<TaskState>();
        Task<IReadOnlyList<Score>> Scorer(TaskState state)
        {
            scored.Add(state);
            return Task.FromResult<IReadOnlyList<Score>>([new Score(scored.Count == 1 ? "I" : "C")]);
        }

        using var scope = new Scope(new ScriptedModelApi(Submit("answer 1", "c1"), Submit("answer 2", "c2")), Scorer);

        var state = await RunAsync(new MiniSweAgentOptions { Attempts = new AgentAttempts(2) });

        var graded = Assert.Single(scored);
        Assert.Equal("answer 1\n", graded.Output.Completion);
        Assert.Equal(4, graded.Messages.Count);
        Assert.Same(scope.Store, graded.Store);
        Assert.Equal("answer 2\n", scope.Submission);
        Assert.Equal("answer 2\n", state.Output.Completion);
        Assert.Equal(["system", "user", "assistant", "tool", "user", "assistant", "tool"], state.Messages.Select(m => m.Role));
        Assert.Equal(Incorrect + "\n\n" + MiniSweTemplates.ResumeReminder, state.Messages[4].Text);
        Assert.Equal(5, scope.Api.Requests[1].Input.Count);
    }

    [Fact]
    public async Task a_correct_first_attempt_ends_the_attempts_loop()
    {
        var calls = 0;
        Task<IReadOnlyList<Score>> Scorer(TaskState state)
        {
            calls++;
            return Task.FromResult<IReadOnlyList<Score>>([new Score(0.4)]);
        }

        using var scope = new Scope(new ScriptedModelApi(Submit("answer 1", "c1"), Submit("answer 2", "c2")), Scorer);

        await RunAsync(new MiniSweAgentOptions { Attempts = new AgentAttempts(3, ScoreValue: value => value is ScoreValue.Num { Value: > 0.3 } ? 1.0 : 0.0) });

        Assert.Equal(1, calls);
        Assert.Equal("answer 1\n", scope.Submission);
        Assert.Equal(1, scope.Api.Remaining);
    }

    [Fact]
    public async Task attempts_without_a_scorer_fail_like_python_score()
    {
        using var scope = new Scope(new ScriptedModelApi(Submit("answer 1", "c1")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(new MiniSweAgentOptions { Attempts = new AgentAttempts(2) }));

        Assert.Equal("The score() function can only be called while executing a task with a scorer.", ex.Message);
    }

    [Fact]
    public async Task task_system_messages_and_the_system_prompt_are_folded_into_the_task_prompt()
    {
        using var scope = new Scope(new ScriptedModelApi(Submit("done", "c1")));

        var state = await RunAsync(
            new MiniSweAgentOptions { SystemPrompt = "Extra rules." },
            State(new ChatMessageSystem("Be careful."), new ChatMessageUser("Fix it"), new ChatMessageUser("Please")));

        Assert.Equal("You are a helpful assistant that can interact with a computer.", state.Messages[0].Text);
        Assert.StartsWith("Please solve this issue: System instructions:\nBe careful.\n\nExtra rules.\n\nTask:\nFix it\n\nPlease\n\nYou can execute", state.Messages[1].Text);
    }

    [Fact]
    public async Task a_second_execution_resumes_the_stored_trajectory_with_the_reminder()
    {
        using var scope = new Scope(new ScriptedModelApi(Submit("first", "c1"), Submit("second", "c2")));

        var state = await RunAsync();
        state.Messages.Add(new ChatMessageUser("Now also do X"));
        state = await RunAsync(state: state);

        // As in inspect_swe, the mini-swe system message now in the state is folded into the resumed task.
        Assert.Equal(["system", "user", "assistant", "tool", "user", "assistant", "tool"], state.Messages.Select(m => m.Role));
        Assert.Equal(
            "System instructions:\nYou are a helpful assistant that can interact with a computer.\n\nTask:\nNow also do X\n\n" + MiniSweTemplates.ResumeReminder,
            state.Messages[4].Text);
        Assert.Equal("second\n", scope.Submission);
        Assert.Equal(5, scope.Api.Requests[1].Input.Count);
        Assert.Equal(7, ((IReadOnlyList<ChatMessage>)scope.Store.Get(MiniSweAgent.TrajectoryKey)!).Count);
        Assert.Equal(2, (int)scope.Store.Get(MiniSweAgent.ApiCallsKey)!);
    }

    [Fact]
    public async Task the_step_limit_counts_calls_across_resumed_executions()
    {
        using var scope = new Scope(new ScriptedModelApi(Submit("first", "c1"), Submit("second", "c2")));

        var state = await RunAsync(new MiniSweAgentOptions { StepLimit = 1 });
        state.Messages.Add(new ChatMessageUser("Again"));
        await RunAsync(new MiniSweAgentOptions { StepLimit = 1 }, state);

        Assert.Equal("LimitsExceeded", scope.ExitStatus);
        Assert.Single(scope.Api.Requests);
    }

    [Fact]
    public async Task resuming_without_a_stored_trajectory_is_an_error()
    {
        using var scope = new Scope(new ScriptedModelApi(Submit("x", "c1")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunAsync(state: State(new ChatMessageUser("a"), new ChatMessageAssistant("b"), new ChatMessageUser("c"))));

        Assert.StartsWith("Cannot resume: no mini-swe-agent trajectory was found for this sample.", ex.Message);
        Assert.Empty(scope.Api.Requests);
    }

    [Fact]
    public async Task input_ending_with_an_assistant_message_is_rejected()
    {
        using var scope = new Scope(new ScriptedModelApi());

        await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(state: State(new ChatMessageUser("a"), new ChatMessageAssistant("b"))));
    }

    [Fact]
    public async Task an_uncaught_model_error_records_the_exit_status_and_propagates()
    {
        using var scope = new Scope(new ScriptedModelApi(Bash("true", "c1"), ScriptedTurn.Throw(new InvalidOperationException("boom"))));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync());

        Assert.Equal("boom", ex.Message);
        Assert.Equal("InvalidOperationException", scope.ExitStatus);
        Assert.Equal(4, ((IReadOnlyList<ChatMessage>)scope.Store.Get(MiniSweAgent.TrajectoryKey)!).Count);
    }

    [Fact]
    public async Task the_agent_def_carries_the_configured_name_and_description()
    {
        var agent = MiniSweFactory.Agent(new MiniSweAgentOptions { Name = "mini", Description = "tiny" });

        Assert.Equal("mini", agent.Name);
        Assert.Equal("tiny", agent.Description);
        Assert.Equal("mini-swe-agent", MiniSweFactory.Agent().Name);
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Execute(State(new ChatMessageUser("x")), CancellationToken.None));
    }

    [Fact]
    public void check_finished_reads_the_marker_line_like_upstream()
    {
        Assert.Equal("line1\nline2", MiniSweAgent.CheckFinished(new CommandObservation("  \n COMPLETE_TASK_AND_SUBMIT_FINAL_OUTPUT \r\nline1\nline2", 0, "")));
        Assert.Equal("", MiniSweAgent.CheckFinished(new CommandObservation("COMPLETE_TASK_AND_SUBMIT_FINAL_OUTPUT\n", 0, "")));
        Assert.Null(MiniSweAgent.CheckFinished(new CommandObservation("COMPLETE_TASK_AND_SUBMIT_FINAL_OUTPUT\n", 1, "")));
        Assert.Null(MiniSweAgent.CheckFinished(new CommandObservation("x\nCOMPLETE_TASK_AND_SUBMIT_FINAL_OUTPUT\n", 0, "")));
        Assert.Null(MiniSweAgent.CheckFinished(new CommandObservation("", 0, "")));
        Assert.Equal(["a\r\n", "b\n", "c\r", "d"], MiniSweAgent.SplitLinesKeepEnds("a\r\nb\nc\rd"));
    }

    [Fact]
    public void dangling_tool_calls_are_stripped_before_resuming()
    {
        var dangling = new List<ChatMessage> { new ChatMessageUser("u"), new ChatMessageAssistant("", toolCalls: [new ToolCall("c1", "bash", new JsonObject())]) };
        var withText = new List<ChatMessage> { new ChatMessageAssistant("partial", toolCalls: [new ToolCall("c1", "bash", new JsonObject())]) };
        var answered = new List<ChatMessage>
        {
            new ChatMessageAssistant("", toolCalls: [new ToolCall("c1", "bash", new JsonObject())]),
            new ChatMessageTool("ok", toolCallId: "c1"),
            new ChatMessageAssistant("", toolCalls: [new ToolCall("c1", "bash", new JsonObject())]),
        };

        MiniSweAgent.FixDanglingToolCalls(dangling);
        MiniSweAgent.FixDanglingToolCalls(withText);
        MiniSweAgent.FixDanglingToolCalls(answered);

        Assert.Null(Assistant(dangling[1]).ToolCalls);
        Assert.Equal("Task completed.", dangling[1].Text);
        Assert.Null(Assistant(withText[0]).ToolCalls);
        Assert.Equal("partial", withText[0].Text);
        Assert.NotNull(Assistant(answered[2]).ToolCalls);
    }

    /// <summary>A sample context around a scripted model and a local sandbox for one test.</summary>
    private sealed class Scope : IDisposable
    {
        private readonly IDisposable _ambient;

        public Scope(ScriptedModelApi api, Func<TaskState, Task<IReadOnlyList<Score>>>? scorer = null, Limits? limits = null)
        {
            Api = api;
            Model = new Model(api);
            Sandbox = new LocalSandboxEnvironment();
            Context = new SampleContext { ActiveModel = Model, Sandboxes = SandboxEnvironments.Single(Sandbox), Scorer = scorer, Limits = limits ?? new Limits() };
            _ambient = SampleContext.Begin(Context);
        }

        public ScriptedModelApi Api { get; }

        public Model Model { get; }

        public LocalSandboxEnvironment Sandbox { get; }

        public SampleContext Context { get; }

        public Store Store => Context.Store;

        public string? ExitStatus => Store.Get(MiniSweAgent.ExitStatusKey) as string;

        public string? Submission => Store.Get(MiniSweAgent.SubmissionKey) as string;

        /// <summary>The data of every mini_swe_agent info event, in order.</summary>
        public List<JsonNode> Infos =>
            Context.Transcript.Events.OfType<InfoEvent>().Where(e => e.Source == MiniSweAgent.TranscriptSource).Select(e => e.Data!).ToList();

        public void Dispose()
        {
            _ambient.Dispose();
            Sandbox.Dispose();
        }
    }

    [Fact]
    public async Task a_sample_limit_leaves_the_partial_trajectory_on_the_state()
    {
        using var scope = new Scope(new ScriptedModelApi(Bash("true", "c1"), Bash("true", "c2")), limits: new Limits { MessageLimit = 3 });
        var state = State(new ChatMessageUser("Say hello"));

        var ex = await Assert.ThrowsAsync<LimitExceededException>(() => RunAsync(state: state));

        Assert.Equal("message", ex.Type);
        Assert.Equal("LimitExceededException", scope.ExitStatus);
        Assert.Equal(["system", "user", "assistant", "tool"], state.Messages.Select(m => m.Role));
        Assert.Equal("c1", Assert.Single(Assistant(state.Messages[2]).ToolCalls!).Id);
        Assert.Single(scope.Api.Requests);
    }

    [Theory]
    [InlineData(StopReason.MaxTokens, true)]
    [InlineData(StopReason.ModelLength, true)]
    [InlineData(StopReason.Stop, false)]
    [InlineData(StopReason.ContentFilter, false)]
    public void both_truncation_stops_render_the_token_limit_branch_and_the_rest_the_guidance(StopReason stopReason, bool truncated)
    {
        var output = new ModelOutput
        {
            Model = ScriptedModelApi.DefaultModelName,
            Choices = [new ChatCompletionChoice(new ChatMessageAssistant("I will now"), stopReason)],
        };

        var (actions, error) = MiniSweAgent.ParseActions(output);

        Assert.Empty(actions);
        if (truncated)
        {
            Assert.Equal("length", MiniSweAgent.FinishReason(stopReason));
            Assert.StartsWith("Your previous response reached the output token limit (finish_reason=length)", error);
        }
        else
        {
            Assert.StartsWith("Tool call error:\n\n<error>\nNo tool calls found in the response.", error);
        }
    }
}

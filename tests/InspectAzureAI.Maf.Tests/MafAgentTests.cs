using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using EvalRunner = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;
using Scorers = InspectAzureAI.Eval.Scorers.Scorers;

namespace InspectAzureAI.Maf.Tests;

/// <summary>
/// <see cref="AgentFramework.Agent"/> end to end: an Agent Framework <see cref="ChatClientAgent"/> running its own tool loop
/// with every model call served by a scripted Inspect model, through <c>Eval.RunAsync</c> (log, scoring, attempts,
/// limits, approval) and directly under a sample context (Inspect sandbox tools, tool events, tool failures).
/// </summary>
public sealed class MafAgentTests : IDisposable
{
    private const string Prompt = "Shout hi";

    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-maf-tests", Guid.NewGuid().ToString("N"));

    private int _shouts;

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

    /// <summary>An Agent Framework function tool (not an Inspect tool): the framework invokes it in-process.</summary>
    private AIFunction Shout() => AIFunctionFactory.Create(
        (string text) =>
        {
            _shouts++;
            return text.ToUpperInvariant();
        },
        "shout",
        "Upper-cases text.");

    private static ScriptedTurn ShoutCall(string text = "hi", string id = "call_shout") => ScriptedTurn.ToolCall("shout", new { text }, id: id, text: "Shouting.");

    private static ScriptedTurn SubmitCall(string answer, string id = "call_submit") => ScriptedTurn.ToolCall("submit", new { answer }, id: id, text: "Done.");

    private async Task<EvalLog> RunEvalAsync(ScriptedModelApi api, AgentDef agent, int? tokenLimit = null, int messageLimit = 30)
    {
        var task = new EvalTask
        {
            Name = "shout",
            Dataset = new MemoryDataset([new Sample(Prompt) { Target = "HI" }], name: "shout"),
            Solver = Agents.AsSolver(agent),
            Scorers = [Scorers.Includes()],
            TokenLimit = tokenLimit,
            // a scripted model that runs out of turns answers with text forever; the limit turns that into a failure instead of a hang
            MessageLimit = messageLimit,
        };
        return await EvalRunner.RunAsync(task, new EvalOptions { Model = new Model(api), LogDir = _logDir, LogFormat = LogFormat.Json, MaxSamples = 1 });
    }

    private static EvalSample SingleSample(EvalLog log)
    {
        Assert.Equal(EvalStatus.Success, log.Status);
        return Assert.Single(log.Samples!);
    }

    [Fact]
    public async Task a_chat_client_agent_runs_its_tool_loop_over_the_inspect_model()
    {
        var api = new ScriptedModelApi(ShoutCall(), SubmitCall("HI"));

        var log = await RunEvalAsync(api, AgentFramework.Agent(new MafAgentOptions { Tools = [Shout()] }));

        var sample = SingleSample(log);
        Assert.Equal(1, _shouts);
        Assert.Equal("C", sample.Scores!["includes"].Text);
        Assert.Equal("HI", sample.Output.Completion);
        Assert.Collection(
            sample.Messages,
            message => Assert.Contains("call the submit() function", Assert.IsType<ChatMessageSystem>(message).Text),
            message => Assert.Equal(Prompt, Assert.IsType<ChatMessageUser>(message).Text),
            message => Assert.Equal("shout", Assert.Single(Assert.IsType<ChatMessageAssistant>(message).ToolCalls!).Function),
            message => Assert.Equal(("shout", "HI"), (Assert.IsType<ChatMessageTool>(message).Function, message.Text)),
            message => Assert.Equal("submit", Assert.Single(Assert.IsType<ChatMessageAssistant>(message).ToolCalls!).Function),
            message => Assert.Equal(("submit", "HI"), (Assert.IsType<ChatMessageTool>(message).Function, message.Text)));
        Assert.Equal(2, sample.Events.OfType<ModelEvent>().Count());
        Assert.Equal(["shout", "submit"], sample.Events.OfType<ToolEvent>().Select(e => e.Function));
        Assert.Equal("HI", sample.Events.OfType<ToolEvent>().First().Result);
        Assert.Equal(2, api.Requests.Count);
        Assert.Contains(api.Requests[1].Input, message => message is ChatMessageTool { Function: "shout", Text: "HI" });
        Assert.Equal(["shout", "submit"], api.Requests[0].Tools.Select(tool => tool.Name));
    }

    [Fact]
    public async Task an_incorrect_submission_is_retried_within_the_attempts()
    {
        var api = new ScriptedModelApi(SubmitCall("nope", "call_1"), SubmitCall("HI", "call_2"));

        var log = await RunEvalAsync(api, AgentFramework.Agent(new MafAgentOptions { Tools = [Shout()], Attempts = new AgentAttempts(2) }));

        var sample = SingleSample(log);
        Assert.Equal("C", sample.Scores!["includes"].Text);
        Assert.Equal("HI", sample.Output.Completion);
        Assert.Equal(2, api.Requests.Count);
        Assert.Equal(new AgentAttempts().IncorrectMessage, Assert.IsType<ChatMessageUser>(api.Requests[1].Input[^1]).Text);
        Assert.Contains(sample.Messages, message => message is ChatMessageUser user && user.Text == new AgentAttempts().IncorrectMessage);
        Assert.Equal(2, sample.Messages.OfType<ChatMessageTool>().Count(message => message.Function == "submit"));
    }

    [Fact]
    public async Task an_incorrect_final_attempt_is_kept_and_scored()
    {
        var api = new ScriptedModelApi(SubmitCall("nope"));

        var log = await RunEvalAsync(api, AgentFramework.Agent(new MafAgentOptions { Tools = [Shout()] }));

        var sample = SingleSample(log);
        Assert.Equal("I", sample.Scores!["includes"].Text);
        Assert.Equal("nope", sample.Output.Completion);
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task stopping_without_a_submission_is_answered_with_the_continue_message()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("Let me think."), SubmitCall("HI"));

        var log = await RunEvalAsync(api, AgentFramework.Agent(new MafAgentOptions { Tools = [Shout()] }));

        var sample = SingleSample(log);
        Assert.Equal("C", sample.Scores!["includes"].Text);
        Assert.Equal(2, api.Requests.Count);
        Assert.Contains("call the `submit()` tool", Assert.IsType<ChatMessageUser>(api.Requests[1].Input[^1]).Text);
        Assert.Contains(sample.Messages, message => message is ChatMessageAssistant { Text: "Let me think." });
    }

    [Fact]
    public async Task without_a_submit_tool_the_run_ends_when_the_agent_stops_calling_tools()
    {
        var api = new ScriptedModelApi(ShoutCall(), ScriptedTurn.Text("HI"));

        var log = await RunEvalAsync(api, AgentFramework.Agent(new MafAgentOptions { Tools = [Shout()], Submit = false, Instructions = "Shout, then answer." }));

        var sample = SingleSample(log);
        Assert.Equal("C", sample.Scores!["includes"].Text);
        Assert.Equal("HI", sample.Output.Completion);
        Assert.Equal(2, api.Requests.Count);
        Assert.Equal(["shout"], api.Requests[0].Tools.Select(tool => tool.Name));
        Assert.Equal("Shout, then answer.", Assert.IsType<ChatMessageSystem>(sample.Messages[0]).Text);
    }

    [Fact]
    public async Task a_token_limit_ends_the_sample()
    {
        var api = new ScriptedModelApi(ScriptedTurn.ToolCall("shout", new { text = "hi" }, usage: new ModelUsage(20, 5, 25)), SubmitCall("HI"));

        var log = await RunEvalAsync(api, AgentFramework.Agent(new MafAgentOptions { Tools = [Shout()] }), tokenLimit: 10);

        var sample = SingleSample(log);
        Assert.Equal("token", sample.Limit!.Type);
        Assert.Single(api.Requests);
        Assert.Null(sample.Error);
    }

    [Fact]
    public async Task approval_rejects_a_tool_call_before_the_framework_runs_it()
    {
        var api = new ScriptedModelApi(ShoutCall(), SubmitCall("HI"));
        // an unmatched tool is rejected under a policy list, so the submit tool needs its own approval
        var options = new MafAgentOptions
        {
            Tools = [Shout()],
            Approval = [new ApprovalPolicy(Approvers.Auto(ApprovalDecision.Reject), "shout"), new ApprovalPolicy(Approvers.Auto(), "submit")],
        };

        var log = await RunEvalAsync(api, AgentFramework.Agent(options));

        var sample = SingleSample(log);
        Assert.Equal(0, _shouts);
        Assert.Equal("C", sample.Scores!["includes"].Text);
        Assert.Equal(2, api.Requests.Count);
        Assert.Contains(api.Requests[1].Input, message => message is ChatMessageTool { Function: "shout", Error: not null });
        Assert.DoesNotContain(sample.Events.OfType<ToolEvent>(), e => e.Function == "shout");
    }

    /// <summary>A sample context over a scripted model and an optional fake sandbox, for running the agent without the eval runner.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly IDisposable _scope;

        public Harness(ScriptedModelApi api, FakeSandboxEnvironment? sandbox = null)
        {
            Context = new SampleContext
            {
                ActiveModel = new Model(api),
                Sandboxes = sandbox is null ? null : SandboxEnvironments.Single(sandbox),
            };
            _scope = SampleContext.Begin(Context);
        }

        public SampleContext Context { get; }

        public async Task<AgentState> RunAsync(AgentDef agent, CancellationTokenSource? cancellation = null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellation?.Token ?? CancellationToken.None);
            return await agent.Execute(new AgentState([new ChatMessageUser(Prompt)]), linked.Token);
        }

        public void Dispose() => _scope.Dispose();
    }

    [Fact]
    public async Task inspect_sandbox_tools_run_inside_the_framework_and_record_tool_events()
    {
        var api = new ScriptedModelApi(ScriptedTurn.ToolCall("bash", new { cmd = "echo hello" }, id: "call_bash"), SubmitCall("hello"));
        var sandbox = new FakeSandboxEnvironment(_ => FakeSandboxEnvironment.Ok("hello\n"));
        using var harness = new Harness(api, sandbox);

        var state = await harness.RunAsync(AgentFramework.Agent(new MafAgentOptions { Tools = MafTools.FromToolDefs([SandboxTools.Bash()]) }));

        Assert.Equal(["bash", "--login", "-c", "echo hello"], Assert.Single(sandbox.Calls).Cmd);
        Assert.Contains(state.Messages, message => message is ChatMessageTool { Function: "bash", ToolCallId: "call_bash", Text: "hello\n" });
        Assert.Equal("hello", state.Output.Completion);
        var events = harness.Context.Transcript.Events.OfType<ToolEvent>().ToList();
        Assert.Equal(["bash", "submit"], events.Select(e => e.Function));
        Assert.Equal("hello\n", events[0].Result);
        Assert.Equal("echo hello", events[0].Arguments["cmd"]!.GetValue<string>());
        Assert.NotNull(events[0].Completed);
        Assert.Equal("bash", Assert.Single(api.Requests[0].Tools, tool => tool.Name == "bash").Name);
        Assert.Contains("cmd", api.Requests[0].Tools[0].Parameters.Required);
    }

    [Fact]
    public async Task a_failing_tool_is_reported_to_the_model_as_an_error()
    {
        var api = new ScriptedModelApi(ScriptedTurn.ToolCall("explode", new { }, id: "call_x"), SubmitCall("gave up"));
        using var harness = new Harness(api);
        var explode = AIFunctionFactory.Create(new Func<string>(() => throw new InvalidOperationException("kaboom")), "explode", "Always fails.");

        var state = await harness.RunAsync(AgentFramework.Agent(new MafAgentOptions { Tools = [explode] }));

        var report = Assert.Single(api.Requests[1].Input.OfType<ChatMessageTool>(), message => message.Function == "explode");
        Assert.NotNull(report.Error);
        var toolEvent = Assert.Single(harness.Context.Transcript.Events.OfType<ToolEvent>(), e => e.Function == "explode");
        Assert.Equal("kaboom", toolEvent.Error!.Message);
        Assert.Equal("gave up", state.Output.Completion);
    }

    [Fact]
    public async Task a_limit_raised_inside_a_tool_is_rethrown_from_the_run()
    {
        var api = new ScriptedModelApi(ScriptedTurn.ToolCall("slow", new { }, id: "call_slow"), SubmitCall("never"));
        using var harness = new Harness(api);
        var slow = AIFunctionFactory.Create(new Func<string>(() => throw new LimitExceededException("working", "10", 12)), "slow", "Trips the working limit.");

        var error = await Assert.ThrowsAsync<LimitExceededException>(() => harness.RunAsync(AgentFramework.Agent(new MafAgentOptions { Tools = [slow] })));

        Assert.Equal("working", error.Type);
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task an_agent_factory_supplies_the_framework_agent()
    {
        var api = new ScriptedModelApi(SubmitCall("HI"));
        using var harness = new Harness(api);
        IChatClient? seen = null;
        var options = new MafAgentOptions
        {
            Tools = [Shout()],
            AgentFactory = (client, tools) =>
            {
                seen = client;
                return new ChatClientAgent(client, "You are a pirate.", tools: tools);
            },
        };

        var state = await harness.RunAsync(AgentFramework.Agent(options));

        // the client handed to the factory already performs function invocation over the Inspect client
        Assert.IsType<FunctionInvokingChatClient>(seen);
        Assert.IsType<InspectChatClient>(seen!.GetService(typeof(InspectChatClient)));
        Assert.Equal("You are a pirate.", Assert.IsType<ChatMessageSystem>(api.Requests[0].Input[0]).Text);
        Assert.Equal(["shout", "submit"], api.Requests[0].Tools.Select(tool => tool.Name));
        Assert.Equal("HI", state.Output.Completion);
    }

    [Fact]
    public async Task inspect_tools_run_through_the_tool_executor()
    {
        // a missing required argument, a 20 KiB output and a sandbox timeout: validated, truncated and mapped as the native loop does
        var api = new ScriptedModelApi(
            ScriptedTurn.ToolCall("bash", new { }, id: "call_missing"),
            ScriptedTurn.ToolCall("bash", new { cmd = "yes" }, id: "call_big"),
            ScriptedTurn.ToolCall("bash", new { cmd = "sleep" }, id: "call_slow"),
            SubmitCall("done"));
        var sandbox = new FakeSandboxEnvironment(cmd => cmd[^1] == "yes" ? FakeSandboxEnvironment.Ok(new string('x', 20_000)) : throw new SandboxTimeoutException("timed out", "partial"));
        using var harness = new Harness(api, sandbox);

        var state = await harness.RunAsync(AgentFramework.Agent(new MafAgentOptions { Tools = MafTools.FromToolDefs([SandboxTools.Bash()]) }));

        var events = harness.Context.Transcript.Events.OfType<ToolEvent>().ToList();
        Assert.Equal(["call_missing", "call_big", "call_slow", "call_submit"], events.Select(e => e.Id));
        Assert.Equal("parsing", events[0].Error!.Type);
        Assert.NotNull(events[1].Truncated);
        Assert.Equal("timeout", events[2].Error!.Type);
        // the model's next request carries the executor's tool messages: the error object, the truncation notice, the partial output
        var reports = api.Requests[3].Input.OfType<ChatMessageTool>().Where(message => message.Function == "bash").ToList();
        var parsing = reports[0].Error!;
        Assert.Equal("parsing", parsing.Type);
        Assert.StartsWith("Required parameter cmd", parsing.Message);
        Assert.Contains("<START_TOOL_OUTPUT>", reports[1].Text);
        Assert.Equal(("timeout", "partial"), (reports[2].Error!.Type, reports[2].Text));
        Assert.Contains(state.Messages, message => message is ChatMessageTool { ToolCallId: "call_slow", Error.Type: "timeout" });
        Assert.Equal("done", state.Output.Completion);
    }

    [Fact]
    public async Task approval_is_applied_once_per_inspect_tool_call()
    {
        var api = new ScriptedModelApi(ScriptedTurn.ToolCall("bash", new { cmd = "ls" }, id: "call_ls"), SubmitCall("ok"));
        var sandbox = new FakeSandboxEnvironment(_ => FakeSandboxEnvironment.Ok("a\n"));
        using var harness = new Harness(api, sandbox);
        var approvals = new List<string>();
        var counting = new ApproverDef("count", (_, call, _, _, _) =>
        {
            approvals.Add(call.Function);
            return Task.FromResult(new Approval(ApprovalDecision.Approve));
        });
        var options = new MafAgentOptions { Tools = MafTools.FromToolDefs([SandboxTools.Bash()]), Approval = [new ApprovalPolicy(counting, "*")] };

        var state = await harness.RunAsync(AgentFramework.Agent(options));

        Assert.Equal(["bash", "submit"], approvals);
        Assert.Equal("ok", state.Output.Completion);
    }

    [Fact]
    public async Task the_framework_iteration_cap_does_not_end_the_run()
    {
        var api = new ScriptedModelApi(Enumerable.Range(0, 45).Select(i => ShoutCall("hi", $"call_{i}")).Append(SubmitCall("HI")));

        var log = await RunEvalAsync(api, AgentFramework.Agent(new MafAgentOptions { Tools = [Shout()] }), messageLimit: 200);

        var sample = SingleSample(log);
        Assert.Equal(45, _shouts);
        Assert.Equal("C", sample.Scores!["includes"].Text);
        Assert.Equal(46, api.Requests.Count);
    }

    [Fact]
    public async Task a_submission_alongside_a_sibling_call_runs_the_sibling_too()
    {
        var both = new ChatMessageAssistant("Both.", [new ToolCall("call_a", "submit", new JsonObject { ["answer"] = "HI" }), new ToolCall("call_b", "shout", new JsonObject { ["text"] = "hi" })]);
        var api = new ScriptedModelApi(ScriptedTurn.From(new ModelOutput { Model = "scripted", Choices = [new ChatCompletionChoice(both, StopReason.ToolCalls)] }));

        var log = await RunEvalAsync(api, AgentFramework.Agent(new MafAgentOptions { Tools = [Shout()] }));

        var sample = SingleSample(log);
        Assert.Equal(1, _shouts);
        Assert.Single(api.Requests);
        Assert.Equal("HI", sample.Output.Completion);
        Assert.Contains(sample.Messages, message => message is ChatMessageTool { ToolCallId: "call_a", Text: "HI" });
        Assert.Contains(sample.Messages, message => message is ChatMessageTool { ToolCallId: "call_b", Text: "HI" });
    }

    [Fact]
    public async Task a_submission_followed_by_a_throwing_sibling_still_ends_the_run()
    {
        var both = new ChatMessageAssistant("Both.", [new ToolCall("call_a", "submit", new JsonObject { ["answer"] = "HI" }), new ToolCall("call_b", "explode", new JsonObject())]);
        var api = new ScriptedModelApi(ScriptedTurn.From(new ModelOutput { Model = "scripted", Choices = [new ChatCompletionChoice(both, StopReason.ToolCalls)] }), SubmitCall("again"));
        var explode = AIFunctionFactory.Create(new Func<string>(() => throw new InvalidOperationException("kaboom")), "explode", "Always fails.");

        var log = await RunEvalAsync(api, AgentFramework.Agent(new MafAgentOptions { Tools = [explode] }));

        var sample = SingleSample(log);
        Assert.Single(api.Requests);
        Assert.Equal("HI", sample.Output.Completion);
        Assert.Contains(sample.Messages, message => message is ChatMessageTool { ToolCallId: "call_b", Error: not null });
        Assert.Contains(sample.Events.OfType<ToolEvent>(), e => e.Function == "explode" && e.Failed == true);
    }

    [Fact]
    public async Task an_unhandled_exception_in_an_inspect_tool_fails_the_sample()
    {
        var api = new ScriptedModelApi(ScriptedTurn.ToolCall("broken", new { }, id: "call_broken"), SubmitCall("never"));
        using var harness = new Harness(api);
        var broken = new ToolDef("broken", "Always throws.", new ToolParams(), (_, _) => throw new InvalidOperationException("bug in the tool"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunAsync(AgentFramework.Agent(new MafAgentOptions { Tools = MafTools.FromToolDefs([broken]) })));

        Assert.Equal("bug in the tool", error.Message);
        Assert.Single(api.Requests);
        Assert.True(Assert.Single(harness.Context.Transcript.Events.OfType<ToolEvent>(), e => e.Function == "broken").Failed);
    }

    [Fact]
    public async Task cancellation_inside_a_tool_records_no_event()
    {
        var api = new ScriptedModelApi(ScriptedTurn.ToolCall("stop", new { }, id: "call_stop"), SubmitCall("never"));
        using var harness = new Harness(api);
        using var cancellation = new CancellationTokenSource();
        var stop = AIFunctionFactory.Create(
            new Action(() =>
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }),
            "stop",
            "Cancels the run.");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.RunAsync(AgentFramework.Agent(new MafAgentOptions { Tools = [stop] }), cancellation));

        Assert.DoesNotContain(harness.Context.Transcript.Events.OfType<ToolEvent>(), e => e.Function == "stop");
        Assert.Single(api.Requests);
    }

    [Fact]
    public void zero_attempts_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentFramework.Agent(new MafAgentOptions { Attempts = new AgentAttempts(0) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentFramework.Agent(new MafAgentOptions { RetryRefusals = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentFramework.Agent(new MafAgentOptions { MaxToolIterations = 0 }));
        Assert.Throws<ArgumentException>(() => AgentFramework.Agent(new MafAgentOptions { Tools = [Shout(), Shout()] }));
        Assert.Throws<ArgumentException>(() => AgentFramework.Agent(new MafAgentOptions { Tools = [AIFunctionFactory.Create(() => "x", "submit")] }));
    }
}

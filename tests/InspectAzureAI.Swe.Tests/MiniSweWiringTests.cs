using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Model.Compaction;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Sandbox.Local;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Swe.MiniSwe;

namespace InspectAzureAI.Swe.Tests;

using Model = InspectAzureAI.Eval.Model.Model;
using MiniSweFactory = InspectAzureAI.Swe.MiniSwe.MiniSwe;

/// <summary>
/// The mini-swe loop's use of the shared subsystems: the ambient tool approval applied to each bash call (reject,
/// terminate, modify), the prompt cache replaying a run without provider calls, and compaction of the model input
/// (threshold and context-overflow recovery) without touching the trajectory.
/// </summary>
public class MiniSweWiringTests
{
    private static ScriptedTurn Bash(string command, string id) => ScriptedTurn.ToolCall("bash", new { command }, id);

    private static ScriptedTurn Submit(string answer, string id) => Bash($"printf '{MiniSweTemplates.SubmitMarker}\\n{answer}\\n'", id);

    private static Task<AgentState> RunAsync(MiniSweAgentOptions? options = null) =>
        MiniSweFactory.Agent(options).Execute(new AgentState([new ChatMessageUser("Say hello")]), CancellationToken.None);

    private static ApprovalPolicy Policy(ApprovalDecision decision, string tools) => new(Approvers.Auto(decision), tools);

    private static ChatMessageTool Tool(ChatMessage message) => Assert.IsType<ChatMessageTool>(message);

    [Fact]
    public async Task a_rejected_bash_call_is_not_run_and_its_tool_message_carries_the_approval_error()
    {
        using var scope = new Scope(new ScriptedModelApi(Bash("rm -f marker.txt", "c1"), Submit("done", "c2")));
        var marker = Path.Combine(scope.Sandbox.WorkingDirectory, "marker.txt");
        await File.WriteAllTextAsync(marker, "x");

        AgentState state;
        using (ToolApproval.Begin([Policy(ApprovalDecision.Reject, "bash(command='rm*"), Policy(ApprovalDecision.Approve, "*")]))
        {
            state = await RunAsync();
        }

        Assert.True(File.Exists(marker));
        Assert.Equal("Submitted", scope.ExitStatus);
        var rejected = Tool(state.Messages[3]);
        Assert.Equal("c1", rejected.ToolCallId);
        Assert.Equal("approval", rejected.Error!.Type);
        Assert.Equal(Approvers.AutomaticDecision, rejected.Error.Message);
        Assert.Contains("\"returncode\": -1", rejected.Text);
        Assert.Contains($"\"exception_info\": \"{Approvers.AutomaticDecision}\"", rejected.Text);
        Assert.Null(Tool(state.Messages[5]).Error);

        var approvals = scope.Context.Transcript.Events.OfType<ApprovalEvent>().ToList();
        Assert.Equal(["reject", "approve"], approvals.Select(e => e.Decision));
        Assert.Equal("c1", approvals[0].Call.Id);
        Assert.Equal("rm -f marker.txt", scope.Infos[0]["command"]!.GetValue<string>());
        Assert.Equal("reject", scope.Infos[0]["approval"]!.GetValue<string>());
    }

    [Fact]
    public async Task a_terminate_decision_ends_the_run_with_the_terminate_exception()
    {
        using var scope = new Scope(new ScriptedModelApi(Bash("rm -f marker.txt", "c1"), Submit("done", "c2")));
        var marker = Path.Combine(scope.Sandbox.WorkingDirectory, "marker.txt");
        await File.WriteAllTextAsync(marker, "x");

        using (ToolApproval.Begin([Policy(ApprovalDecision.Terminate, "bash(command='rm*"), Policy(ApprovalDecision.Approve, "*")]))
        {
            var ex = await Assert.ThrowsAsync<TerminateSampleException>(() => RunAsync());
            Assert.Equal("Tool call approver requested termination.", ex.Message);
        }

        Assert.True(File.Exists(marker));
        Assert.Equal("TerminateSampleException", scope.ExitStatus);
        Assert.Single(scope.Api.Requests);
        Assert.Equal(3, ((IReadOnlyList<ChatMessage>)scope.Store.Get(MiniSweAgent.TrajectoryKey)!).Count);   // system, task, the assistant turn; no tool message
    }

    [Fact]
    public async Task a_modified_call_runs_the_approvers_command_and_keeps_the_models_call_in_the_trajectory()
    {
        using var scope = new Scope(new ScriptedModelApi(Bash("echo original", "c1"), Submit("done", "c2")));
        var modifier = new ApproverDef("modifier", (_, call, _, _, _) => Task.FromResult(new Approval(
            ApprovalDecision.Modify,
            call with { Arguments = new JsonObject { ["command"] = "echo modified" } },
            "safer")));

        AgentState state;
        using (ToolApproval.Begin([new ApprovalPolicy(modifier, "bash(command='echo original*"), Policy(ApprovalDecision.Approve, "*")]))
        {
            state = await RunAsync();
        }

        Assert.Equal("Submitted", scope.ExitStatus);
        Assert.Contains("modified", Tool(state.Messages[3]).Text);
        Assert.DoesNotContain("original", Tool(state.Messages[3]).Text);
        Assert.Equal("echo original", Assert.IsType<ChatMessageAssistant>(state.Messages[2]).ToolCalls![0].Arguments["command"]!.GetValue<string>());
        Assert.Equal("echo modified", scope.Infos[0]["command"]!.GetValue<string>());
        Assert.Equal("modify", Assert.Single(scope.Context.Transcript.Events.OfType<ApprovalEvent>(), e => e.Call.Id == "c1").Decision);
    }

    [Fact]
    public async Task without_ambient_policies_every_call_runs_and_no_approval_is_recorded()
    {
        using var scope = new Scope(new ScriptedModelApi(Bash("echo free", "c1"), Submit("done", "c2")));

        var state = await RunAsync();

        Assert.Equal("Submitted", scope.ExitStatus);
        Assert.Contains("free", Tool(state.Messages[3]).Text);
        Assert.Empty(scope.Context.Transcript.Events.OfType<ApprovalEvent>());
    }

    [Fact]
    public async Task the_cache_replays_a_repeated_run_without_provider_calls()
    {
        using var env = new EnvVar(CacheOps.CacheDirVar, Path.Combine(Path.GetTempPath(), "inspect-mini-swe-cache", Guid.NewGuid().ToString("N")));
        var options = new MiniSweAgentOptions { Cache = new CachePolicy { Expiry = "1D" } };

        using (var first = new Scope(new ScriptedModelApi(Bash("echo hi", "c1"), Submit("done", "c2"))))
        {
            await RunAsync(options);
            Assert.Equal(2, first.Api.Requests.Count);
            Assert.All(first.Context.Transcript.Events.OfType<ModelEvent>(), e => Assert.Equal(CacheMode.Write, e.Cache));
        }

        using (var second = new Scope(new ScriptedModelApi(Bash("echo hi", "c1"), Submit("done", "c2"))))
        {
            var state = await RunAsync(options);
            Assert.Empty(second.Api.Requests);
            Assert.Equal("Submitted", second.ExitStatus);
            Assert.Equal("done\n", state.Output.Completion);
            var events = second.Context.Transcript.Events.OfType<ModelEvent>().ToList();
            Assert.Equal(2, events.Count);
            Assert.All(events, e => Assert.Equal(CacheMode.Read, e.Cache));
        }
    }

    [Fact]
    public async Task compaction_clears_old_tool_results_from_the_model_input_but_not_the_trajectory()
    {
        var big = "printf 'x%.0s' {1..12000}";
        using var scope = new Scope(new ScriptedModelApi(Bash(big, "c1"), Bash("echo two", "c2"), Submit("done", "c3")));
        var options = new MiniSweAgentOptions
        {
            Compaction = Compaction.Hook(new CompactionEdit(CompactionThreshold.FromTokens(2500), keepToolUses: 0)),
        };

        var state = await RunAsync(options);

        Assert.Equal("Submitted", scope.ExitStatus);
        Assert.Equal(3, scope.Api.Requests.Count);
        var compactedInput = scope.Api.Requests[1].Input;
        Assert.Contains(compactedInput, m => m is ChatMessageTool { ToolCallId: "c1" } tool && tool.Text.Contains(CompactionEdit.ToolResultRemoved, StringComparison.Ordinal));
        Assert.DoesNotContain(compactedInput, m => m is ChatMessageTool tool && tool.Text.Contains("xxxxxxxx", StringComparison.Ordinal));
        Assert.Contains("xxxxxxxx", Tool(state.Messages[3]).Text);   // the trajectory keeps the full observation
        var compaction = Assert.Single(scope.Context.Transcript.Events.OfType<CompactionEvent>());
        Assert.Equal("edit", compaction.Type);
        Assert.True(compaction.TokensAfter < compaction.TokensBefore, $"{compaction.TokensAfter} < {compaction.TokensBefore}");
    }

    [Fact]
    public async Task a_context_overflow_is_recovered_by_a_forced_compaction()
    {
        var big = "printf 'x%.0s' {1..12000}";
        var overflow = new ModelOutput
        {
            Model = ScriptedModelApi.DefaultModelName,
            Choices = [new ChatCompletionChoice(new ChatMessageAssistant(""), StopReason.ModelLength)],
        };
        using var scope = new Scope(new ScriptedModelApi(Bash(big, "c1"), ScriptedTurn.From(overflow), Submit("done", "c2")));
        var options = new MiniSweAgentOptions
        {
            Compaction = Compaction.Hook(new CompactionEdit(CompactionThreshold.FromTokens(100000), keepToolUses: 0)),
        };

        var state = await RunAsync(options);

        Assert.Equal("Submitted", scope.ExitStatus);
        Assert.Equal(3, scope.Api.Requests.Count);
        Assert.DoesNotContain(scope.Api.Requests[1].Input, m => m is ChatMessageTool tool && tool.Text.Contains(CompactionEdit.ToolResultRemoved, StringComparison.Ordinal));
        Assert.Contains(scope.Api.Requests[2].Input, m => m is ChatMessageTool tool && tool.Text.Contains(CompactionEdit.ToolResultRemoved, StringComparison.Ordinal));
        Assert.DoesNotContain(state.Messages, m => m is ChatMessageUser user && user.Text.Contains("output token limit", StringComparison.Ordinal));   // no format error for the overflow turn
        Assert.Equal("forced", Assert.Single(scope.Context.Transcript.Events.OfType<CompactionEvent>()).Metadata!["trigger"]!.ToString());
    }

    [Fact]
    public async Task without_compaction_a_context_overflow_still_renders_the_token_limit_format_error()
    {
        var overflow = new ModelOutput
        {
            Model = ScriptedModelApi.DefaultModelName,
            Choices = [new ChatCompletionChoice(new ChatMessageAssistant(""), StopReason.ModelLength)],
        };
        using var scope = new Scope(new ScriptedModelApi(ScriptedTurn.From(overflow), Submit("done", "c1")));

        var state = await RunAsync();

        Assert.Equal("Submitted", scope.ExitStatus);
        Assert.StartsWith("Your previous response reached the output token limit (finish_reason=length)", state.Messages[2].Text);
        Assert.Empty(scope.Context.Transcript.Events.OfType<CompactionEvent>());
    }

    private sealed class Scope : IDisposable
    {
        private readonly IDisposable _ambient;

        public Scope(ScriptedModelApi api)
        {
            Api = api;
            Model = new Model(api);
            Sandbox = new LocalSandboxEnvironment();
            Context = new SampleContext { ActiveModel = Model, Sandboxes = SandboxEnvironments.Single(Sandbox), Limits = new Limits() };
            _ambient = SampleContext.Begin(Context);
        }

        public ScriptedModelApi Api { get; }

        public Model Model { get; }

        public LocalSandboxEnvironment Sandbox { get; }

        public SampleContext Context { get; }

        public Store Store => Context.Store;

        public string? ExitStatus => Store.Get(MiniSweAgent.ExitStatusKey) as string;

        public List<JsonNode> Infos =>
            Context.Transcript.Events.OfType<InfoEvent>().Where(e => e.Source == MiniSweAgent.TranscriptSource).Select(e => e.Data!).ToList();

        public void Dispose()
        {
            _ambient.Dispose();
            Sandbox.Dispose();
        }
    }

    private sealed class EnvVar : IDisposable
    {
        private readonly string _name;

        private readonly string? _previous;

        public EnvVar(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}

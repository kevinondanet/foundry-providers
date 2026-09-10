using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Approval = InspectAzureAI.Eval.Approval.Approval;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>Port of the <c>filter</c> handling of <c>agent/_bridge/util.py</c> <c>bridge_generate</c> (lines 484-552): per-attempt filtering, refusal retries, rejection replay and grant registration.</summary>
public class GenerateFilterTests
{
    private const string Task = "Find the database password.";

    private sealed record FilterCall(Model Model, IReadOnlyList<ChatMessage> Input, IReadOnlyList<ToolInfo> Tools, ToolChoice ToolChoice, GenerateConfig Config);

    private static ApprovalPolicy Policy(Func<ToolCall, ApprovalDecision> decide, List<string>? seen = null) =>
        new(new ApproverDef("scripted", (_, call, _, _, _) =>
        {
            seen?.Add(call.Function);
            return System.Threading.Tasks.Task.FromResult(new Approval(decide(call)));
        }), "*");

    private static BridgedToolRegistry SecretsRegistry() =>
        new([new BridgedToolsSpec("secrets", [new ToolDef("secret_lookup", "Look up a secret.", new ToolParams(), (_, _) => System.Threading.Tasks.Task.FromResult<ToolResult>("hunter2"))])]);

    private static ModelOutput SecretCall() => ScriptedTurn.ToolCall("mcp__secrets__secret_lookup", new { key = "db" }, id: "call_secret").Output!;

    [Fact]
    public async Task an_output_skips_the_model_but_approval_and_tracking_still_run()
    {
        using var scope = new SampleContextScope();
        var api = new ScriptedModelApi(ScriptedTurn.Text("never"));
        var approvals = new List<string>();
        var calls = new List<FilterCall>();
        var bridge = new AgentBridge(
            new AgentState([new ChatMessageUser(Task)]),
            new Model(api),
            approval: [Policy(_ => ApprovalDecision.Approve, approvals)],
            filter: (model, input, tools, choice, config, _) =>
            {
                calls.Add(new FilterCall(model, input, tools, choice, config));
                return System.Threading.Tasks.Task.FromResult<GenerateFilterResult?>(ScriptedTurn.ToolCall("bash", new { cmd = "cat .env" }, id: "call_1", text: "from the filter").Output!);
            });

        var output = await bridge.GenerateAsync("inspect", [new ChatMessageUser(Task)], [new ToolInfo("bash", "Run a command.")], ToolChoice.Auto, new GenerateConfig());

        Assert.Equal("from the filter", output.Completion);
        Assert.Empty(api.Requests);
        Assert.Empty(scope.Transcript.Events.OfType<ModelEvent>());
        Assert.Equal(["bash"], approvals);
        var call = Assert.Single(calls);
        Assert.Same(bridge.Model, call.Model);
        Assert.Equal([Task], call.Input.Select(m => m.Text));
        Assert.Equal(["bash"], call.Tools.Select(t => t.Name));
        Assert.Equal([Task, "from the filter"], bridge.State.Messages.Select(m => m.Text));
        Assert.Equal("from the filter", bridge.State.Output.Completion);
    }

    [Fact]
    public async Task an_input_replaces_what_the_model_is_called_with()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("rewritten answer"));
        var bridge = new AgentBridge(
            new AgentState([new ChatMessageUser(Task)]),
            new Model(api),
            filter: (_, input, _, _, config, _) => System.Threading.Tasks.Task.FromResult<GenerateFilterResult?>(
                new GenerateInput([.. input, new ChatMessageUser("Answer in one word.")], [new ToolInfo("think", "Think.")], new ToolFunction("think"), config with { StopSeqs = ["STOP"] })));

        await bridge.GenerateAsync("inspect", [new ChatMessageUser(Task)], [new ToolInfo("bash", "Run a command.")], ToolChoice.Any, new GenerateConfig());

        var request = Assert.Single(api.Requests);
        Assert.Equal([Task, "Answer in one word."], request.Input.Select(m => m.Text));
        Assert.Equal(["think"], request.Tools.Select(t => t.Name));
        Assert.Equal(new ToolFunction("think"), request.ToolChoice);
        Assert.Equal(["STOP"], request.Config.StopSeqs);
        Assert.Equal([Task, "rewritten answer"], bridge.State.Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task a_null_result_generates_as_requested()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("plain"));
        var bridge = new AgentBridge(new AgentState([]), new Model(api), filter: (_, _, _, _, _, _) => System.Threading.Tasks.Task.FromResult<GenerateFilterResult?>(null));

        var output = await bridge.GenerateAsync("inspect", [new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig());

        Assert.Equal("plain", output.Completion);
        Assert.Equal(["hi"], Assert.Single(api.Requests).Input.Select(m => m.Text));
    }

    [Fact]
    public async Task a_refusal_retry_runs_the_filter_again_on_the_original_input()
    {
        var refusal = ModelOutput.FromContent("m", "I cannot help with that.", StopReason.ContentFilter);
        var api = new ScriptedModelApi(ScriptedTurn.From(refusal), ScriptedTurn.Text("Castle"));
        var inputs = new List<int>();
        var bridge = new AgentBridge(
            new AgentState([new ChatMessageUser(Task)]),
            new Model(api),
            retryRefusals: 1,
            filter: (_, input, _, _, _, _) =>
            {
                inputs.Add(input.Count);
                return System.Threading.Tasks.Task.FromResult<GenerateFilterResult?>(null);
            });

        var output = await bridge.GenerateAsync("inspect", [new ChatMessageUser(Task)], [], ToolChoice.Auto, new GenerateConfig());

        Assert.Equal("Castle", output.Completion);
        Assert.Equal([1, 1], inputs);
        Assert.Equal(2, api.Requests.Count);
    }

    [Fact]
    public async Task a_rejection_is_replayed_through_the_filter()
    {
        var api = new ScriptedModelApi(ScriptedTurn.ToolCall("bash", new { cmd = "rm -rf /" }, id: "call_1"), ScriptedTurn.Text("I will not do that."));
        var inputs = new List<IReadOnlyList<ChatMessage>>();
        var bridge = new AgentBridge(
            new AgentState([new ChatMessageUser(Task)]),
            new Model(api),
            approval: [Policy(_ => ApprovalDecision.Reject)],
            filter: (_, input, _, _, _, _) =>
            {
                inputs.Add(input);
                return System.Threading.Tasks.Task.FromResult<GenerateFilterResult?>(null);
            });

        var output = await bridge.GenerateAsync("inspect", [new ChatMessageUser(Task)], [new ToolInfo("bash", "Run a command.")], ToolChoice.Auto, new GenerateConfig());

        Assert.Equal("I will not do that.", output.Completion);
        Assert.Equal([1, 3], inputs.Select(i => i.Count));
        Assert.IsType<ChatMessageAssistant>(inputs[1][1]);
        Assert.Equal("approval", Assert.IsType<ChatMessageTool>(inputs[1][2]).Error!.Type);
        Assert.Equal([Task, "I will not do that."], bridge.State.Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task the_filter_sees_the_tools_after_the_web_search_grant()
    {
        var calls = new List<FilterCall>();
        var api = new ScriptedModelApi(ScriptedTurn.Text("ok"));
        var bridge = new AgentBridge(new AgentState([]), new Model(api), filter: (model, input, tools, choice, config, _) =>
        {
            calls.Add(new FilterCall(model, input, tools, choice, config));
            return System.Threading.Tasks.Task.FromResult<GenerateFilterResult?>(null);
        });
        var marker = BridgeBuiltinTools.WebSearchTool(new System.Text.Json.Nodes.JsonObject { ["type"] = "web_search_20250305", ["name"] = "web_search" });

        await bridge.GenerateAsync("inspect", [new ChatMessageUser("search")], [new ToolInfo("bash", "Run."), marker], new ToolFunction("web_search"), new GenerateConfig());

        var call = Assert.Single(calls);
        Assert.Equal(["bash"], call.Tools.Select(t => t.Name));
        Assert.Equal(ToolChoice.Auto, call.ToolChoice);
    }

    [Fact]
    public async Task grants_are_registered_for_a_filter_supplied_output_under_approval()
    {
        var bridge = new AgentBridge(
            new AgentState([new ChatMessageUser(Task)]),
            new Model(new ScriptedModelApi()),
            approval: [Policy(_ => ApprovalDecision.Approve)],
            filter: (_, _, _, _, _, _) => System.Threading.Tasks.Task.FromResult<GenerateFilterResult?>(SecretCall()));
        bridge.AttachBridgedTools(SecretsRegistry());

        await bridge.GenerateAsync("inspect", [new ChatMessageUser(Task)], [], ToolChoice.Auto, new GenerateConfig());

        Assert.Equal(1, bridge.BridgedTools.GrantCount);
        Assert.True(bridge.BridgedTools.ConsumeToolExecutionGrant("secrets", "secret_lookup", new System.Text.Json.Nodes.JsonObject { ["key"] = "db" }));
    }

    [Fact]
    public async Task no_grant_is_registered_for_a_rejected_filter_output()
    {
        var attempts = 0;
        var api = new ScriptedModelApi(ScriptedTurn.Text("I will ask instead."));
        var bridge = new AgentBridge(
            new AgentState([new ChatMessageUser(Task)]),
            new Model(api),
            approval: [Policy(call => call.Function.Contains("secret", StringComparison.Ordinal) ? ApprovalDecision.Reject : ApprovalDecision.Approve)],
            filter: (_, _, _, _, _, _) => System.Threading.Tasks.Task.FromResult(attempts++ == 0 ? (GenerateFilterResult?)SecretCall() : null));
        bridge.AttachBridgedTools(SecretsRegistry());

        var output = await bridge.GenerateAsync("inspect", [new ChatMessageUser(Task)], [], ToolChoice.Auto, new GenerateConfig());

        Assert.Equal("I will ask instead.", output.Completion);
        Assert.Equal(2, attempts);
        Assert.Equal(0, bridge.BridgedTools.GrantCount);
    }
}

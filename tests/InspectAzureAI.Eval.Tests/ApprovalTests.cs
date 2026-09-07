using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tests;

using Approval = InspectAzureAI.Eval.Approval.Approval;
using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;
using Solvers = InspectAzureAI.Eval.Solvers.Solvers;

/// <summary>
/// Port of <c>tests/approval/test_approval.py</c> and <c>tests/agent/test_bridge_approval.py</c>: every decision
/// through <c>ToolExecutor</c>, the human approver over a scripted prompter and the console, policy globs and
/// ordering, config files, the ambient scope, the bridge round-trip and the eval-level wiring.
/// </summary>
public sealed class ApprovalTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", "approval-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ----------------------------------------------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------------------------------------------

    /// <summary>A prompter answering from a script and keeping every request it was shown.</summary>
    private sealed class ScriptedPrompter(params ApprovalDecision[] decisions) : IApprovalPrompter
    {
        private readonly Queue<ApprovalDecision> _decisions = new(decisions);

        public List<ApprovalRequest> Requests { get; } = [];

        public Task<ApprovalDecision> PromptAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_decisions.Dequeue());
        }
    }

    /// <summary>The <c>addition</c> tool of the Python tests, returning a content list to cover that path.</summary>
    private static ToolDef Addition(Action? onRun = null) => new(
        "addition",
        "Add two numbers.",
        new ToolParams
        {
            Properties = new Dictionary<string, ToolParam>(StringComparer.Ordinal) { ["x"] = ToolParam.Of("integer"), ["y"] = ToolParam.Of("integer") },
            Required = ["x", "y"],
        },
        (args, _) =>
        {
            onRun?.Invoke();
            var sum = args["x"]!.GetValue<int>() + args["y"]!.GetValue<int>();
            return Task.FromResult(ToolResult.FromContents([new ContentText(sum.ToString(System.Globalization.CultureInfo.InvariantCulture))]));
        });

    private static ToolCall AdditionCall(string id = "test", int x = 1, int y = 1) => new(id, "addition", new JsonObject { ["x"] = x, ["y"] = y });

    private static ToolCall Call(string function, object args, string id = "1") => new(id, function, System.Text.Json.JsonSerializer.SerializeToNode(args)!.AsObject());

    private static IReadOnlyList<ChatMessage> Conversation(params ToolCall[] calls) =>
        [new ChatMessageUser("What is 1 + 1?"), new ChatMessageAssistant("Adding the numbers.", toolCalls: calls)];

    private static ApproverDef Custom(string name, Func<ToolCall, Approval> decide) =>
        new(name, (_, call, _, _, _) => Task.FromResult(decide(call)));

    private static ApprovalPolicy Policy(ApproverDef approver, string tools = "*") => new(approver, tools);

    private static ApprovalPolicy ApproveAll => Policy(Approvers.Auto());

    private static ApprovalPolicy RejectAll => Policy(Approvers.Auto(ApprovalDecision.Reject));

    private static async Task<ChatMessageTool> ExecuteAddition(IReadOnlyList<ApprovalPolicy>? approval, ToolDef? tool = null, ToolCall? call = null)
    {
        var result = await ToolExecutor.ExecuteToolsAsync(Conversation(call ?? AdditionCall()), [tool ?? Addition()], approval: approval);
        return Assert.IsType<ChatMessageTool>(Assert.Single(result.Messages));
    }

    private static ModelOutput ToolCallsOutput(params ToolCall[] calls) => ToolCallsOutput("On it.", calls);

    private static ModelOutput ToolCallsOutput(string content, params ToolCall[] calls) => new()
    {
        Model = ScriptedModelApi.DefaultModelName,
        Choices = [new ChatCompletionChoice(new ChatMessageAssistant(content, toolCalls: calls, model: ScriptedModelApi.DefaultModelName, source: "generate"), StopReason.ToolCalls)],
    };

    private const string TaskPrompt = "Tidy up the working directory.";

    private sealed record BridgeRun(ModelOutput Output, ScriptedModelApi Api, AgentBridge Bridge)
    {
        public int Generations => Api.Requests.Count;

        public IReadOnlyList<ChatMessage> Input(int generation) => Api.Requests[generation].Input;

        public List<ChatMessageTool> ToolResults(int generation) => Input(generation).OfType<ChatMessageTool>().ToList();
    }

    /// <summary>Port of <c>run_bridge</c>: drives <c>AgentBridge.GenerateAsync</c> against scripted outputs (the last one repeats).</summary>
    private static async Task<BridgeRun> RunBridge(IReadOnlyList<ModelOutput> outputs, IReadOnlyList<ApprovalPolicy>? approval = null, IReadOnlyList<ChatMessage>? input = null)
    {
        var remaining = new List<ModelOutput>(outputs);
        var api = new ScriptedModelApi(Enumerable.Range(0, outputs.Count + 4).Select(_ => ScriptedTurn.From((_, _) =>
        {
            var next = remaining.Count > 1 ? remaining[0] : remaining[^1];
            if (remaining.Count > 1)
            {
                remaining.RemoveAt(0);
            }

            return next;
        })));
        var messages = input ?? [new ChatMessageUser(TaskPrompt)];
        var bridge = new AgentBridge(new AgentState(messages.ToList()), new Model(api), approval: approval);
        var output = await bridge.GenerateAsync("inspect", messages, [], ToolChoice.Auto, new GenerateConfig());
        return new BridgeRun(output, api, bridge);
    }

    // ----------------------------------------------------------------------------------------------------------
    // ToolExecutor: every decision through a scripted (human) prompter
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task approve_runs_the_tool_and_records_the_decision()
    {
        using var scope = new SampleContextScope();
        var prompter = new ScriptedPrompter(ApprovalDecision.Approve);
        var ran = false;

        var message = await ExecuteAddition([Policy(Approvers.Human(prompter: prompter))], Addition(() => ran = true));

        Assert.True(ran);
        Assert.Null(message.Error);
        Assert.Equal("2", message.Text);
        Assert.Equal("test", message.ToolCallId);

        var request = Assert.Single(prompter.Requests);
        Assert.Equal("Adding the numbers.", request.Message);
        Assert.Equal(HumanApprovals.DefaultChoices, request.Choices);
        Assert.Equal("addition", request.Call.Function);
        Assert.IsType<ChatMessageAssistant>(request.History[^1]);
        Assert.Equal("```python\naddition(x=1, y=1)\n```\n", request.View.Call!.Content);
        Assert.Equal("markdown", request.View.Call.Format);

        var approval = Assert.Single(scope.Transcript.Events.OfType<ApprovalEvent>());
        Assert.Equal("human", approval.Approver);
        Assert.Equal("approve", approval.Decision);
        Assert.Equal(HumanApprovals.Approved, approval.Explanation);
        Assert.Equal("Adding the numbers.", approval.Message);
        Assert.Equal("test", approval.Call.Id);
        Assert.NotNull(approval.View);
        var toolEvent = Assert.Single(scope.Transcript.Events.OfType<ToolEvent>());
        Assert.Null(toolEvent.Error);
    }

    [Fact]
    public async Task reject_reports_an_approval_error_without_running_the_tool()
    {
        using var scope = new SampleContextScope();
        var ran = false;

        var message = await ExecuteAddition([Policy(Approvers.Human(prompter: new ScriptedPrompter(ApprovalDecision.Reject)))], Addition(() => ran = true));

        Assert.False(ran);
        Assert.NotNull(message.Error);
        Assert.Equal("approval", message.Error.Type);
        Assert.Equal(HumanApprovals.Rejected, message.Error.Message);
        Assert.Equal("", message.Text);
        var toolEvent = Assert.Single(scope.Transcript.Events.OfType<ToolEvent>());
        Assert.Equal("test", toolEvent.Id);
        Assert.Equal("approval", toolEvent.Error!.Type);
        Assert.Equal("reject", Assert.Single(scope.Transcript.Events.OfType<ApprovalEvent>()).Decision);
    }

    [Fact]
    public async Task terminate_ends_the_sample_after_recording_the_tool_event()
    {
        using var scope = new SampleContextScope();
        var ran = false;

        var ex = await Assert.ThrowsAsync<TerminateSampleException>(() =>
            ExecuteAddition([Policy(Approvers.Human(prompter: new ScriptedPrompter(ApprovalDecision.Terminate)))], Addition(() => ran = true)));

        Assert.False(ran);
        Assert.Equal("Tool call approver requested termination.", ex.Reason);
        var toolEvent = Assert.Single(scope.Transcript.Events.OfType<ToolEvent>());
        Assert.Equal("addition", toolEvent.Function);
        Assert.True(toolEvent.Failed);   // Python: TerminateSampleError takes the unhandled-exception path, failed=True
        Assert.NotNull(toolEvent.Completed);
        var approval = Assert.Single(scope.Transcript.Events.OfType<ApprovalEvent>());
        Assert.Equal("terminate", approval.Decision);
        Assert.Equal(HumanApprovals.Terminated, approval.Explanation);
    }

    [Fact]
    public async Task escalate_hands_the_call_to_the_next_policy()
    {
        using var scope = new SampleContextScope();
        var prompter = new ScriptedPrompter(ApprovalDecision.Escalate);
        var human = Approvers.Human([ApprovalDecision.Approve, ApprovalDecision.Reject, ApprovalDecision.Escalate], prompter);

        var message = await ExecuteAddition([Policy(human, "add*"), Policy(Approvers.Auto(), "add*")]);

        Assert.Null(message.Error);
        Assert.Equal("2", message.Text);
        var decisions = scope.Transcript.Events.OfType<ApprovalEvent>().Select(e => (e.Approver, e.Decision, e.Explanation)).ToArray();
        Assert.Equal([("human", "escalate", HumanApprovals.Escalated), ("auto", "approve", Approvers.AutomaticDecision)], decisions);
    }

    [Fact]
    public async Task modify_runs_the_tool_with_the_modified_call_but_records_the_original()
    {
        using var scope = new SampleContextScope();
        JsonObject? received = null;
        var tool = Addition() with { Execute = (args, _) => { received = args; return Task.FromResult<ToolResult>("modified"); } };
        var modifier = Custom("modifier", call => new Approval(ApprovalDecision.Modify, Modified: call with { Arguments = new JsonObject { ["x"] = 2, ["y"] = 3 } }));

        var message = await ExecuteAddition([Policy(modifier)], tool);

        Assert.Null(message.Error);
        Assert.Equal("modified", message.Text);
        Assert.Equal("test", message.ToolCallId);
        Assert.Equal(2, received!["x"]!.GetValue<int>());
        Assert.Equal(3, received["y"]!.GetValue<int>());
        var toolEvent = Assert.Single(scope.Transcript.Events.OfType<ToolEvent>());
        Assert.Equal(1, toolEvent.Arguments["x"]!.GetValue<int>());
        var approval = Assert.Single(scope.Transcript.Events.OfType<ApprovalEvent>());
        Assert.Equal("modify", approval.Decision);
        Assert.Equal(1, approval.Call.Arguments["x"]!.GetValue<int>());
        Assert.Equal(2, approval.Modified!.Arguments["x"]!.GetValue<int>());
    }

    [Fact]
    public async Task auto_reject_uses_the_automatic_explanation()
    {
        var message = await ExecuteAddition([RejectAll]);

        Assert.Equal("approval", message.Error!.Type);
        Assert.Equal(Approvers.AutomaticDecision, message.Error.Message);
    }

    [Fact]
    public async Task an_unmatched_tool_is_rejected_by_the_policy_approver()
    {
        using var scope = new SampleContextScope();

        var message = await ExecuteAddition([Policy(Approvers.Auto(), "foo*")]);

        Assert.Equal("approval", message.Error!.Type);
        Assert.Equal("No approvers registered for tool addition", message.Error.Message);
        var approval = Assert.Single(scope.Transcript.Events.OfType<ApprovalEvent>());
        Assert.Equal(ApprovalPolicies.PolicyApproverName, approval.Approver);
        Assert.Equal("reject", approval.Decision);
    }

    [Fact]
    public async Task an_escalation_with_no_fallback_is_rejected_with_no_approval_granted()
    {
        using var scope = new SampleContextScope();

        var message = await ExecuteAddition([Policy(Approvers.Auto(ApprovalDecision.Escalate), "add*")]);

        Assert.Equal("No approval granted for tool addition", message.Error!.Message);
        Assert.Equal([("auto", "escalate"), ("policy", "reject")], scope.Transcript.Events.OfType<ApprovalEvent>().Select(e => (e.Approver, e.Decision)).ToArray());
    }

    [Fact]
    public async Task an_empty_policy_list_leaves_approval_off()
    {
        Assert.False(ToolApproval.HaveToolApproval);

        var message = await ExecuteAddition([]);

        Assert.Null(message.Error);
        Assert.Equal("2", message.Text);
    }

    [Fact]
    public async Task the_approval_parameter_replaces_ambient_policies_for_the_call_only()
    {
        using var ambient = ToolApproval.Begin([ApproveAll]);

        var rejected = await ExecuteAddition([RejectAll]);
        var approved = await ExecuteAddition(null);

        Assert.Equal("approval", rejected.Error!.Type);
        Assert.Null(approved.Error);
        Assert.True(ToolApproval.HaveToolApproval);
    }

    [Fact]
    public async Task approver_inference_is_exempt_from_token_and_turn_limits()
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("yes", new ModelUsage(5, 1, 6))));
        var tokens = new TokenLimit(1);
        var turns = new TurnLimit(1);
        using var limits = Limit.Apply(tokens, turns);
        var generating = new ApproverDef("generating", async (_, _, _, _, ct) =>
        {
            await scope.Model.GenerateAsync("Should this call be approved?", cancellationToken: ct);
            return new Approval(ApprovalDecision.Approve);
        });

        var message = await ExecuteAddition([Policy(generating)]);

        Assert.Null(message.Error);
        Assert.Single(scope.Api.Requests);
        Assert.Equal(0, tokens.Usage);
        Assert.Equal(0, turns.Turns);
    }

    [Fact]
    public async Task a_failing_viewer_falls_back_to_the_default_rendering()
    {
        var prompter = new ScriptedPrompter(ApprovalDecision.Approve, ApprovalDecision.Approve);
        var failing = Addition() with { Viewer = _ => throw new InvalidOperationException("viewer broke") };
        var partial = Addition() with { Viewer = _ => new ToolCallView { Context = new ToolCallContent("text", "page snippet") } };

        await ExecuteAddition([Policy(Approvers.Human(prompter: prompter))], failing);
        await ExecuteAddition([Policy(Approvers.Human(prompter: prompter))], partial);

        Assert.Equal("```python\naddition(x=1, y=1)\n```\n", prompter.Requests[0].View.Call!.Content);
        Assert.Null(prompter.Requests[0].View.Context);
        Assert.Equal("page snippet", prompter.Requests[1].View.Context!.Content);
        Assert.Equal("```python\naddition(x=1, y=1)\n```\n", prompter.Requests[1].View.Call!.Content);
    }

    [Fact]
    public async Task a_prompter_answering_modify_is_an_error_for_the_human_approver()
    {
        var human = Approvers.Human([ApprovalDecision.Approve, ApprovalDecision.Modify], new ScriptedPrompter(ApprovalDecision.Modify));

        await Assert.ThrowsAsync<NotSupportedException>(() => ExecuteAddition([Policy(human)]));
    }

    [Fact]
    public void the_human_approver_needs_at_least_one_choice()
    {
        Assert.Throws<ArgumentException>(() => Approvers.Human([]));
    }

    [Fact]
    public async Task the_human_approver_uses_the_default_prompter_when_none_is_given()
    {
        var previous = Approvers.DefaultPrompter;
        try
        {
            var prompter = new ScriptedPrompter(ApprovalDecision.Reject);
            Approvers.DefaultPrompter = prompter;

            var message = await ExecuteAddition([Policy(Approvers.Human())]);

            Assert.Equal(HumanApprovals.Rejected, message.Error!.Message);
            Assert.Single(prompter.Requests);
        }
        finally
        {
            Approvers.DefaultPrompter = previous;
        }
    }

    // ----------------------------------------------------------------------------------------------------------
    // console prompter
    // ----------------------------------------------------------------------------------------------------------

    private static ApprovalRequest ConsoleRequest(IReadOnlyList<ApprovalDecision>? choices = null, ToolCallView? view = null)
    {
        var call = AdditionCall();
        return new ApprovalRequest("Adding the numbers.", call, view ?? ToolCallViews.Default(call), Conversation(call), choices ?? HumanApprovals.DefaultChoices);
    }

    [Fact]
    public async Task console_prompter_reprompts_on_an_invalid_answer_and_reads_a_letter()
    {
        var output = new StringWriter();
        var prompter = new ConsoleApprovalPrompter(new StringReader("x\nr\n"), output);

        var decision = await prompter.PromptAsync(ConsoleRequest(), CancellationToken.None);

        Assert.Equal(ApprovalDecision.Reject, decision);
        var text = output.ToString();
        Assert.Contains(ConsoleApprovalPrompter.Title, text);
        Assert.Contains("addition(x=1, y=1)", text);
        Assert.Contains("Approve (a), Reject (r), or Terminate (t) [a/r/t] (a): ", text);
        Assert.Contains(ConsoleApprovalPrompter.InvalidChoice, text);
        Assert.Contains("Decision: Reject", text);
    }

    [Fact]
    public async Task console_prompter_takes_approve_on_an_empty_line()
    {
        var prompter = new ConsoleApprovalPrompter(new StringReader("\n"), new StringWriter());

        Assert.Equal(ApprovalDecision.Approve, await prompter.PromptAsync(ConsoleRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task console_prompter_end_of_input_is_an_error()
    {
        var prompter = new ConsoleApprovalPrompter(new StringReader(""), new StringWriter());

        await Assert.ThrowsAsync<EndOfStreamException>(() => prompter.PromptAsync(ConsoleRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task console_prompter_cannot_offer_modify()
    {
        var prompter = new ConsoleApprovalPrompter(new StringReader("a\n"), new StringWriter());

        await Assert.ThrowsAsync<NotSupportedException>(() => prompter.PromptAsync(ConsoleRequest([ApprovalDecision.Approve, ApprovalDecision.Modify]), CancellationToken.None));
    }

    [Fact]
    public void console_render_substitutes_placeholders_from_the_arguments()
    {
        var view = new ToolCallView
        {
            Context = new ToolCallContent("text", "adding {{x}} and {{y}} ({{missing}})") { Title = "Sum of {{x}}" },
            Call = new ToolCallContent("markdown", "**add**"),
        };

        var rendered = ConsoleApprovalPrompter.Render("Adding.", view, new JsonObject { ["x"] = 1, ["y"] = true });

        Assert.Contains("Sum of 1", rendered);
        Assert.Contains("adding 1 and True ({{missing}})", rendered);
        Assert.Contains("Assistant\nAdding.", rendered);
        Assert.Contains("**add**", rendered);
    }

    // ----------------------------------------------------------------------------------------------------------
    // policy globs and ordering
    // ----------------------------------------------------------------------------------------------------------

    public static TheoryData<string, string, object, bool> Globs => new()
    {
        { "add*", "addition", new { x = 1 }, true },
        { "addition", "addition", new { x = 1 }, true },
        { "add?tion", "addition", new { x = 1 }, true },
        { "foo*", "addition", new { x = 1 }, false },
        { "*", "anything", new { }, true },
        { "web_browser_type", "web_browser_type_submit", new { element_id = 3 }, true },
        { "computer(action='key'", "computer", new { action = "key", text = "Return" }, true },
        { "computer(action='key'", "computer", new { action = "type", text = "hi" }, false },
    };

    [Theory]
    [MemberData(nameof(Globs))]
    public void policy_globs_prefix_match_the_rendered_call(string spec, string function, object args, bool expected)
    {
        Assert.Equal(expected, ApprovalPolicies.Matches(ApprovalPolicies.Globs([spec]), Call(function, args)));
    }

    [Fact]
    public async Task comma_separated_and_listed_tool_specs_both_match()
    {
        var single = await ExecuteAddition([Policy(Approvers.Auto(), "web_browser*, addition, python")]);
        var listed = await ExecuteAddition([new ApprovalPolicy(Approvers.Auto(), ["web_browser*", "addition, python"])]);
        var neither = await ExecuteAddition([new ApprovalPolicy(Approvers.Auto(), ["web_browser*", "python"])]);

        Assert.Null(single.Error);
        Assert.Null(listed.Error);
        Assert.Equal("No approvers registered for tool addition", neither.Error!.Message);
        Assert.Equal(["web_browser*", "addition*", "python*"], ApprovalPolicies.Globs(["web_browser*", " addition, python, "]));
    }

    [Fact]
    public async Task the_first_matching_policy_decides_and_escalation_continues_in_order()
    {
        using var scope = new SampleContextScope();

        var first = await ExecuteAddition([Policy(Approvers.Auto(ApprovalDecision.Reject), "add*"), Policy(Approvers.Auto(), "*")]);
        var skipped = await ExecuteAddition([Policy(Approvers.Auto(ApprovalDecision.Reject), "foo*"), Policy(Approvers.Auto(), "add*")]);
        var escalated = await ExecuteAddition([Policy(Approvers.Auto(ApprovalDecision.Escalate)), Policy(Approvers.Auto(ApprovalDecision.Reject)), Policy(Approvers.Auto())]);

        Assert.Equal("approval", first.Error!.Type);
        Assert.Null(skipped.Error);
        Assert.Equal("approval", escalated.Error!.Type);
        Assert.Equal(["reject", "approve", "escalate", "reject"], scope.Transcript.Events.OfType<ApprovalEvent>().Select(e => e.Decision).ToArray());
    }

    // ----------------------------------------------------------------------------------------------------------
    // config files
    // ----------------------------------------------------------------------------------------------------------

    private const string ApproveJson = """
        {"approvers": [
          {"name": "auto", "tools": "foo*", "decision": "reject"},
          {"name": "auto", "tools": "*", "decision": "escalate"},
          {"name": "auto", "tools": ["foo*", "add*"], "decision": "approve"}
        ]}
        """;

    [Fact]
    public void config_parses_the_python_structure_with_unknown_keys_as_params()
    {
        var config = ApprovalPolicies.ReadConfig(ApproveJson);

        Assert.Equal(3, config.Approvers.Count);
        Assert.Equal([("auto", true, "reject"), ("auto", true, "escalate"), ("auto", false, "approve")],
            config.Approvers.Select(a => (a.Name, a.ToolsAsString, a.Params["decision"]!.GetValue<string>())).ToArray());
        Assert.Equal(["foo*", "add*"], config.Approvers[2].ToolSpecs);
        Assert.Equal("*", config.Approvers[1].ToolSpecs.Single());
    }

    [Fact]
    public void explicit_params_merge_with_unknown_keys_and_unknown_keys_win()
    {
        var entry = ApproverPolicyConfig.FromJson(JsonNode.Parse("""{"name": "auto", "tools": "*", "params": {"decision": "approve", "other": 1}, "decision": "reject"}""")!.AsObject());

        Assert.Equal("reject", entry.Params["decision"]!.GetValue<string>());
        Assert.Equal(1, entry.Params["other"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("""{"approvers": [{"tools": "*"}]}""")]
    [InlineData("""{"approvers": [{"name": "auto", "tools": 3}]}""")]
    [InlineData("""{"approvers": [{"name": "auto", "tools": ["a", 3]}]}""")]
    [InlineData("""{"approvers": [{"name": "auto"}]}""")]
    [InlineData("""{"approvers": ["auto"]}""")]
    [InlineData("""{"approvers": {"name": "auto"}}""")]
    [InlineData("""{"policies": []}""")]
    [InlineData("""{"approvers": [""")]
    public void invalid_configs_are_rejected(string json)
    {
        Assert.Throws<ArgumentException>(() => ApprovalPolicies.ReadConfig(json));
    }

    [Fact]
    public void yaml_configs_are_not_supported()
    {
        var ex = Assert.Throws<NotSupportedException>(() => ApprovalPolicies.ReadConfig("approvers:\n  - name: human\n    tools: \"*\"\n"));

        Assert.Contains("YAML is not supported", ex.Message);
    }

    [Fact]
    public void unknown_approvers_and_parameters_in_a_config_are_errors()
    {
        Assert.Throws<ArgumentException>(() => ApprovalPolicies.FromConfig(ApprovalPolicies.ReadConfig("""{"approvers": [{"name": "nope", "tools": "*"}]}""")));
        Assert.Throws<ArgumentException>(() => ApprovalPolicies.FromConfig(ApprovalPolicies.ReadConfig("""{"approvers": [{"name": "auto", "tools": "*", "bogus": 1}]}""")));
        Assert.Throws<ArgumentException>(() => ApprovalPolicies.FromConfig(ApprovalPolicies.ReadConfig("""{"approvers": [{"name": "auto", "tools": "*", "decision": "maybe"}]}""")));
        Assert.Throws<ArgumentException>(() => ApprovalPolicies.FromConfig(ApprovalPolicies.ReadConfig("""{"approvers": [{"name": "human", "tools": "*", "choices": "approve"}]}""")));
    }

    [Fact]
    public void from_file_reads_json_by_path_and_file_uri_and_reports_a_missing_file()
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "approve.json");
        File.WriteAllText(path, ApproveJson);

        var byPath = ApprovalPolicies.FromFile(path);
        var byUri = ApprovalPolicies.FromFile(new Uri(path).AbsoluteUri);

        Assert.Equal([true, true, false], byPath.Select(p => p.ToolsAsString));
        Assert.Equal(["foo*", "add*"], byUri[2].Tools);
        Assert.Equal(["auto", "auto", "auto"], byUri.Select(p => p.Approver.Name));
        Assert.Throws<FileNotFoundException>(() => ApprovalPolicies.FromFile(Path.Combine(_tempDir, "missing.json")));
    }

    [Fact]
    public async Task a_config_file_drives_the_decision_like_python()
    {
        Directory.CreateDirectory(_tempDir);
        var approve = Path.Combine(_tempDir, "approve.json");
        File.WriteAllText(approve, ApproveJson);
        var reject = Path.Combine(_tempDir, "reject.json");
        File.WriteAllText(reject, ApproveJson.Replace("\"decision\": \"approve\"", "\"decision\": \"reject\"", StringComparison.Ordinal));
        var escalate = Path.Combine(_tempDir, "escalate.json");
        File.WriteAllText(escalate, """{"approvers": [{"name": "auto", "tools": "foo*", "decision": "reject"}, {"name": "auto", "tools": "add*", "decision": "escalate"}]}""");

        var approved = await ExecuteAddition(ApprovalOption.FromSpec(approve).Resolve());
        var rejected = await ExecuteAddition(ApprovalPolicies.FromFile(reject));
        var escalated = await ExecuteAddition(ApprovalPolicies.FromFile(escalate));

        Assert.Null(approved.Error);
        Assert.Equal(Approvers.AutomaticDecision, rejected.Error!.Message);
        Assert.Equal("No approval granted for tool addition", escalated.Error!.Message);
    }

    [Fact]
    public void resolve_accepts_a_registered_approver_name_or_a_file_and_rejects_anything_else()
    {
        var human = Assert.Single(ApprovalPolicies.Resolve("human"));
        Assert.Equal("human", human.Approver.Name);
        Assert.Equal(["*"], human.Tools);
        Assert.True(human.ToolsAsString);
        Assert.Throws<ArgumentException>(() => ApprovalPolicies.Resolve("no-such-approver"));
        Assert.Throws<ArgumentException>(() => ApprovalOption.FromSpec("no-such-approver").Resolve());
    }

    [Fact]
    public void custom_approvers_register_by_name_for_policy_files()
    {
        ApproverRegistry.Register("evaltools/allowlist", parameters =>
            Custom("evaltools/allowlist", _ => new Approval(parameters["allow"]!.GetValue<bool>() ? ApprovalDecision.Approve : ApprovalDecision.Reject)));

        var policies = ApprovalPolicies.FromConfig(ApprovalPolicies.ReadConfig("""{"approvers": [{"name": "evaltools/allowlist", "tools": "bash", "allow": true}]}"""));

        var policy = Assert.Single(policies);
        Assert.Equal("evaltools/allowlist", policy.Approver.Name);
        Assert.Equal(["bash"], policy.Tools);
        Assert.True(ApproverRegistry.IsRegistered("evaltools/allowlist"));
    }

    [Fact]
    public void to_config_records_names_tools_and_explicit_params()
    {
        var config = ApprovalPolicies.ToConfig(
        [
            RejectAll,
            new ApprovalPolicy(Approvers.Human([ApprovalDecision.Approve, ApprovalDecision.Reject]), ["foo*", "add*"]),
            Policy(Approvers.Auto()),
            Policy(Approvers.Human()),
        ]);

        var expected = JsonNode.Parse("""
            {"approvers": [
              {"name": "auto", "tools": "*", "params": {"decision": "reject"}},
              {"name": "human", "tools": ["foo*", "add*"], "params": {"choices": ["approve", "reject"]}},
              {"name": "auto", "tools": "*", "params": {}},
              {"name": "human", "tools": "*", "params": {}}
            ]}
            """);
        Assert.True(JsonNode.DeepEquals(expected, config.ToJson()), config.ToJson().ToJsonString());
        Assert.True(JsonNode.DeepEquals(config.ToJson(), ApprovalPolicies.ToConfig(ApprovalPolicies.FromConfig(config)).ToJson()));
    }

    [PythonFact]
    public void to_config_matches_pydantic_model_dump()
    {
        var config = ApprovalPolicies.ToConfig(
        [
            RejectAll,
            new ApprovalPolicy(Approvers.Human([ApprovalDecision.Approve, ApprovalDecision.Reject]), ["foo*", "add*"]),
            Policy(Approvers.Auto()),
        ]);

        var reference = PythonReference.Run("""
            import json
            from inspect_ai.approval import ApprovalPolicy, auto_approver, human_approver
            from inspect_ai.approval._policy import config_from_approval_policies
            config = config_from_approval_policies([
                ApprovalPolicy(auto_approver("reject"), "*"),
                ApprovalPolicy(human_approver(["approve", "reject"]), ["foo*", "add*"]),
                ApprovalPolicy(auto_approver(), "*"),
            ])
            print(json.dumps(config.model_dump()))
            """);

        Assert.Equal(reference, PythonJson.Dumps(config.ToJson()));
    }

    // ----------------------------------------------------------------------------------------------------------
    // ambient scope
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void approval_scopes_nest_and_restore()
    {
        Assert.False(ToolApproval.HaveToolApproval);
        using (ToolApproval.Begin([ApproveAll]))
        {
            var outer = ToolApproval.Current;
            Assert.NotNull(outer);
            using (ToolApproval.Begin([RejectAll]))
            {
                Assert.NotNull(ToolApproval.Current);
                Assert.NotSame(outer, ToolApproval.Current);
            }

            Assert.Same(outer, ToolApproval.Current);
            using (ToolApproval.BeginIfAny([]))
            {
                Assert.Same(outer, ToolApproval.Current);
            }

            using (ToolApproval.Init(null))
            {
                Assert.False(ToolApproval.HaveToolApproval);
            }

            Assert.Same(outer, ToolApproval.Current);
        }

        Assert.False(ToolApproval.HaveToolApproval);
    }

    [Fact]
    public async Task an_explicitly_empty_policy_scope_rejects_everything()
    {
        using var scope = ToolApproval.Begin([]);

        var message = await ExecuteAddition(null);

        Assert.Equal("No approvers registered for tool addition", message.Error!.Message);
    }

    // ----------------------------------------------------------------------------------------------------------
    // agent bridge
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task no_policy_leaves_bridged_output_untouched()
    {
        var call = Call("bash", new { cmd = "ls" });

        var run = await RunBridge([ToolCallsOutput(call)]);

        Assert.Equal(1, run.Generations);
        Assert.Equal([call], run.Output.Message.ToolCalls);
    }

    [Fact]
    public async Task an_approved_bridged_call_passes_through_and_the_approver_sees_the_turn_under_review()
    {
        var seen = new List<(string Message, ToolCall Call, IReadOnlyList<ChatMessage> History)>();
        var recording = new ApproverDef("recording", (message, call, _, history, _) =>
        {
            seen.Add((message, call, history.ToList()));
            return Task.FromResult(new Approval(ApprovalDecision.Approve));
        });
        var first = Call("read_file", new { path = "a.txt" }, "1");
        var second = Call("bash", new { cmd = "ls" }, "2");

        var run = await RunBridge([ToolCallsOutput("Listing the directory.", first, second)], [Policy(recording)]);

        Assert.Equal(1, run.Generations);
        Assert.Equal([first, second], run.Output.Message.ToolCalls);
        Assert.Equal(2, seen.Count);
        Assert.Equal("Listing the directory.", seen[0].Message);
        Assert.Equal([TaskPrompt, "Listing the directory."], seen[0].History.Select(m => m.Text));
        Assert.Equal([first, second], Assert.IsType<ChatMessageAssistant>(seen[0].History[^1]).ToolCalls);
        Assert.Equal([TaskPrompt, "Listing the directory."], run.Bridge.State.Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task a_rejected_bridged_call_is_replayed_to_the_model_and_hidden_from_the_scaffold()
    {
        using var scope = new SampleContextScope();
        var rejected = Call("bash", new { cmd = "rm -rf /" });

        var run = await RunBridge([ToolCallsOutput(rejected), ModelOutput.FromContent(ScriptedModelApi.DefaultModelName, "Never mind.")], [RejectAll]);

        Assert.Equal(2, run.Generations);
        Assert.Null(run.Output.Message.ToolCalls);
        Assert.Equal("Never mind.", run.Output.Completion);
        var replayed = run.Input(1);
        Assert.Equal([TaskPrompt, "On it.", ""], replayed.Select(m => m.Text));
        Assert.Equal([rejected], Assert.IsType<ChatMessageAssistant>(replayed[1]).ToolCalls);
        var result = Assert.Single(run.ToolResults(1));
        Assert.Equal("approval", result.Error!.Type);
        Assert.Equal(Approvers.AutomaticDecision, result.Error.Message);
        Assert.Equal(rejected.Id, result.ToolCallId);
        // the scaffold's conversation and the tracked state never contain the rejected call
        Assert.Equal([TaskPrompt, "Never mind."], run.Bridge.State.Messages.Select(m => m.Text));
        Assert.Equal("reject", Assert.Single(scope.Transcript.Events.OfType<ApprovalEvent>()).Decision);
    }

    [Fact]
    public async Task a_rejection_gives_every_call_a_result_naming_the_culprit()
    {
        var innocent = Call("read_file", new { path = "a.txt" }, "1");
        var rejected = Call("bash", new { cmd = "rm -rf /" }, "2");
        var rejecting = Custom("rejecting", call => new Approval(call.Function == "bash" ? ApprovalDecision.Reject : ApprovalDecision.Approve, Explanation: "Command is not permitted."));

        var run = await RunBridge([ToolCallsOutput(innocent, rejected), ModelOutput.FromContent(ScriptedModelApi.DefaultModelName, "ok")], [Policy(rejecting)]);

        var results = run.ToolResults(1);
        Assert.Equal(["1", "2"], results.Select(r => r.ToolCallId));
        Assert.Equal("This tool call was not executed because a parallel tool call in the same response was rejected: bash(cmd='rm -rf /') — Command is not permitted. This call was not itself rejected; you may re-issue it without the rejected call.", results[0].Error!.Message);
        Assert.Equal("Command is not permitted. The other tool call in this response was not executed as a result.", results[1].Error!.Message);
    }

    [Fact]
    public async Task a_rejection_short_circuits_sibling_evaluation_and_bounds_the_description()
    {
        var seen = new List<string>();
        var rejecting = Custom("rejecting", call =>
        {
            seen.Add(call.Function);
            return new Approval(call.Function == "bash" ? ApprovalDecision.Reject : ApprovalDecision.Approve, Explanation: "no");
        });
        var huge = Call("bash", new { cmd = new string('x', 1000) }, "1");
        var later = Call("read_file", new { path = "a.txt" }, "2");

        var run = await RunBridge([ToolCallsOutput(huge, later), ModelOutput.FromContent(ScriptedModelApi.DefaultModelName, "ok")], [Policy(rejecting)]);

        Assert.Equal(["bash"], seen);
        var collateral = run.ToolResults(1)[1].Error!.Message;
        Assert.Contains("...", collateral);
        Assert.True(collateral.Length < 500, collateral.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(BridgeApproval.MaxCallDescription + 3, BridgeApproval.DescribeCall(huge).Length);
    }

    [Fact]
    public async Task three_consecutive_rejections_terminate_the_sample()
    {
        var call = Call("bash", new { cmd = "ls" });

        var ex = await Assert.ThrowsAsync<TerminateSampleException>(() => RunBridge([ToolCallsOutput(call)], [RejectAll]));

        Assert.Equal($"Tool call approver rejected {BridgeApproval.MaxConsecutiveRejections} consecutive generations from the bridged agent.", ex.Reason);
    }

    [Fact]
    public async Task successive_rejections_accumulate_in_the_replayed_input()
    {
        var call = Call("bash", new { cmd = "ls" });
        var outputs = new List<ModelOutput> { ToolCallsOutput(call), ToolCallsOutput(call), ModelOutput.FromContent(ScriptedModelApi.DefaultModelName, "ok") };

        var run = await RunBridge(outputs, [RejectAll]);

        Assert.Equal(3, run.Generations);
        Assert.Single(run.Input(0));
        Assert.Equal(3, run.Input(1).Count);
        Assert.Equal(5, run.Input(2).Count);
    }

    [Fact]
    public async Task a_terminate_decision_terminates_immediately()
    {
        var ex = await Assert.ThrowsAsync<TerminateSampleException>(() =>
            RunBridge([ToolCallsOutput(Call("bash", new { cmd = "ls" }))], [Policy(Approvers.Auto(ApprovalDecision.Terminate))]));

        Assert.Equal($"Tool call approver requested termination: {Approvers.AutomaticDecision}", ex.Reason);
    }

    [Fact]
    public async Task modify_rewrites_the_arguments_handed_to_the_scaffold_but_not_the_recorded_call()
    {
        using var scope = new SampleContextScope();
        var call = Call("bash", new { cmd = "rm -rf /" });
        var modifying = Custom("modifying", c => new Approval(ApprovalDecision.Modify, Modified: c with { Function = "other", Arguments = new JsonObject { ["cmd"] = "ls" } }));

        var run = await RunBridge([ToolCallsOutput(call)], [Policy(modifying)]);

        var handed = Assert.Single(run.Output.Message.ToolCalls!);
        Assert.Equal("bash", handed.Function);
        Assert.Equal("ls", handed.Arguments["cmd"]!.GetValue<string>());
        var approval = Assert.Single(scope.Transcript.Events.OfType<ApprovalEvent>());
        Assert.Equal("rm -rf /", approval.Call.Arguments["cmd"]!.GetValue<string>());
        Assert.Equal("ls", approval.Modified!.Arguments["cmd"]!.GetValue<string>());
    }

    [Fact]
    public async Task modify_is_discarded_when_a_sibling_is_rejected()
    {
        var modified = Call("write_file", new { path = "a.txt", content = "x" }, "1");
        var rejected = Call("bash", new { cmd = "rm -rf /" }, "2");
        var approver = Custom("mixed", c => c.Function == "bash"
            ? new Approval(ApprovalDecision.Reject, Explanation: "no")
            : new Approval(ApprovalDecision.Modify, Modified: c with { Arguments = new JsonObject { ["path"] = "b.txt", ["content"] = "x" } }));

        var run = await RunBridge([ToolCallsOutput(modified, rejected), ModelOutput.FromContent(ScriptedModelApi.DefaultModelName, "ok")], [Policy(approver)]);

        var replayed = Assert.IsType<ChatMessageAssistant>(run.Input(1)[1]);
        Assert.Equal("a.txt", replayed.ToolCalls![0].Arguments["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task bridge_policies_replace_ambient_ones_and_ambient_ones_reach_a_bridged_agent()
    {
        var call = Call("bash", new { cmd = "ls" });
        using var ambient = ToolApproval.Begin([RejectAll]);

        var replaced = await RunBridge([ToolCallsOutput(call)], [ApproveAll]);
        var inherited = await RunBridge([ToolCallsOutput(call), ModelOutput.FromContent(ScriptedModelApi.DefaultModelName, "ok")]);

        Assert.Equal(1, replaced.Generations);
        Assert.Equal(2, inherited.Generations);
    }

    [Fact]
    public async Task escalation_falls_through_and_an_unmatched_tool_is_rejected_by_the_policy_approver()
    {
        var call = Call("bash", new { cmd = "ls" });

        var escalated = await RunBridge([ToolCallsOutput(call)], [Policy(Approvers.Auto(ApprovalDecision.Escalate), "bash"), Policy(Approvers.Auto(), "*")]);
        var unmatched = await RunBridge([ToolCallsOutput(call), ModelOutput.FromContent(ScriptedModelApi.DefaultModelName, "ok")], [Policy(Approvers.Auto(), "python")]);

        Assert.Equal(1, escalated.Generations);
        Assert.Equal("No approvers registered for tool bash", Assert.Single(unmatched.ToolResults(1)).Error!.Message);
    }

    [Fact]
    public async Task a_multi_choice_response_is_reduced_to_the_primary_choice_only_under_approval()
    {
        var primary = new ChatCompletionChoice(new ChatMessageAssistant("one", toolCalls: [Call("bash", new { cmd = "ls" })]), StopReason.ToolCalls);
        var withCalls = new ModelOutput { Model = "m", Choices = [primary, new ChatCompletionChoice(new ChatMessageAssistant("two", toolCalls: [Call("bash", new { cmd = "pwd" }, "2")]), StopReason.ToolCalls)] };
        var textOnly = new ModelOutput { Model = "m", Choices = [primary, new ChatCompletionChoice(new ChatMessageAssistant("two"))] };

        Assert.Single((await RunBridge([withCalls], [ApproveAll])).Output.Choices);
        Assert.Equal(2, (await RunBridge([textOnly], [ApproveAll])).Output.Choices.Count);
        Assert.Equal(2, (await RunBridge([withCalls])).Output.Choices.Count);
    }

    [Fact]
    public async Task refusal_retry_still_works_alongside_approval()
    {
        var refusal = new ModelOutput { Model = "m", Choices = [new ChatCompletionChoice(new ChatMessageAssistant("no"), StopReason.ContentFilter)] };
        var api = new ScriptedModelApi(ScriptedTurn.From(refusal), ScriptedTurn.From(ToolCallsOutput(Call("bash", new { cmd = "ls" }))), ScriptedTurn.Text("done"));
        var bridge = new AgentBridge(new AgentState([new ChatMessageUser(TaskPrompt)]), new Model(api), retryRefusals: 2, approval: [RejectAll]);

        var output = await bridge.GenerateAsync("inspect", [new ChatMessageUser(TaskPrompt)], [], ToolChoice.Auto, new GenerateConfig());

        Assert.Equal("done", output.Completion);
        Assert.Equal(3, api.Requests.Count);
    }

    // ----------------------------------------------------------------------------------------------------------
    // sandbox agent bridge: the gate holds over HTTP
    // ----------------------------------------------------------------------------------------------------------

    private static async Task<(SandboxAgentBridge Server, HttpClient Client, ScriptedModelApi Api)> StartServerAsync(IReadOnlyList<ApprovalPolicy>? approval, params ScriptedTurn[] turns)
    {
        var api = new ScriptedModelApi(turns, "served-model");
        var bridge = new AgentBridge(new AgentState([new ChatMessageUser("Fix the bug in main.py")]), new Model(api), approval: approval);
        var server = await SandboxAgentBridge.StartAsync(bridge, new FakeSandboxEnvironment());
        var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl + "/") };
        client.DefaultRequestHeaders.Add("x-api-key", server.AuthToken);
        return (server, client, api);
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private const string MessagesRequest = """
        {"model": "claude-sonnet-4-6", "max_tokens": 100, "system": "You are Claude Code.",
         "tools": [{"name": "bash", "description": "run", "input_schema": {"type": "object", "properties": {"cmd": {"type": "string"}}}}],
         "messages": [{"role": "user", "content": "Fix the bug in main.py"}]}
        """;

    private const string CompletionsRequest = """
        {"model": "gpt-4o", "messages": [{"role": "user", "content": "Fix the bug in main.py"}],
         "tools": [{"type": "function", "function": {"name": "bash", "description": "run", "parameters": {"type": "object", "properties": {"cmd": {"type": "string"}}}}}]}
        """;

    [Fact]
    public async Task a_bridged_tool_call_the_policy_rejects_never_reaches_the_sandboxed_agent()
    {
        using var scope = new SampleContextScope();
        var (server, client, api) = await StartServerAsync([Policy(Approvers.Auto(ApprovalDecision.Reject), "bash"), ApproveAll],
            ScriptedTurn.ToolCall("bash", new { cmd = "rm -rf /" }, id: "toolu_1"), ScriptedTurn.Text("Done."));
        await using (server)
        using (client)
        {
            var response = await client.PostAsync("v1/messages", Body(MessagesRequest));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            var content = body["content"]!.AsArray();
            Assert.DoesNotContain(content, block => block!["type"]!.GetValue<string>() == "tool_use");
            Assert.Equal("Done.", content.Single()!["text"]!.GetValue<string>());
            Assert.Equal(2, api.Requests.Count);
            Assert.Equal("approval", api.Requests[1].Input.OfType<ChatMessageTool>().Single().Error!.Type);
            Assert.Equal("reject", Assert.Single(scope.Transcript.Events.OfType<ApprovalEvent>()).Decision);
        }
    }

    [Fact]
    public async Task an_ambient_policy_gates_the_sandbox_bridge_on_the_completions_dialect()
    {
        using var scope = new SampleContextScope();
        using var ambient = ToolApproval.Begin([RejectAll]);
        var (server, client, api) = await StartServerAsync(null, ScriptedTurn.ToolCall("bash", new { cmd = "ls" }, id: "call_1"), ScriptedTurn.Text("Done."));
        await using (server)
        using (client)
        {
            var response = await client.PostAsync("v1/chat/completions", Body(CompletionsRequest));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            var message = body["choices"]![0]!["message"]!.AsObject();
            Assert.Null(message["tool_calls"]);
            Assert.Equal("Done.", message["content"]!.GetValue<string>());
            Assert.Equal(2, api.Requests.Count);
        }
    }

    [Fact]
    public async Task a_terminate_decision_from_a_bridged_generation_is_signalled_to_the_agent()
    {
        var (server, client, api) = await StartServerAsync([Policy(Approvers.Auto(ApprovalDecision.Terminate))], ScriptedTurn.ToolCall("bash", new { cmd = "ls" }), ScriptedTurn.Text("never"));
        await using (server)
        using (client)
        {
            var response = await client.PostAsync("v1/messages", Body(MessagesRequest));

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var terminate = Assert.IsType<TerminateSampleException>(server.TerminateError);
            Assert.StartsWith("Tool call approver requested termination: ", terminate.Reason, StringComparison.Ordinal);
            Assert.True(server.TerminateRequested.IsCancellationRequested);
            Assert.Null(server.LimitError);
            Assert.Single(api.Requests);
            Assert.Contains("requested termination", (await response.Content.ReadFromJsonAsync<JsonObject>())!["error"]!["message"]!.GetValue<string>());
        }
    }

    // ----------------------------------------------------------------------------------------------------------
    // eval-level wiring
    // ----------------------------------------------------------------------------------------------------------

    private static EvalTask AdditionTask(ApprovalOption? approval = null) => new()
    {
        Name = "approval",
        Dataset = new MemoryDataset([new Sample("What is 1 + 1?") { Target = "2" }], name: "add"),
        Solver = Solvers.Chain(Solvers.UseTools(Addition()), Solvers.Generate()),
        Approval = approval,
    };

    private static ScriptedModelApi AdditionModel() => new(ScriptedTurn.ToolCall("addition", new { x = 1, y = 1 }, id: "c1"), ScriptedTurn.Text("2"));

    private async Task<Log.EvalLog> RunEval(EvalTask task, ApprovalOption? approval = null) =>
        await Eval.RunAsync(task, new EvalOptions { Model = new Model(AdditionModel()), LogDir = _tempDir, MaxSamples = 1, Approval = approval });

    private static readonly JsonNode RejectAllConfig = JsonNode.Parse("""{"approvers": [{"name": "auto", "tools": "*", "params": {"decision": "reject"}}]}""")!;

    [Fact]
    public async Task eval_level_approval_is_applied_and_recorded_in_the_log_config()
    {
        var log = await RunEval(AdditionTask(), approval: RejectAll);

        var sample = Assert.Single(log.Samples!);
        var approval = Assert.Single(sample.Events.OfType<ApprovalEvent>());
        Assert.Equal("reject", approval.Decision);
        Assert.Equal("auto", approval.Approver);
        Assert.Equal("approval", sample.Messages.OfType<ChatMessageTool>().Single().Error!.Type);
        Assert.True(JsonNode.DeepEquals(RejectAllConfig, log.Eval.Config.Approval), log.Eval.Config.Approval?.ToJsonString());
    }

    [Fact]
    public async Task task_level_approval_applies_and_the_eval_level_overrides_it()
    {
        var taskOnly = await RunEval(AdditionTask(RejectAll));
        var overridden = await RunEval(AdditionTask(RejectAll), approval: ApproveAll);
        var none = await RunEval(AdditionTask());

        Assert.Equal("reject", Assert.Single(taskOnly.Samples![0].Events.OfType<ApprovalEvent>()).Decision);
        Assert.True(JsonNode.DeepEquals(RejectAllConfig, taskOnly.Eval.Config.Approval));
        Assert.Equal("approve", Assert.Single(overridden.Samples![0].Events.OfType<ApprovalEvent>()).Decision);
        // a defaulted auto_approver() records no params, as Python's registry_params does
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"approvers": [{"name": "auto", "tools": "*", "params": {}}]}"""), overridden.Eval.Config.Approval), overridden.Eval.Config.Approval?.ToJsonString());
        Assert.Empty(none.Samples![0].Events.OfType<ApprovalEvent>());
        Assert.Null(none.Eval.Config.Approval);
        Assert.False(ToolApproval.HaveToolApproval);
    }

    [Fact]
    public async Task an_approver_name_or_a_config_file_can_be_given_at_the_eval_level()
    {
        Directory.CreateDirectory(_tempDir);
        var file = Path.Combine(_tempDir, "reject.json");
        File.WriteAllText(file, RejectAllConfig.ToJsonString());

        var byName = await RunEval(AdditionTask(), approval: "auto");
        var byFile = await RunEval(AdditionTask(), approval: file);

        Assert.Equal("approve", Assert.Single(byName.Samples![0].Events.OfType<ApprovalEvent>()).Decision);
        Assert.Equal("*", byName.Eval.Config.Approval!["approvers"]![0]!["tools"]!.GetValue<string>());
        Assert.Equal("reject", Assert.Single(byFile.Samples![0].Events.OfType<ApprovalEvent>()).Decision);
        Assert.True(JsonNode.DeepEquals(RejectAllConfig, byFile.Eval.Config.Approval));
    }

    [Fact]
    public async Task a_terminate_decision_ends_the_sample_as_an_operator_limit()
    {
        var log = await RunEval(AdditionTask(), approval: Policy(Approvers.Auto(ApprovalDecision.Terminate)));

        Assert.Equal(Log.EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);
        Assert.Equal("operator", sample.Limit!.Type);
        Assert.Equal(1, sample.Limit.Limit);
        Assert.Equal("Tool call approver requested termination.", sample.Limit.Reason);
        Assert.Contains(sample.Events, e => e is SampleLimitEvent { Type: "operator", Message: "Tool call approver requested termination." });
        Assert.Contains(sample.Events, e => e is ToolEvent { Function: "addition" });
    }
}

using System.ComponentModel;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;

namespace InspectAzureAI.Examples.InlineCards;

/// <summary>
/// Port of <c>examples/inline_cards/approval.py</c> <c>approval_demo</c>: a demo task for the inline tool-call
/// approval card. A mockllm-driven react agent fires one tool call (<c>dangerous_action</c>) requiring human
/// approval (<c>approval="human"</c>), then submits. Deviation: the Textual approval surface and the ACP
/// <c>_ApprovalCard</c> (<c>--acp-server</c>) are not ported; the human approver decides at the console
/// (<see cref="ConsoleApprovalPrompter"/>), or by script under <c>--fake</c> (<see cref="ScriptedApprovalPrompter"/>).
/// </summary>
public static class ApprovalDemo
{
    /// <summary>The task name (<c>@task def approval_demo</c>).</summary>
    public const string TaskName = "approval_demo";

    /// <summary>
    /// The name the <c>inspectai</c> CLI discovers the task under. Deviation: <c>examples/approval</c> registers
    /// <c>approval_demo</c> in the same assembly, and the CLI resolves bare names per assembly (Python resolves them per
    /// file), so this one is qualified to keep both reachable.
    /// </summary>
    public const string CliTaskName = "inline_cards_approval_demo";

    /// <summary>The <c>Sample(input=...)</c> of <c>approval_demo</c>, verbatim.</summary>
    public const string SampleInput = "Use the dangerous_action tool to clean up /tmp/example, then submit 'ok'.";

    /// <summary>The <c>Sample(target=["ok"])</c>.</summary>
    public const string SampleTarget = "ok";

    /// <summary>The <c>AgentSubmit(description=...)</c>, verbatim.</summary>
    public const string SubmitDescription = "Submit the final answer once the action has run.";

    /// <summary>The <c>tool_arguments={"action": ...}</c> of the scripted call, verbatim.</summary>
    public const string ScriptedAction = "rm -rf /tmp/example";

    /// <summary>Port of <c>@tool def dangerous_action()</c>: performs an action that needs human approval before running.</summary>
    public static ToolDef DangerousAction() => ToolDef.FromMethod(new Func<string, string>(DangerousActionExecute), name: "dangerous_action");

    /// <summary>Port of the <c>get_model("mockllm/model", custom_outputs=[...])</c> of <c>approval_demo</c>.</summary>
    public static InspectAzureAI.Eval.Model.Model Model() => MockLlm.Create(
        () => ScriptedTurn.ToolCall("dangerous_action", new { action = ScriptedAction }),
        () => ScriptedTurn.ToolCall(Agents.DefaultSubmitName, new { answer = SampleTarget }));

    /// <summary>Port of <c>@task def approval_demo()</c> (<c>approval="human"</c>: the console prompter), discoverable by the <c>inspectai</c> CLI as <see cref="CliTaskName"/>.</summary>
    [Task(CliTaskName)]
    public static EvalTask ApprovalDemoTask() => Build();

    /// <summary>
    /// The task: one sample, <c>react(tools=[dangerous_action()], submit=AgentSubmit("submit", ...))</c>, <c>includes()</c>,
    /// <c>approval="human"</c>, <c>message_limit=10</c>, and its own mockllm model (<c>Task(model=model)</c>, which wins over
    /// the eval's). With <paramref name="prompter"/> the human approver decides through it instead of the console.
    /// </summary>
    public static EvalTask Build(SandboxSpec? sandbox = null, IApprovalPrompter? prompter = null) => new()
    {
        Name = TaskName,
        Dataset = new MemoryDataset([new Sample(SampleInput) { Target = new Target([SampleTarget]) }]),
        Solver = Agents.AsSolver(Agents.React(
            tools: [DangerousAction()],
            submit: new AgentSubmit { Name = Agents.DefaultSubmitName, Description = SubmitDescription })),
        Scorers = [Scorers.Includes()],
        Approval = prompter is null
            ? ApprovalOption.FromSpec("human")
            : ApprovalOption.FromPolicies(new ApprovalPolicy(Approvers.Human(prompter: prompter), "*")),
        MessageLimit = 10,
        Model = Model(),
        Sandbox = sandbox,
    };

    [Description("Perform an action that needs human approval before running.")]
    private static string DangerousActionExecute([Description("Free-form description of what to do.")] string action) => $"performed: {action}";
}

/// <summary>
/// The human approver under <c>--fake</c>: applies one scripted <paramref name="decision"/> to every call escalated
/// to it except the react agent's submit (approved, so the sample ends), prints what it was shown, and keeps every
/// request in <see cref="Requests"/>. A decision the request does not offer falls back to its first choice.
/// </summary>
public sealed class ScriptedApprovalPrompter(ApprovalDecision decision = ApprovalDecision.Approve, TextWriter? output = null) : IApprovalPrompter
{
    private readonly object _sync = new();
    private readonly List<ApprovalRequest> _requests = [];
    private readonly TextWriter _output = output ?? Console.Out;

    public IReadOnlyList<ApprovalRequest> Requests
    {
        get
        {
            lock (_sync)
            {
                return _requests.ToArray();
            }
        }
    }

    public Task<ApprovalDecision> PromptAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _requests.Add(request);
        }

        var preferred = request.Call.Function == Agents.DefaultSubmitName ? ApprovalDecision.Approve : decision;
        var chosen = request.Choices.Contains(preferred) ? preferred : request.Choices[0];
        var argument = request.Call.Arguments.Select(pair => pair.Value?.ToString() ?? "None").FirstOrDefault() ?? "";
        _output.WriteLine(
            $"[human approver] {request.Call.Function}({argument}) choices: {string.Join("/", request.Choices.Select(choice => choice.ToPython()))} -> {chosen.ToPython()} (scripted)");
        return Task.FromResult(chosen);
    }
}

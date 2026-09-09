using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Approval;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of the README of <c>examples/approval</c> (<c>inspect eval approval.py --approval approval.yaml</c>) as an
/// <see cref="IExample"/>: the <c>approval_demo</c> task (<see cref="ApprovalDemo"/>) under the policy of
/// <c>approval.json</c>, against either the scripted <see cref="FakeApprovalModel"/> (<c>--fake</c>) or a Foundry
/// deployment. Deviation: the Python task is fixed to a Docker sandbox; here <c>--sandbox local</c> runs the bash
/// and python tools on this host, which is how the offline run works (there is no fake sandbox script, the tools
/// run for real).
/// </summary>
public sealed class ApprovalExample : IExample
{
    /// <summary>A safety net for <c>--fake</c>: the two scripts need about 8 and 10 messages; a policy that rejects submit would otherwise loop.</summary>
    public const int FakeMessageLimit = 40;

    public string Name => "approval";

    public string Description => "Approval mode: bash and python tool calls vetted by the custom bash_allowlist and python_allowlist approvers and a human approver, bound by an approval policy";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(ApprovalDemo.TaskName, Build, "asks the model to demonstrate bash (ls, rm) and python (print, math.factorial, shutil.rmtree) in a sandbox"),
    ];

    public ExampleDefaults Defaults { get; } = new(Sandbox: "docker", Approval: "approval.json", NeedsDocker: false);

    public IReadOnlyList<string> Deviations { get; } =
    [
        "The approval policy is approval.json rather than approval.yaml: this port reads JSON policy files only (a YAML file is a NotSupportedException). The structure and values are identical.",
        "python_allowlist uses a lightweight source scanner (PythonSyntax) instead of Python's ast module, so it approximates SyntaxError detection (the 'Invalid Python syntax: ...' messages differ from CPython's) and reports the first violation in source order rather than in ast.walk order.",
        "Both approvers read the call's first argument as Python's str() would (None, True/False, the text of a string); a call with no arguments is rejected as empty (Empty command / Empty code) where Python's next(iter(...)) would error the sample.",
        "The --fake scripted model, the scripted human prompter (rejects every escalated bash/python call, approves submit) and the local sandbox option are additions for running the demonstration offline; the Python example only runs through inspect eval.",
        "The Python task is fixed to sandbox=\"docker\"; here the sandbox comes from the runner (--sandbox, default docker), and --sandbox none is refused because the tools need one.",
    ];

    public Model CreateFakeModel(ExampleContext ctx) => FakeApprovalModel.Create();

    /// <summary>No script: the demo's bash and python calls run for real under <c>--sandbox local</c> (the dangerous ones are rejected by the scripted human).</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;

    private static EvalTask Build(ExampleContext ctx)
    {
        var task = ApprovalDemo.Build(ctx.Sandbox ?? throw new PrerequisiteError("approval_demo needs a sandbox (the bash and python tools run in it): use --sandbox docker or --sandbox local"));
        // The scripted model keeps retrying submit if a policy rejects it, so a fake run also caps the conversation.
        return ctx.Fake ? task with { MessageLimit = FakeMessageLimit } : task;
    }
}

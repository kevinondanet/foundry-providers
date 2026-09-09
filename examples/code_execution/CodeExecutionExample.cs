using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.CodeExecution;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/code_execution.py</c> as an <see cref="IExample"/>: <see cref="CodeExecutionTask"/> against
/// the scripted <see cref="FakeCodeExecutionModel"/> (<c>--fake</c>, with a fake sandbox answering the
/// <c>python3</c> run) or a Foundry deployment in a Docker sandbox. Deviation: the Python task is fixed to
/// <c>sandbox="docker"</c>; here the sandbox comes from the runner (<c>--sandbox</c>, default <c>docker</c>;
/// <c>local</c> runs <c>python3</c> on this host), and <c>none</c> is refused because the fallback needs one.
/// </summary>
public sealed class CodeExecutionExample : IExample
{
    /// <summary>What the fake sandbox prints for the scripted model's code.</summary>
    public const string ExpectedOutput = "459135\n";

    public string Name => "code_execution";

    public string Description => "The code_execution tool (native provider execution in Python, the python() sandbox fallback here) adding 435678 + 23457 in a sandbox";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(CodeExecutionTask.TaskName, Build, "one sample: execute Python code that adds 435678 + 23457 and prints the result; no scorer"),
    ];

    public ExampleDefaults Defaults { get; } = new(Sandbox: "docker");

    public IReadOnlyList<string> Deviations { get; } =
    [
        "No model provider of this port executes code_execution server-side (Python's native OpenAI, Anthropic, Google, Grok and Mistral execution): every model takes the python() sandbox fallback, so the task needs a sandbox with python3 (docker by default; --sandbox local runs python3 on this host).",
        "The Python task is fixed to sandbox=\"docker\"; here the sandbox comes from the runner (--sandbox, default docker) and --sandbox none is refused because the fallback runs in it.",
        "The --fake scripted model and the fake sandbox script (which answers the python3 run with 459135) are additions for running offline; the Python example only runs through inspect eval.",
    ];

    public Model CreateFakeModel(ExampleContext ctx) => FakeCodeExecutionModel.Create();

    /// <summary>Answers the fallback's <c>bash --login -c "python3 -"</c> with what the scripted code prints; any other program fails as an unknown script.</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => new FakeSandboxScript()
        .OnExact(
            call => call.Input?.Trim() == FakeCodeExecutionModel.Code
                ? FakeSandboxScript.Ok(ExpectedOutput)
                : FakeSandboxScript.Fail(1, "Traceback (most recent call last):\n  File \"<stdin>\", line 1, in <module>\nRuntimeError: the scripted sandbox only knows print(435678 + 23457)\n"),
            "bash", "--login", "-c", "python3 -");

    private static EvalTask Build(ExampleContext ctx) =>
        CodeExecutionTask.Build(ctx.Sandbox ?? throw new PrerequisiteError("code_execution_task needs a sandbox (the python fallback runs in it): use --sandbox docker, --sandbox local or --sandbox fake"));
}

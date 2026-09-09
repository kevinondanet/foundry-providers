using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Runner;

namespace InspectAzureAI.Examples.HelloWorld;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/hello_world.py</c> <c>hello_world</c>: the simplest possible Inspect eval, useful for testing
/// your configuration / network / platform etc. One inline sample ("Just reply with Hello World", target
/// "Hello World"), the <c>generate()</c> solver and the <c>exact()</c> scorer. Deviation: the <c>--fake</c> scripted
/// model (which answers "Hello World", or the <c>-T fake_answer=&lt;text&gt;</c> value) is an addition for running
/// the example offline, and the task takes the runner's sandbox (none by default, as in Python).
/// </summary>
public sealed class HelloWorldExample : IExample
{
    /// <summary>The task name (<c>@task def hello_world</c>).</summary>
    public const string TaskName = "hello_world";

    /// <summary>The <c>Sample(input=...)</c> of <c>hello_world</c>, verbatim.</summary>
    public const string SampleInput = "Just reply with Hello World";

    /// <summary>The <c>Sample(target=...)</c> of <c>hello_world</c>, verbatim.</summary>
    public const string SampleTarget = "Hello World";

    /// <summary>The <c>-T</c> key that changes what the scripted model answers (to see the INCORRECT path).</summary>
    public const string FakeAnswerArg = "fake_answer";

    /// <summary>The scripted model's name, as it appears in the banner and the log.</summary>
    public const string FakeModelName = "hello-world-scripted";

    /// <summary>More turns than any run needs (one per sample and epoch).</summary>
    private const int FakeTurnBudget = 10_000;

    public string Name => TaskName;

    public string Description => "The simplest possible Inspect eval, useful for testing your configuration / network / platform etc.";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(TaskName, ctx => Build(ctx.Sandbox), "one sample asking the model to reply with Hello World, scored by exact()"),
    ];

    public ExampleDefaults Defaults { get; } = new();

    public IReadOnlyList<string> Deviations { get; } =
    [
        "The --fake scripted model is an addition for running the example offline: it answers \"Hello World\" (exact() scores CORRECT), or the text given as -T fake_answer=<text>, which shows the INCORRECT path. The Python example only runs through inspect eval.",
        "The task takes the sandbox the runner resolves (--sandbox); the Python task declares none, and none is the default here too.",
    ];

    /// <summary>Port of <c>@task def hello_world()</c>, discoverable by the <c>inspectai</c> CLI (<c>eval hello_world --assembly ...</c>).</summary>
    [Task(TaskName)]
    public static EvalTask HelloWorld() => Build();

    /// <summary>Builds the <c>hello_world</c> task: the one sample, <c>generate()</c> and <c>exact()</c>, on <paramref name="sandbox"/> (null: none, as in Python).</summary>
    public static EvalTask Build(SandboxSpec? sandbox = null) => new()
    {
        Name = TaskName,
        Dataset = new MemoryDataset([new Sample(SampleInput) { Target = SampleTarget }]),
        Solver = Solvers.Generate(),
        Scorers = [Scorers.Exact()],
        Sandbox = sandbox,
    };

    public Model CreateFakeModel(ExampleContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return CreateFakeModel(ctx.TaskArg(FakeAnswerArg, SampleTarget)!);
    }

    /// <summary>The model behind <c>--fake</c>: a scripted model (<see cref="FakeModels.Answering"/>) that answers <paramref name="answer"/> to every request, with a rough token count.</summary>
    public static Model CreateFakeModel(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return FakeModels.Answering(FakeModelName, _ => answer, FakeTurnBudget);
    }

    /// <summary>No sandbox script: the task has no tools.</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;
}

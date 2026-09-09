using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Runner;

namespace InspectAzureAI.Examples.SecurityGuide;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/security_guide.py</c> <c>security_guide</c>: 16 computer-security questions of the bundled
/// <c>security_guide</c> dataset (inputs stored as chat-message lists), answered tersely under a security-expert
/// system message (<c>system_message(SYSTEM_MESSAGE)</c> then <c>generate()</c>) and graded by
/// <c>model_graded_fact()</c> against a short expert answer. As in Python the grader is the model under evaluation
/// unless a grader model is given. Deviation: the <c>--fake</c> scripted model (<see cref="FakeSecurityGuideModel"/>)
/// plays both the answering model and the grader offline, and the task takes the runner's sandbox (none by
/// default, as in Python).
/// </summary>
public sealed class SecurityGuideExample : IExample
{
    /// <summary>The task name (<c>@task def security_guide</c>).</summary>
    public const string TaskName = "security_guide";

    /// <summary>The bundled dataset the task reads (<c>example_dataset("security_guide")</c>).</summary>
    public const string DatasetName = "security_guide";

    /// <summary><c>SYSTEM_MESSAGE</c>, verbatim (a triple-quoted string: it starts and ends with a newline).</summary>
    public const string SystemMessage = "\nYou are a computer security expert and you are tasked with providing concise response to the following questions. Please assume that the reader is also well versed in computer security and provide a short response in a few words.\n";

    public string Name => TaskName;

    public string Description => "16 computer-security questions answered tersely under a security-expert system message and graded by model_graded_fact() against a short expert answer";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(TaskName, ctx => Build(ctx.Sandbox), "the bundled security_guide dataset, short answers graded by model_graded_fact()"),
    ];

    public ExampleDefaults Defaults { get; } = new();

    public IReadOnlyList<string> Deviations { get; } =
    [
        "The --fake scripted model is an addition for running the example offline. It plays both roles, as the Python grader does when no grader model is given: asked a question it answers with the dataset's expert answer, except for the three abbreviated questions (sqli, xss, cmd injection) which it answers vaguely; asked to grade it compares the submission with the expert answer and replies GRADE: C or GRADE: I, so the run scores 13/16. The Python example only runs through inspect eval.",
        "example_dataset reads security_guide.jsonl embedded in the InspectAzureAI.Eval assembly (Datasets.Example) rather than a file of the Python package, so this folder carries no data file; the records are the same.",
        "The task takes the sandbox the runner resolves (--sandbox); the Python task declares none, and none is the default here too.",
    ];

    /// <summary>Port of <c>@task def security_guide()</c>, discoverable by the <c>inspectai</c> CLI (<c>eval security_guide --assembly ...</c>).</summary>
    [Task(TaskName)]
    public static EvalTask SecurityGuide() => Build();

    /// <summary>
    /// Builds the <c>security_guide</c> task: the bundled dataset (records already in sample form: a message-list
    /// <c>input</c> and a <c>target</c>), <c>system_message(SYSTEM_MESSAGE)</c> then <c>generate()</c>, and
    /// <c>model_graded_fact()</c>, on <paramref name="sandbox"/> (null: none, as in Python).
    /// </summary>
    public static EvalTask Build(SandboxSpec? sandbox = null) => new()
    {
        Name = TaskName,
        Dataset = Datasets.Example(DatasetName),
        Solver = Solvers.Chain(Solvers.SystemMessage(SystemMessage), Solvers.Generate()),
        Scorers = [Scorers.ModelGradedFact()],
        Sandbox = sandbox,
    };

    public Model CreateFakeModel(ExampleContext ctx) => FakeSecurityGuideModel.Create();

    /// <summary>No sandbox script: the task has no tools.</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;
}

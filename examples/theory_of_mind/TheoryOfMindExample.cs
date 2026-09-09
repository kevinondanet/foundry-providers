using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Runner;

namespace InspectAzureAI.Examples.TheoryOfMind;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/theory_of_mind.py</c> as an <see cref="IExample"/>: the <c>theory_of_mind</c> task (the
/// 100 false-belief questions of the bundled <c>theory_of_mind</c> example dataset, answered with
/// <c>chain_of_thought()</c> + <c>generate()</c>, refined by <c>self_critique()</c> when <c>critique=True</c>, and
/// graded by <c>model_graded_fact()</c>). The Python <c>__main__</c> runs
/// <c>eval(theory_of_mind(critique=True), model="openai/gpt-4o")</c>; here that is
/// <c>-T critique=true --model &lt;deployment&gt;</c>. Deviation: the model is a Foundry deployment (or the scripted
/// <see cref="FakeTheoryOfMindModel"/> under <c>--fake</c>) rather than <c>openai/gpt-4o</c>.
/// </summary>
public sealed class TheoryOfMindExample : IExample
{
    /// <summary>The task name (<c>@task def theory_of_mind</c>).</summary>
    public const string TaskName = "theory_of_mind";

    /// <summary>The <c>-T</c> task argument that maps to the Python <c>critique</c> parameter.</summary>
    public const string CritiqueArg = "critique";

    public string Name => TaskName;

    public string Description => "Theory of mind: 100 false-belief questions answered with chain_of_thought + generate, optionally refined by self_critique (-T critique=true), graded by model_graded_fact";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(TaskName, ctx => TheoryOfMind(ctx.TaskArgBool(CritiqueArg, false)), "chain of thought, optional self critique (-T critique=true), model-graded fact"),
    ];

    public ExampleDefaults Defaults { get; } = new();

    public IReadOnlyList<string> Deviations { get; } =
    [
        "The Python __main__ evaluates theory_of_mind(critique=True) against openai/gpt-4o; here the model is a Foundry deployment (--model, Entra ID) and critique is a task argument (-T critique=true), defaulting to false as the @task parameter does.",
        "The --fake scripted model is an addition for running the example offline: it recognises the chain-of-thought, critique, improved-answer and grading prompts by their template text and answers from the dataset's targets, so every sample grades C without a network.",
    ];

    public Model CreateFakeModel(ExampleContext ctx) => FakeTheoryOfMindModel.Create();

    /// <summary>No sandbox: the task has no tools.</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;

    /// <summary>
    /// Port of <c>@task def theory_of_mind(critique=False)</c>, discoverable by the <c>inspectai</c> CLI
    /// (<c>eval theory_of_mind -T critique=true --assembly ...</c>): <c>example_dataset("theory_of_mind")</c>,
    /// <c>[chain_of_thought(), generate()]</c> plus <c>self_critique()</c> when <paramref name="critique"/>, and
    /// <c>model_graded_fact()</c>.
    /// </summary>
    [Task(TaskName)]
    public static EvalTask TheoryOfMind(bool critique = false)
    {
        // use self_critique if requested
        var solver = new List<Solver> { Solvers.ChainOfThought(), Solvers.Generate() };
        if (critique)
        {
            solver.Add(Solvers.SelfCritique());
        }

        return new EvalTask
        {
            Name = TaskName,
            Dataset = Datasets.Example("theory_of_mind"),
            Solver = Solvers.Chain([.. solver]),
            Scorers = [Scorers.ModelGradedFact()],
            TaskArgs = new Dictionary<string, object?>(StringComparer.Ordinal) { [CritiqueArg] = critique },
        };
    }
}

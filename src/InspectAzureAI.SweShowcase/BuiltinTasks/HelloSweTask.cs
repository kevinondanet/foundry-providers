using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Tasks;

namespace InspectAzureAI.SweShowcase.BuiltinTasks;

/// <summary>
/// <c>hello-swe</c>: three small Python edits in the workspace (write <c>hello.py</c>, add a <c>--reverse</c> flag to
/// the provided <c>words.py</c>, fix an off-by-one in the provided <c>stats.py</c>), each verified by the bash
/// command in its <c>metadata.check</c>.
/// </summary>
internal static class HelloSweTask
{
    public const string Name = "hello-swe";

    public static readonly ShowcaseTask Definition = new(Name, "three small Python edits, each verified by a bash check command", ExecCheckScorer.Name, Build);

    private static EvalTask Build(TaskBuildContext context) => new()
    {
        Name = Name,
        Dataset = Datasets.Json(TaskData.DatasetPath(Name)),
        Solver = context.Solver,
        Scorers = [ExecCheckScorer.Create()],
        Sandbox = context.Sandbox,
        MessageLimit = ShowcaseLimits.MessageLimit,
        TimeLimit = ShowcaseLimits.TimeLimit,
        // A demo should show every sample; a failed one is recorded (and reported through the exit code) instead of aborting the run.
        FailOnError = false,
    };
}

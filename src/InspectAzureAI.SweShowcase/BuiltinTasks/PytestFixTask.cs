using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Tasks;

namespace InspectAzureAI.SweShowcase.BuiltinTasks;

/// <summary>
/// <c>pytest-fix</c>: two tiny Python packages (<c>textkit.slugify</c>, <c>mathkit.is_prime</c>) shipped with a failing
/// pytest suite; the agent must fix the implementation and the check runs <c>python3 -m pytest -q</c> (exit 0 is correct).
/// </summary>
internal static class PytestFixTask
{
    public const string Name = "pytest-fix";

    public static readonly ShowcaseTask Definition = new(Name, "fix a tiny package until its pytest suite passes", ExecCheckScorer.Name, Build);

    private static EvalTask Build(TaskBuildContext context) => new()
    {
        Name = Name,
        Dataset = Datasets.Json(TaskData.DatasetPath(Name)),
        Solver = context.Solver,
        Scorers = [ExecCheckScorer.Create()],
        Sandbox = context.Sandbox,
        MessageLimit = ShowcaseLimits.MessageLimit,
        TimeLimit = ShowcaseLimits.TimeLimit,
        FailOnError = false,
    };
}

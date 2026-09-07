using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;

namespace InspectAzureAI.SweShowcase.BuiltinTasks;

/// <summary>
/// <c>system-explorer</c>, after the inspect_swe <c>examples/system_explorer</c> task: two questions the agent answers by
/// poking around the container (the installed Python version, the CPU core count), graded by
/// <c>model_graded_qa</c> with the task model as the judge.
/// </summary>
internal static class SystemExplorerTask
{
    public const string Name = "system-explorer";

    public const string ScorerName = "model_graded_qa";

    public static readonly ShowcaseTask Definition = new(Name, "two questions about the container, judged by model_graded_qa", ScorerName, Build);

    private static EvalTask Build(TaskBuildContext context) => new()
    {
        Name = Name,
        Dataset = Datasets.Json(TaskData.DatasetPath(Name)),
        Solver = context.Solver,
        Scorers = [Scorers.ModelGradedQa()],
        Sandbox = context.Sandbox,
        MessageLimit = ShowcaseLimits.MessageLimit,
        TimeLimit = ShowcaseLimits.TimeLimit,
        FailOnError = false,
    };
}

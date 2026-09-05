using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.SweShowcase.BuiltinTasks;

namespace InspectAzureAI.SweShowcase;

/// <summary>What a built-in task takes from the command line: the agent solver and the sandbox spec every sample runs in.</summary>
internal sealed record TaskBuildContext(Solver Solver, SandboxSpec Sandbox);

/// <summary>A built-in task: its name, a one-line description, the scorer it uses (for <c>list</c>) and the factory producing the <see cref="EvalTask"/>.</summary>
internal sealed record ShowcaseTask(string Name, string Description, string ScorerName, Func<TaskBuildContext, EvalTask> Build)
{
    /// <summary>Loads the task's dataset from <c>tasks/&lt;name&gt;/dataset.json</c> next to the executable.</summary>
    public IDataset LoadDataset() => Datasets.Json(TaskData.DatasetPath(Name));
}

/// <summary>Limits shared by the built-in tasks so a runaway agent cannot hold a demo hostage; the runner scores whatever state a limited sample has.</summary>
internal static class ShowcaseLimits
{
    public const int MessageLimit = 200;

    public static readonly TimeSpan TimeLimit = TimeSpan.FromMinutes(20);
}

/// <summary>The task registry of the showcase, the stand-in for Inspect's <c>@task</c> decorator and <c>inspect list tasks</c>.</summary>
internal static class ShowcaseTasks
{
    public static IReadOnlyList<ShowcaseTask> All { get; } = [HelloSweTask.Definition, PytestFixTask.Definition, SystemExplorerTask.Definition];

    public static string Names => string.Join(", ", All.Select(task => task.Name));

    public static ShowcaseTask Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new UsageError($"run requires --task <name> (one of {Names})");
        }

        return All.FirstOrDefault(task => task.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new UsageError($"unknown task '{name}' (known tasks: {Names})");
    }
}

using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;

namespace InspectAzureAI.Cli.Tests;

/// <summary>The <c>[Task]</c> methods the CLI tests discover over this assembly.</summary>
public static class TestTasks
{
    public const string AnswerTarget = "ok";

    [Task]
    public static EvalTask Hello() => new()
    {
        Name = "hello",
        Dataset = new MemoryDataset([new Sample("Say ok") { Target = AnswerTarget }, new Sample("Say ok again") { Target = AnswerTarget }]),
        Scorers = [Scorers.Includes()],
    };

    [Task("parametrized", "light=true", "draft=false")]
    public static EvalTask Parametrized(int count = 2, string target = AnswerTarget, bool shuffle = false, double threshold = 0.5, IReadOnlyList<string>? tags = null) => new()
    {
        Name = "parametrized",
        Dataset = new MemoryDataset(Enumerable.Range(1, count).Select(i => new Sample($"question {i}") { Target = target, Id = i })),
        Scorers = [Scorers.Includes()],
        Metadata = new Dictionary<string, object?>
        {
            ["count"] = count,
            ["target"] = target,
            ["shuffle"] = shuffle,
            ["threshold"] = threshold,
            ["tags"] = tags,
        },
    };

    [Task("draft_task", "draft=true", "size=large")]
    public static EvalTask Draft() => Hello() with { Name = "draft_task" };

    [Task("needs_arg")]
    public static EvalTask NeedsArg(string required) => Hello() with { Name = required };
}

/// <summary>A second declaring type with a task named like one in <see cref="TestTasks"/>, so name resolution can be ambiguous.</summary>
public static class DuplicateTasks
{
    [Task("dup")]
    public static EvalTask First() => TestTasks.Hello() with { Name = "dup" };
}

public static class MoreDuplicateTasks
{
    [Task("dup")]
    public static EvalTask Second() => TestTasks.Hello() with { Name = "dup" };
}

using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Eval.Tools.Builtin;

namespace InspectAzureAI.Examples.Surfer;

/// <summary>
/// Port of <c>examples/surfer.py</c> <c>surfer</c>: a <c>react</c> agent with the built-in <c>web_search()</c> tool
/// answers "What were the scores of last night's NHL games?". (The file's <see cref="WebSurfer"/> tool is ported
/// alongside but, as in Python, the task does not use it.)
/// </summary>
public static class Surfer
{
    /// <summary>The task name (<c>@task def surfer</c>).</summary>
    public const string TaskName = "surfer";

    /// <summary>The <c>Sample(input=...)</c>, verbatim.</summary>
    public const string SampleInput = "What were the scores of last night's NHL games?";

    /// <summary>The task as the CLI discovers it (<c>inspectai eval surfer</c>): <c>web_search()</c> with no provider named, i.e. the model's own search on the Anthropic route.</summary>
    [Task(TaskName)]
    public static EvalTask SurferTask() => Build(BuiltinTools.WebSearch());

    /// <summary>Builds <c>surfer</c> with <paramref name="webSearch"/> as the react agent's only tool.</summary>
    public static EvalTask Build(ToolDef webSearch)
    {
        ArgumentNullException.ThrowIfNull(webSearch);
        return new EvalTask
        {
            Name = TaskName,
            Dataset = new MemoryDataset([new Sample(SampleInput)]),
            Solver = Agents.AsSolver(Agents.React(tools: [webSearch])),
        };
    }
}

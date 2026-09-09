using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Tools;

namespace InspectAzureAI.Examples.EvalsInEval;

/// <summary>
/// Port of <c>examples/evals_in_eval/bash_task.py</c> <c>bash_task</c>: one sample asking the model to print
/// <c>hello world</c> with the <c>bash()</c> tool, scored with <c>includes()</c>. The second inner eval the Claude Code
/// agent runs inside the container (the Python file is copied in verbatim); ported so it also runs on this engine.
/// </summary>
public static class BashTask
{
    public const string TaskName = "bash_task";

    /// <summary>The sample input, as written in <c>bash_task.py</c>.</summary>
    public const string Input = "Use the bash tool to print 'hello world'.";

    public const string Target = "hello world";

    /// <summary>Port of <c>@task def bash_task</c> (Docker sandbox, as in Python).</summary>
    [Task(TaskName)]
    public static EvalTask BashTaskTask() => Build(new SandboxSpec("docker"));

    /// <summary>The task on <paramref name="sandbox"/> (the runner's choice; Python fixes <c>sandbox="docker"</c>).</summary>
    public static EvalTask Build(SandboxSpec sandbox)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        return new EvalTask
        {
            Name = TaskName,
            Dataset = new MemoryDataset([new Sample(Input) { Target = Target }]),
            Solver = Solvers.Chain(Solvers.UseTools(SandboxTools.Bash()), Solvers.Generate()),
            Scorers = [Scorers.Includes()],
            Sandbox = sandbox,
        };
    }
}

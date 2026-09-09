using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Tools.Builtin;

namespace InspectAzureAI.Examples.CodeExecution;

/// <summary>
/// Port of <c>examples/code_execution.py</c> <c>code_execution_task</c>: one sample asking the model to add
/// 435678 and 23457 with the <c>code_execution()</c> tool in a Docker sandbox, with no scorer. In Python the
/// tool runs natively on providers that execute code server-side and falls back to the <c>python()</c> sandbox
/// tool elsewhere; in this port every model takes the sandbox fallback (see
/// <see cref="BuiltinTools.CodeExecution"/>), so the sandbox needs <c>python3</c>.
/// </summary>
public static class CodeExecutionTask
{
    /// <summary>The task name (<c>@task def code_execution_task</c>).</summary>
    public const string TaskName = "code_execution_task";

    /// <summary>The <c>Sample(...)</c> input, verbatim.</summary>
    public const string Input = "Please use your available tools to execute Python code that adds 435678 + 23457 and then prints the result.";

    /// <summary>Port of <c>@task def code_execution_task()</c> (<c>sandbox="docker"</c>), discoverable by the <c>inspectai</c> CLI.</summary>
    [Task(TaskName)]
    public static EvalTask Create() => Build(new SandboxSpec("docker"));

    /// <summary>Builds <c>code_execution_task</c> for <paramref name="sandbox"/> (the Python task is fixed to <c>"docker"</c>; the examples runner substitutes <c>--sandbox</c>).</summary>
    public static EvalTask Build(SandboxSpec sandbox)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        return new EvalTask
        {
            Name = TaskName,
            Dataset = new MemoryDataset([new Sample(Input)]),
            Solver = Solvers.Chain(Solvers.UseTools(BuiltinTools.CodeExecution()), Solvers.Generate()),
            Sandbox = sandbox,
        };
    }
}

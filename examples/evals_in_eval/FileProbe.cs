using System.ComponentModel;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Tools;

namespace InspectAzureAI.Examples.EvalsInEval;

/// <summary>
/// Port of <c>examples/evals_in_eval/file_probe.py</c>: the <c>list_files</c> tool and the <c>file_probe</c> task. In the
/// Python example this is one of the two inner evals the Claude Code agent runs with the <c>inspect</c> CLI inside the
/// container (the file is copied into the sandbox verbatim); the C# port lets the same task run on this engine, offline
/// or through the <c>inspectai</c> CLI.
/// </summary>
public static class FileProbe
{
    public const string TaskName = "file_probe";

    /// <summary>The sample input, as written in <c>file_probe.py</c>.</summary>
    public const string Input = "Is there a file named \"foo.txt\" in the current directory?";

    public const string Target = "Yes";

    /// <summary>Port of <c>@tool def list_files</c>: <c>ls dir</c> in the sandbox; a failure is a <see cref="ToolError"/> carrying stderr.</summary>
    public static ToolDef ListFiles() => ToolDef.FromMethod(Execute, "list_files");

    /// <summary>Port of <c>@task def file_probe</c> (Docker sandbox, as in Python).</summary>
    [Task(TaskName)]
    public static EvalTask FileProbeTask() => Build(new SandboxSpec("docker"));

    /// <summary>The task on <paramref name="sandbox"/> (the runner's choice; Python fixes <c>sandbox="docker"</c>).</summary>
    public static EvalTask Build(SandboxSpec sandbox)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        return new EvalTask
        {
            Name = TaskName,
            Dataset = new MemoryDataset(
            [
                new Sample(Input)
                {
                    Target = Target,
                    Files = new Dictionary<string, string>(StringComparer.Ordinal) { ["foo.txt"] = "hello" },
                },
            ]),
            Solver = Solvers.Chain(Solvers.UseTools(ListFiles()), Solvers.Generate()),
            Scorers = [Scorers.Includes()],
            Sandbox = sandbox,
        };
    }

    [Description("List the files in a directory.")]
    private static async Task<string> Execute([Description("Directory")] string dir, CancellationToken cancellationToken)
    {
        var result = await SampleContext.Require().Sandbox().ExecAsync(["ls", dir], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            return result.Stdout;
        }

        throw new ToolError(result.Stderr);
    }
}

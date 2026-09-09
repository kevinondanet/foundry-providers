using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;

namespace InspectAzureAI.Examples.ToolUse;

/// <summary>
/// Port of the five <c>@task</c> functions of <c>examples/tool_use.py</c> (<c>addition_problem</c>, <c>bash</c>,
/// <c>read</c>, <c>write</c>, <c>parallel_add</c>), each discoverable by the <c>inspectai</c> CLI through its
/// <see cref="TaskAttribute"/>. The three sandbox tasks are fixed to <c>sandbox="local"</c> in Python; the
/// <paramref name="sandbox"/> overloads let the examples runner substitute the sandbox of <c>--sandbox</c>.
/// </summary>
public static class ToolUseTasks
{
    /// <summary>Port of <c>SYSTEM_MESSAGE</c>, verbatim (the triple-quoted string keeps its leading and trailing newline).</summary>
    public const string SystemMessage = "\nPlease answer exactly Yes or No with no additional words.\n";

    /// <summary>The <c>Sample(input=...)</c> of <c>addition_problem</c>.</summary>
    public const string AdditionInput = "What is 1 + 1?";

    /// <summary>The <c>Sample(input=...)</c> of <c>bash</c>.</summary>
    public const string BashInput = "Please list the files in the /usr/bin directory. Is there a file named 'python3' in the directory?";

    /// <summary>The <c>Sample(input=...)</c> of <c>read</c>.</summary>
    public const string ReadInput = "Please read the file 'foo.txt'";

    /// <summary>The <c>Sample(input=...)</c> of <c>write</c>.</summary>
    public const string WriteInput = "Please write 'bar' to a file named 'foo.txt'.";

    /// <summary>The <c>Sample(input=...)</c> of <c>parallel_add</c>.</summary>
    public const string ParallelAddInput =
        "Please add the numbers 1+1 and 2+2, and then print the results of those computations side by side as just two numbers (with no additional text). You should use the add tool to do this, and you should make the two required calls to add in parallel so the results are computed faster.";

    /// <summary>Port of <c>@task def addition_problem()</c>: the <c>add</c> tool, scored with <c>match(numeric=True)</c> against <c>["2", "2.0"]</c>; no sandbox.</summary>
    [Task("addition_problem")]
    public static EvalTask AdditionProblem() => new()
    {
        Name = "addition_problem",
        Dataset = new MemoryDataset([new Sample(AdditionInput) { Target = new Target(["2", "2.0"]) }]),
        Solver = Solvers.Chain(Solvers.UseTools(ToolUseTools.Add()), Solvers.Generate()),
        Scorers = [Scorers.Match(numeric: true)],
    };

    /// <summary>Port of <c>@task def bash()</c> with its <c>sandbox="local"</c>.</summary>
    [Task("bash")]
    public static EvalTask Bash() => Bash(new SandboxSpec("local"));

    /// <summary>Port of <c>@task def bash()</c>: the system message, the <c>list_files</c> tool and <c>includes()</c> against <c>["Yes"]</c>, in <paramref name="sandbox"/>.</summary>
    public static EvalTask Bash(SandboxSpec sandbox)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        return new EvalTask
        {
            Name = "bash",
            Dataset = new MemoryDataset([new Sample(BashInput) { Target = new Target(["Yes"]) }]),
            Solver = Solvers.Chain(
                Solvers.SystemMessage(SystemMessage),
                Solvers.UseTools(ToolUseTools.ListFiles()),
                Solvers.Generate()),
            Sandbox = sandbox,
            Scorers = [Scorers.Includes()],
        };
    }

    /// <summary>Port of <c>@task def read()</c> with its <c>sandbox="local"</c>.</summary>
    [Task("read")]
    public static EvalTask Read() => Read(new SandboxSpec("local"));

    /// <summary>Port of <c>@task def read()</c>: the <c>read_file</c> tool and <c>match()</c> (the sample has no target), in <paramref name="sandbox"/>.</summary>
    public static EvalTask Read(SandboxSpec sandbox)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        return new EvalTask
        {
            Name = "read",
            Dataset = new MemoryDataset([new Sample(ReadInput)]),
            Solver = Solvers.Chain(Solvers.UseTools(ToolUseTools.ReadFile()), Solvers.Generate()),
            Scorers = [Scorers.Match()],
            Sandbox = sandbox,
        };
    }

    /// <summary>Port of <c>@task def write()</c> with its <c>sandbox="local"</c>.</summary>
    [Task("write")]
    public static EvalTask Write() => Write(new SandboxSpec("local"));

    /// <summary>Port of <c>@task def write()</c>: the <c>write_file</c> tool and <c>match()</c> (the sample has no target), in <paramref name="sandbox"/>.</summary>
    public static EvalTask Write(SandboxSpec sandbox)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        return new EvalTask
        {
            Name = "write",
            Dataset = new MemoryDataset([new Sample(WriteInput)]),
            Solver = Solvers.Chain(Solvers.UseTools(ToolUseTools.WriteFile()), Solvers.Generate()),
            Scorers = [Scorers.Match()],
            Sandbox = sandbox,
        };
    }

    /// <summary>Port of <c>@task def parallel_add()</c>: the <c>add</c> tool called twice in one turn, scored with <c>includes()</c> against <c>["2 4"]</c>; no sandbox.</summary>
    [Task("parallel_add")]
    public static EvalTask ParallelAdd() => new()
    {
        Name = "parallel_add",
        Dataset = new MemoryDataset([new Sample(ParallelAddInput) { Target = new Target(["2 4"]) }]),
        Solver = Solvers.Chain(Solvers.UseTools(ToolUseTools.Add()), Solvers.Generate()),
        Scorers = [Scorers.Includes()],
    };
}

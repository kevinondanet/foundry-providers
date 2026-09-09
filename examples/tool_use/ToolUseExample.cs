using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.ToolUse;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/tool_use.py</c> as an <see cref="IExample"/>: the five tasks of <see cref="ToolUseTasks"/>
/// over the custom tools of <see cref="ToolUseTools"/>, against the scripted <see cref="FakeToolUseModel"/>
/// (<c>--fake</c>) or a Foundry deployment. Deviation: <c>bash</c>, <c>read</c> and <c>write</c> are fixed to
/// <c>sandbox="local"</c> in Python; here they take the runner's sandbox (<c>local</c> by default, <c>fake</c>
/// under <c>--fake</c>, where <c>ls /usr/bin</c> is answered by a fixed listing and <c>foo.txt</c> starts out
/// holding <c>bar</c>), and <c>addition_problem</c> and <c>parallel_add</c> run without one as in Python.
/// </summary>
public sealed class ToolUseExample : IExample
{
    /// <summary>What the fake sandbox answers for <c>ls /usr/bin</c>: enough of a listing to contain <c>python3</c>.</summary>
    public const string UsrBinListing = "awk\nbash\ncat\nenv\ngrep\nls\npython3\nsed\nsh\n";

    public string Name => "tool_use";

    public string Description => "Custom @tool functions: an add tool (also called twice in parallel) and list_files, read_file and write_file tools over the sample's sandbox";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask("addition_problem", _ => ToolUseTasks.AdditionProblem(), "the add tool on 'What is 1 + 1?', scored with match(numeric=True)"),
        new ExampleTask("bash", ctx => ToolUseTasks.Bash(RequireSandbox(ctx, "bash")), "the list_files tool (sandbox().exec) on /usr/bin, a Yes/No answer scored with includes()"),
        new ExampleTask("read", ctx => ToolUseTasks.Read(RequireSandbox(ctx, "read")), "the read_file tool (sandbox().read_file) on foo.txt"),
        new ExampleTask("write", ctx => ToolUseTasks.Write(RequireSandbox(ctx, "write")), "the write_file tool (sandbox().write_file) writing 'bar' to foo.txt"),
        new ExampleTask("parallel_add", _ => ToolUseTasks.ParallelAdd(), "two add calls in one turn, the sums printed side by side, scored with includes()"),
    ];

    public ExampleDefaults Defaults { get; } = new(Sandbox: "local");

    public IReadOnlyList<string> Deviations { get; } =
    [
        "Both local sandboxes give every sample a fresh temporary working directory (Python's LocalSandboxEnvironment creates a tempfile.TemporaryDirectory per sample and resolves relative paths under it, as the C# local sandbox does), so in either port read finds no foo.txt under --sandbox local and reports it as missing (a tool error the model relays); only the fake sandbox seeds foo.txt with 'bar'.",
        "bash, read and write are fixed to sandbox=\"local\" in Python; here the sandbox comes from the runner (--sandbox, default local) and --sandbox none is refused for them. addition_problem and parallel_add have no sandbox in Python and ignore --sandbox.",
        "Tool descriptions and parameter descriptions come from [Description] attributes rather than docstrings (the docstrings' Returns sections are not part of the schema in Python either).",
        "The --fake scripted model and the fake sandbox script (a fixed /usr/bin listing, a seeded foo.txt) are additions for running offline; the Python example only runs through inspect eval.",
    ];

    public Model CreateFakeModel(ExampleContext ctx) => FakeToolUseModel.Create();

    /// <summary><c>ls /usr/bin</c> answers <see cref="UsrBinListing"/>, any other <c>ls</c> fails as it would on a missing path, and <c>foo.txt</c> is seeded for the read task.</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => new FakeSandboxScript()
        .OnExact(FakeSandboxScript.Ok(UsrBinListing), "ls", "/usr/bin")
        .OnPrefix(call => FakeSandboxScript.Fail(2, $"ls: cannot access '{string.Join(" ", call.Cmd.Skip(1))}': No such file or directory\n"), "ls")
        .WithFile("foo.txt", "bar");

    private static SandboxSpec RequireSandbox(ExampleContext ctx, string task) =>
        ctx.Sandbox ?? throw new PrerequisiteError($"{task} needs a sandbox (its tool runs in it): use --sandbox local, --sandbox fake or --sandbox docker");
}

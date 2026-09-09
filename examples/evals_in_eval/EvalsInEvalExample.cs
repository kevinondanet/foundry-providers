using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.EvalsInEval;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/evals_in_eval/task.py</c> <c>evals_in_eval</c> as an <see cref="IExample"/>: a Claude Code agent
/// (<see cref="Claude.ClaudeCode"/>, driven through the sandbox agent bridge) is told to run <c>inspect eval
/// file_probe.py</c> and <c>inspect eval bash_task.py</c> inside a Docker-in-Docker compose sandbox (a container with
/// Python inspect-ai, Claude Code and a Docker CLI pointed at a rootless <c>docker:dind</c> sidecar) and to report both
/// accuracy scores. The two inner tasks are copied into the sandbox verbatim and are also ported
/// (<see cref="FileProbe"/>, <see cref="BashTask"/>) so they run on this engine. Deviation: under <c>--fake</c> the
/// sandbox is scripted (<see cref="FakeSandbox"/>) and a stand-in <see cref="FakeClaudeCli"/> plays the CLI against
/// the real bridge; Python has no offline mode.
/// </summary>
public sealed class EvalsInEvalExample : IExample
{
    public const string TaskName = "evals_in_eval";

    /// <summary>The sample input, as written in <c>task.py</c>.</summary>
    public const string Input = "Run two evaluations using the inspect CLI:\n1. 'inspect eval file_probe.py'\n2. 'inspect eval bash_task.py'\n\nAfter running both, report the accuracy scores from each evaluation.";

    public const string FakeModelName = FakeEvalsInEvalModel.ModelName;

    /// <summary>The compose file next to the task (<c>sandbox=("docker", "compose.yaml")</c>).</summary>
    public const string ComposeFile = "compose.yaml";

    /// <summary>Where the project copies this example's data files.</summary>
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "evals_in_eval");

    public string Name => "evals_in_eval";

    public string Description => "Evals inside an eval: Claude Code, driven through the sandbox agent bridge in a Docker-in-Docker compose sandbox, runs the file_probe and bash_task Inspect evals with the inspect CLI and reports their accuracy";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(TaskName, BuildOuter, "Claude Code runs `inspect eval file_probe.py` and `inspect eval bash_task.py` in the sandbox and reports the accuracies"),
        new ExampleTask(FileProbe.TaskName, ctx => FileProbe.Build(RequireSandbox(ctx, FileProbe.TaskName)), "the first inner eval: is foo.txt in the current directory? (list_files tool, includes scorer)"),
        new ExampleTask(BashTask.TaskName, ctx => BashTask.Build(RequireSandbox(ctx, BashTask.TaskName)), "the second inner eval: print 'hello world' with the bash tool (includes scorer)"),
    ];

    public ExampleDefaults Defaults { get; } = new(
        Sandbox: "docker",
        ComposeFile: ComposeFile,
        NeedsDocker: true,
        ModelHint: "any deployment (Claude Code speaks the Anthropic Messages dialect to the bridge, which serves the deployment behind it)");

    public IReadOnlyList<string> Deviations { get; } =
    [
        "The agent is a thin wrapper over the Swe project's ClaudeCode.Agent (Version = \"sandbox\": the image's own npm-installed claude) instead of a hand-rolled sandbox().exec: the CLI also receives --session-id, --output-format stream-json --verbose and a seeded ~/.claude/settings.json, and its JSONL output is recorded on the transcript as claude_code info events.",
        "Authentication to the bridge is its per-instance token in ANTHROPIC_AUTH_TOKEN (the C# SandboxAgentBridge listens on the host at host.docker.internal:<port>, not in the container at localhost:13131, and answers 401 without the token), not Python's placeholder ANTHROPIC_API_KEY.",
        "INSPECT_EVAL_MODEL defaults to anthropic/inspect (override with -T inspect_eval_model=<spec>) rather than str(get_model()): the inner `inspect` CLI is the Python one, and an Anthropic provider spec makes its SDK follow ANTHROPIC_BASE_URL/ANTHROPIC_AUTH_TOKEN to the same bridge, which maps any model name to the sample's model. A Foundry deployment name is not a Python model spec.",
        "Sample files are given as absolute paths into the example's output folder (Python's relative file_probe.py/bash_task.py resolve against the task file's directory).",
        "file_probe and bash_task are also ported as C# tasks (list_files over Sandbox().ExecAsync, bash(), includes()) so they can run on this engine and through the inspectai CLI; in Python they only exist as the inner Python evals.",
        "The Python task is fixed to sandbox=(\"docker\", \"compose.yaml\"); here the sandbox comes from the runner (--sandbox, default docker with this folder's compose.yaml). --sandbox local cannot work for the outer task (no claude on this host); --fake uses the scripted sandbox, in which a stand-in claude POSTs the prompt to the real bridge and feeds the model canned `inspect eval` output instead of running Python inspect-ai and nested Docker.",
    ];

    /// <summary>Port of <c>@task def evals_in_eval</c> (Docker compose sandbox, as in Python).</summary>
    [Task(TaskName)]
    public static EvalTask EvalsInEval() => Build(DefaultDirectory, new SandboxSpec("docker", Path.Combine(DefaultDirectory, ComposeFile)));

    /// <summary>The task with its data files under <paramref name="directory"/> and the given sandbox.</summary>
    public static EvalTask Build(string directory, SandboxSpec sandbox, string inspectEvalModel = Claude.DefaultInspectEvalModel)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(sandbox);
        return new EvalTask
        {
            Name = TaskName,
            Dataset = new MemoryDataset(
            [
                new Sample(Input)
                {
                    Files = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["file_probe.py"] = Path.Combine(directory, "file_probe.py"),
                        ["bash_task.py"] = Path.Combine(directory, "bash_task.py"),
                    },
                },
            ]),
            Solver = Agents.AsSolver(Claude.ClaudeCode(inspectEvalModel)),
            Sandbox = sandbox,
        };
    }

    public Model CreateFakeModel(ExampleContext ctx) => FakeEvalsInEvalModel.Create();

    /// <summary>
    /// The scripted container: <c>which claude</c> finds the image's CLI, <c>pwd</c> is the image's WORKDIR, the
    /// settings seed succeeds, the launch is played by <see cref="FakeClaudeCli"/> against the bridge, <c>ls</c>
    /// lists the sample's files (for <c>file_probe</c>) and <c>echo</c> prints (for <c>bash_task</c>).
    /// </summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx)
    {
        var script = new FakeSandboxScript();
        var cli = new FakeClaudeCli(() => ScriptedSandboxEnvironment.Current ?? script.Environments.LastOrDefault());
        return script
            .OnExact(FakeSandboxScript.Ok(FakeClaudeCli.BinaryPath + "\n"), "bash", "-c", "which claude")
            .OnExact(FakeSandboxScript.Ok(FakeClaudeCli.WorkingDirectory + "\n"), "bash", "-c", "pwd")
            .OnMatch(call => call.Cmd is ["bash", "-c", var cmd] && cmd.StartsWith("mkdir -p \"$HOME/.claude\"", StringComparison.Ordinal), _ => FakeSandboxScript.Ok())
            .OnMatch(FakeClaudeCli.IsLaunch, cli.Run)
            .OnPrefix(_ => FakeSandboxScript.Ok(Listing(ScriptedSandboxEnvironment.Current ?? script.Environments.LastOrDefault())), "ls")
            .OnMatch(call => call.Cmd is ["bash", "--login", "-c", var cmd] && cmd.StartsWith("echo ", StringComparison.Ordinal), call => FakeSandboxScript.Ok(Echo(call.Cmd[3])))
            .WithDefault(FakeSandboxScript.Fail(127, "fake sandbox: command not scripted\n"));
    }

    private static EvalTask BuildOuter(ExampleContext ctx) =>
        Build(ctx.ExampleDirectory, RequireSandbox(ctx, TaskName), ctx.TaskArg("inspect_eval_model", Claude.DefaultInspectEvalModel)!);

    private static SandboxSpec RequireSandbox(ExampleContext ctx, string task) =>
        ctx.Sandbox ?? throw new PrerequisiteError($"{task} needs a sandbox (the agent and the tools run in it): use --sandbox docker (or --fake)");

    /// <summary>The names of the sample's top-level files, one per line, as <c>ls</c> in the working directory would print.</summary>
    private static string Listing(ScriptedSandboxEnvironment? environment)
    {
        if (environment is null)
        {
            return "";
        }

        var names = environment.Files.Keys
            .Where(path => !path.Contains('/') && !path.StartsWith('.'))
            .Order(StringComparer.Ordinal)
            .ToList();
        return names.Count == 0 ? "" : string.Join("\n", names) + "\n";
    }

    /// <summary>A minimal <c>echo</c>: strips one level of single or double quotes from each word.</summary>
    private static string Echo(string command)
    {
        var words = command["echo ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => word.Trim('\'', '"'));
        return string.Join(" ", words) + "\n";
    }
}

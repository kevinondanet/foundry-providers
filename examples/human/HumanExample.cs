using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Agents.Human;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Human;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/human/human.py</c> <c>human</c> as an <see cref="IExample"/>: a task whose solver is the
/// <c>human_cli</c> agent (<see cref="HumanCli"/>). The agent installs the <c>task</c> command set
/// (<c>task start/stop/note/status/instructions/submit/quit</c>) into the sample's Docker sandbox (built from this
/// folder's <c>Dockerfile</c>: <c>python:3.12-bookworm</c> with a <c>nonroot</c> user, no network), prints the
/// <c>docker exec</c> login command, and waits until the person runs <c>task submit &lt;answer&gt;</c>;
/// <c>-T user=root|nonroot</c> picks the login user. No model is involved: the human is the agent. Deviation: under
/// <c>--fake</c> the sandbox is a scripted container (<see cref="HumanFakeSandboxProvider"/>) in which a scripted
/// operator runs the task commands (instructions, start, note, status, submit) through the real sandbox-service
/// protocol, so the run completes with a submitted answer and nothing waits for a login; the runner's own fake
/// sandbox cannot host the agent because it answers no connection requests.
/// </summary>
public sealed class HumanExample : IExample
{
    /// <summary>The task name (<c>@task def human</c>).</summary>
    public const string TaskName = "human";

    /// <summary>The <c>-T</c> key of the Python task parameter (<c>user: Literal["root", "nonroot"] | None</c>).</summary>
    public const string UserArg = "user";

    /// <summary>The <c>-T</c> key that changes what the scripted operator submits under <c>--fake</c>.</summary>
    public const string FakeAnswerArg = "fake_answer";

    /// <summary>What the scripted operator submits by default.</summary>
    public const string DefaultFakeAnswer = "42";

    /// <summary>The input of the sample Python's <c>Task()</c> makes when no dataset is given (<c>Sample(input="prompt")</c>).</summary>
    public const string DefaultSampleInput = "prompt";

    /// <summary>The scripted model's name (never called: the human is the agent).</summary>
    public const string FakeModelName = "human-unused";

    /// <summary>How often the scripted container is polled under <c>--fake</c> (the docker default is 0.2 s, other sandboxes 2 s).</summary>
    public static readonly TimeSpan FakePollingInterval = TimeSpan.FromMilliseconds(50);

    public string Name => TaskName;

    public string Description => "Human CLI agent: the solver is a person who logs into the sandbox and works with the installed task command set (task start/stop/note/status/instructions/submit) until they submit an answer";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(TaskName, Build, "human_cli(user=...) in a Docker sandbox built from this folder's Dockerfile (-T user=root|nonroot); under --fake a scripted operator runs the task commands and submits an answer"),
    ];

    public ExampleDefaults Defaults { get; } = new(
        Sandbox: "docker",
        ComposeFile: "compose.yaml",
        NeedsDocker: true,
        ModelHint: "none (the human is the agent): run with --fake --sandbox docker so no deployment is resolved");

    public IReadOnlyList<string> Deviations { get; } =
    [
        "No model is involved, but the runner always resolves one: a real human run is `--fake --sandbox docker` (the scripted model is never called; the Docker sandbox and the login command are real), since without --fake the runner would resolve a Foundry deployment first.",
        "Under --fake alone the sandbox is a scripted container (HumanFakeSandboxProvider, registered in place of the runner's fake sandbox, which cannot answer connection requests): it emulates the file commands the installer and the sandbox service run, and a scripted operator issues task instructions, task start, task note, task status and task submit through the real request/response protocol, so the run completes with the answer (42, or -T fake_answer=<text>) and nothing waits for a login. Python has no offline mode.",
        "The Textual Human Agent panel and its VS Code login links are not ported; the console view prints the login command, a status line on clock changes, the notes and the final answer (Python's ConsoleView is the same fallback).",
        "The agent's polling interval is a parameter (50 ms under --fake, the sandbox's default otherwise), an addition of the C# human_cli.",
        "The compose file's `network_mode: none` and `build: .` are honoured by the Docker Compose sandbox; --sandbox local is refused because the agent would append to this host's ~/.bashrc and write /opt/human_agent.",
    ];

    /// <summary>Port of <c>@task def human(user=None)</c>, discoverable by the <c>inspectai</c> CLI (<c>-T user=nonroot</c>).</summary>
    [Task(TaskName)]
    public static EvalTask Human(string? user = null) => Build(user, new SandboxSpec("docker", Path.Combine(DefaultExampleDirectory, "compose.yaml")));

    /// <summary>Where the example's compose file lives when nothing else is said: <c>AppContext.BaseDirectory/human</c>.</summary>
    public static string DefaultExampleDirectory => Path.Combine(AppContext.BaseDirectory, "human");

    /// <summary>
    /// Builds the <c>human</c> task: Python's default one-sample dataset, <c>human_cli(user=user)</c> as the solver
    /// and <paramref name="sandbox"/>; <paramref name="view"/> and <paramref name="pollingInterval"/> pass through to
    /// the agent (the console view and the sandbox's interval when null).
    /// </summary>
    public static EvalTask Build(string? user, SandboxSpec? sandbox, IHumanAgentView? view = null, TimeSpan? pollingInterval = null) => new()
    {
        Name = TaskName,
        Dataset = new MemoryDataset([new Sample(DefaultSampleInput)]),
        Solver = Agents.AsSolver(HumanCli.Agent(user: user, view: view, pollingInterval: pollingInterval)),
        Sandbox = sandbox,
    };

    /// <summary>A scripted model that is never asked anything (the human is the agent); one turn in case a limit or an error path generates.</summary>
    public Model CreateFakeModel(ExampleContext ctx) => new(new ScriptedModelApi([ScriptedTurn.Text("(the human agent does not call the model)")], FakeModelName));

    /// <summary>The script the scripted container answers from (its rules cover what the emulation does not); see <see cref="HumanFakeSandboxProvider"/>.</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => new FakeSandboxScript().WithDefault(FakeSandboxScript.Ok());

    private static EvalTask Build(ExampleContext ctx)
    {
        var user = ctx.TaskArg(UserArg);
        var sandbox = ctx.Sandbox switch
        {
            null => throw new PrerequisiteError($"{TaskName} needs a sandbox that supports connections: use --sandbox docker (or --fake for the scripted operator)"),
            { Type: "local" } => throw new PrerequisiteError($"{TaskName} cannot use the local sandbox (the agent would append to this host's ~/.bashrc and write /opt/human_agent): use --sandbox docker or --fake"),
            { Type: ScriptedSandboxProvider.TypeName } => HumanFakeSandboxProvider.Register(
                RunnerScript(),
                new HumanOperatorScript(ctx.Out, ctx.TaskArg(FakeAnswerArg, DefaultFakeAnswer)!)),
            var other => other,
        };
        var scripted = sandbox.Type == ScriptedSandboxProvider.TypeName;
        return Build(user, sandbox, new ConsoleHumanAgentView(ctx.Out), scripted ? FakePollingInterval : null);
    }

    /// <summary>The script the runner registered for <c>--sandbox fake</c> (so its calls stay visible to tests), or a fresh one.</summary>
    private static FakeSandboxScript RunnerScript() =>
        SandboxRegistry.Get(ScriptedSandboxProvider.TypeName) is ScriptedSandboxProvider provider ? provider.Script : new FakeSandboxScript().WithDefault(FakeSandboxScript.Ok());
}

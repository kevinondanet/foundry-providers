using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Computer;

using ComputerTool = InspectAzureAI.Eval.Tools.Computer;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/computer/computer.py</c> <c>computer_task</c> as an <see cref="IExample"/>: three desktop tasks
/// (read <c>/tmp/flag.txt</c> through the GUI, type a sentence into a terminal, multiply with the calculator) solved
/// by a <c>react</c> agent driving the <c>computer()</c> tool (xdotool and screenshots) inside the
/// <c>aisiuk/inspect-computer-tool</c> image of <c>compose.yaml</c>, scored with <c>includes()</c>. Under
/// <c>--fake</c> the scripted <see cref="FakeComputerModel"/> works the scripted desktop of
/// <see cref="FakeComputerSandbox"/>. Deviation: Python passes <c>messsage_limit=100</c> (a typo <c>Task</c> ignores,
/// so no limit applies); here <see cref="EvalTask.MessageLimit"/> is 100 as the author intended.
/// </summary>
public sealed class ComputerExample : IExample
{
    /// <summary>The Python task's name.</summary>
    public const string TaskName = "computer_task";

    /// <summary>Port of <c>SYSTEM_MESSAGE</c>, verbatim (the triple-quoted string keeps its leading and trailing newline).</summary>
    public const string SystemMessage =
        "\nBefore each step, please clearly explain your intent for performing a tool action: \"I expect tool command X to ...\".\n\n"
        + "After each step, carefully evaluate the resulting screenshot to see if the command achieved the right outcome.\n\n"
        + "Explicitly show your thinking: \"I have evaluated step X...\" If not correct, try again. Only when\n"
        + "you confirm a step was executed correctly should you move on to the next one.\n\n"
        + "Note that launching applications from desktop icons requires a double click.\n";

    /// <summary>The first sample's input, verbatim.</summary>
    public const string FlagInput = "Use the computer tool to find the contents of the file '/tmp/flag.txt'. When you've found it, report its contents.";

    /// <summary>The second sample's input, verbatim.</summary>
    public const string TerminalInput = "Use the computer tool to launch a terminal. Type 'Trudging across the tundra. Mile after Mile.' into the terminal. Important: Make sure that the terminal window is active before typing. When you are done, please use the submit tool to record the result of hitting enter in the terminal after entering that text.";

    /// <summary>The third sample's input, verbatim.</summary>
    public const string CalculatorInput = "Use the computer tool to launch a calculator. Calculate 123 x 456. Report the result.";

    public const string FlagTarget = "Frunobulax";

    public const string TerminalTarget = "bash: Trudging: command not found";

    public const string CalculatorTarget = "56088";

    /// <summary>The <c>files</c> of the first sample: the flag written into the sandbox before the agent starts.</summary>
    public const string FlagPath = "/tmp/flag.txt";

    /// <summary>What Python's <c>messsage_limit=100</c> meant.</summary>
    public const int MessageLimit = 100;

    public string Name => "computer";

    public string Description => "Computer use: a react agent drives a desktop (xdotool + screenshots) with the computer tool to read a file, type into a terminal and use the calculator, inside the aisiuk/inspect-computer-tool container";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(TaskName, Build, "three desktop tasks (read /tmp/flag.txt via the GUI, type into a terminal, multiply 123 x 456 in the calculator), scored with includes()"),
    ];

    public ExampleDefaults Defaults { get; } = new(Sandbox: "docker", ComposeFile: "compose.yaml", NeedsDocker: true, ModelHint: "a vision-capable deployment (the tool returns screenshots)");

    public IReadOnlyList<string> Deviations { get; } =
    [
        "Python passes messsage_limit=100 (a typo Task(**kwargs) ignores, so no message limit applies there); here the task's MessageLimit is 100, as the author intended.",
        "Python's sandbox=\"docker\" resolves the compose.yaml next to computer.py; here the compose file is passed explicitly as SandboxSpec(\"docker\", \"<example dir>/compose.yaml\") by the runner and the [Task] method. Its VNC/noVNC port mappings (127.0.0.1::5900 and ::6080) are published by docker compose as in Python; the bound host ports appear in the sandbox connection info rather than a Running Samples tab.",
        "The computer tool goes to every model as an ordinary JSON-schema function tool; the Anthropic-native computer_20250124 substitution of Python's anthropic provider is not made on the Anthropic route.",
        "The --fake scripted model (screenshot, double-click the application icon, type the command with press_enter, screenshot, submit what the screen shows) and the fake sandbox (a scripted desktop answering every computer_tool.py action with a description of the screen and a 1x1 PNG; the terminal 'runs' cat /tmp/flag.txt against the sample's files) are additions for running offline; the Python example only runs through inspect eval against the real container.",
        "--sandbox none is refused (the computer tool needs a sandbox) and --sandbox local fails at the first tool call with the tool's PrerequisiteError because /opt/inspect/tool/computer_tool.py is not on this host.",
    ];

    /// <summary>Port of <c>@task def computer_task()</c> with its <c>sandbox="docker"</c> (the example's compose.yaml), for the <c>inspectai</c> CLI.</summary>
    [Task(TaskName)]
    public static EvalTask ComputerTask() => Build(new SandboxSpec("docker", Path.Combine(AppContext.BaseDirectory, "computer", "compose.yaml")));

    /// <summary>Port of <c>@task def computer_task()</c>: the three samples, <c>react(prompt=SYSTEM_MESSAGE, tools=[computer()])</c>, <c>includes()</c> and the message limit, in <paramref name="sandbox"/>.</summary>
    public static EvalTask Build(SandboxSpec sandbox)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        return new EvalTask
        {
            Name = TaskName,
            Dataset = new MemoryDataset(
            [
                new Sample(FlagInput) { Target = FlagTarget, Files = new Dictionary<string, string> { [FlagPath] = FlagTarget } },
                new Sample(TerminalInput) { Target = TerminalTarget },
                new Sample(CalculatorInput) { Target = CalculatorTarget },
            ]),
            Solver = Agents.AsSolver(Agents.React(
                prompt: new AgentPrompt(Instructions: SystemMessage),
                tools: [ComputerTool.Create()])),
            MessageLimit = MessageLimit,
            Scorers = [Scorers.Includes()],
            Sandbox = sandbox,
        };
    }

    public Model CreateFakeModel(ExampleContext ctx) => FakeComputerModel.Create();

    /// <summary>The scripted desktop (see <see cref="FakeComputerSandbox"/>).</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => FakeComputerSandbox.Create();

    private static EvalTask Build(ExampleContext ctx) =>
        Build(ctx.Sandbox ?? throw new PrerequisiteError("computer_task needs a sandbox (the computer tool runs in it): use --sandbox docker or --sandbox fake"));
}

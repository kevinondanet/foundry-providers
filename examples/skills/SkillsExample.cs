using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Eval.Tools.Skills;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Skills;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/skills/task.py</c> <c>skills_example</c> as an <see cref="IExample"/>: five Linux
/// system-exploration questions answered by a <c>react</c> agent that has the <c>skill()</c> tool (the
/// <c>system-info</c>, <c>network-info</c> and <c>disk-usage</c> skill folders of <c>skills/</c>, installed into the
/// sandbox on first use) and <c>bash()</c>, in the <c>ubuntu:24.04</c> container of <c>compose.yaml</c> (an
/// internal-only network), graded by <c>model_graded_qa()</c>. Under <c>--fake</c> the scripted
/// <see cref="FakeSkillsModel"/> invokes the matching skill, runs its helper script and submits the answer (and
/// grades every submission C), against the scripted <see cref="FakeSkillsSandbox"/> or a real <c>local</c> sandbox.
/// </summary>
public sealed class SkillsExample : IExample
{
    /// <summary>The Python task's name.</summary>
    public const string TaskName = "skills_example";

    /// <summary>Port of the <c>react(prompt=...)</c> string, verbatim (Python's implicit concatenation of four literals).</summary>
    public const string Prompt =
        "You are a Linux system administrator. You have access to skills "
        + "that provide guidance for system exploration tasks. Use the skill "
        + "tool to get instructions before attempting tasks, then use bash "
        + "to execute the appropriate commands.";

    /// <summary>The five samples (input, target), verbatim.</summary>
    public static readonly IReadOnlyList<(string Input, string Target)> Samples =
    [
        ("What Linux distribution is this system running? Include the version.", "The system is running Ubuntu 24.04"),
        ("How many CPU cores does this system have?", "The number of CPU cores available on the system"),
        ("What is the total amount of memory (RAM) on this system?", "The total RAM available on the system"),
        ("What is the IP address of this system?", "The IP address(es) configured on the system's network interfaces"),
        ("How much disk space is available on the root filesystem?", "The available disk space on the root (/) filesystem"),
    ];

    /// <summary>The skill folders, in the order Python lists them (<c>SKILLS_DIR / "system-info"</c>, ...).</summary>
    public static readonly IReadOnlyList<string> SkillNames = ["system-info", "network-info", "disk-usage"];

    /// <summary>A safety net for <c>--fake</c>: each sample needs three turns (skill, bash, submit).</summary>
    public const int FakeMessageLimit = 20;

    public string Name => "skills";

    public string Description => "Agent skills: a react agent with the skill tool (three agentskills.io SKILL.md packages with helper scripts) and bash answers Linux system questions, graded by model_graded_qa";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(TaskName, Build, "five Linux system-exploration questions (distribution, CPU cores, RAM, IP address, root disk space) answered with the system-info, network-info and disk-usage skills"),
    ];

    public ExampleDefaults Defaults { get; } = new(Sandbox: "docker", ComposeFile: "compose.yaml", NeedsDocker: false, ModelHint: "a tool-calling deployment (it also grades the answers as model_graded_qa's default model)");

    public IReadOnlyList<string> Deviations { get; } =
    [
        "Python resolves the skill folders relative to task.py (SKILLS_DIR = Path(__file__).parent / \"skills\"); here they are read from the example's output folder (<example dir>/skills/<name>), where the project copies them, and the compose file is passed explicitly as SandboxSpec(\"docker\", \"<example dir>/compose.yaml\").",
        "The --fake scripted model (skill(<matching skill>), bash(./skills/<skill>/scripts/<script>.sh), submit(<the relevant lines of the script's output>); the same scripted model grades every submission 'GRADE: C' when model_graded_qa asks it) and the fake sandbox (pwd is /root, the scripts answer with canned Ubuntu 24.04 output) are additions for running offline; the Python example only runs through inspect eval against the container.",
        "--sandbox local installs the skills into a temporary working directory on this host and runs the helper scripts for real (they fall back gracefully where the Linux tools are absent, so on macOS the answers describe this machine loosely); --sandbox none is refused because skill and bash need a sandbox.",
        "A message limit of 20 guards the fake run; the Python task has none.",
    ];

    /// <summary>Port of <c>@task def skills_example()</c> with its <c>sandbox=("docker", "compose.yaml")</c> and the skills folder beside it, for the <c>inspectai</c> CLI.</summary>
    [Task(TaskName)]
    public static EvalTask SkillsExampleTask()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "skills");
        return Build(new SandboxSpec("docker", Path.Combine(directory, "compose.yaml")), Path.Combine(directory, "skills"));
    }

    /// <summary>Port of <c>@task def skills_example()</c>: the five samples, <c>react(prompt, tools=[skill([...]), bash()])</c> and <c>model_graded_qa()</c>, in <paramref name="sandbox"/> with the skills read from <paramref name="skillsDir"/>.</summary>
    public static EvalTask Build(SandboxSpec sandbox, string skillsDir)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(skillsDir);
        return new EvalTask
        {
            Name = TaskName,
            Dataset = new MemoryDataset(Samples.Select(sample => new Sample(sample.Input) { Target = sample.Target }).ToList()),
            Solver = Agents.AsSolver(Agents.React(
                prompt: new AgentPrompt(Instructions: Prompt),
                tools:
                [
                    SkillTools.Skill(SkillNames.Select(name => SkillSource.FromDirectory(Path.Combine(skillsDir, name))).ToList()),
                    SandboxTools.Bash(),
                ])),
            Scorers = [Scorers.ModelGradedQa()],
            Sandbox = sandbox,
        };
    }

    public Model CreateFakeModel(ExampleContext ctx) => FakeSkillsModel.Create();

    /// <summary>The scripted Ubuntu container (see <see cref="FakeSkillsSandbox"/>).</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => FakeSkillsSandbox.Create();

    private static EvalTask Build(ExampleContext ctx)
    {
        var sandbox = ctx.Sandbox ?? throw new PrerequisiteError("skills_example needs a sandbox (the skill and bash tools run in it): use --sandbox docker, --sandbox local or --sandbox fake");
        var task = Build(sandbox, ctx.DataPath("skills"));
        return ctx.Fake ? task with { MessageLimit = FakeMessageLimit } : task;
    }
}

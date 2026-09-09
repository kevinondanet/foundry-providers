using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Intervention;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/intervention</c> (<c>intervention.py</c> and its README) as an <see cref="IExample"/>: the
/// <c>intervention</c> task (<see cref="Intervention"/>) in <c>shell</c>, <c>computer</c> or <c>multi-tool</c> mode
/// (<c>-T mode=...</c>), with or without human approval (<c>-T approval=true</c>), against either a scripted model,
/// operator and sandbox (<c>--fake</c>, <see cref="InterventionScript"/>) or a Foundry deployment in the mode's Docker
/// sandbox with a person at the terminal. <c>--display conversation</c> prints the user and assistant messages as the
/// Python README suggests. Deviation: the runner resolves one compose file per example, so this example swaps in the
/// mode's compose file itself when the sandbox is Docker; under <c>--fake</c> the operator's answers come from the
/// mode's script and a message limit guards the run.
/// </summary>
public sealed class InterventionExample : IExample
{
    /// <summary>The <c>-T</c> keys of the Python task parameters.</summary>
    public const string ModeArg = "mode";

    public const string ApprovalArg = "approval";

    /// <summary>A safety net for <c>--fake</c>: the longest script needs about 14 messages.</summary>
    public const int FakeMessageLimit = 40;

    public string Name => Intervention.TaskName;

    public string Description => "Intervention demo: a human-in-the-loop agent in a Linux sandbox (shell, computer or multi-tool mode) where the operator types the prompt and steers the agent whenever it stops calling tools";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(
            Intervention.TaskName,
            Build,
            "the operator types the initial prompt, the model works with the mode's tools, and between turns the operator sends a message, presses enter to continue or types exit (-T mode=shell|computer|multi-tool, -T approval=true)"),
    ];

    public ExampleDefaults Defaults { get; } = new(
        Sandbox: "docker",
        ComposeFile: Intervention.ComposeFile(Intervention.ShellMode),
        NeedsDocker: true,
        ModelHint: "a tool-calling deployment (computer mode needs a vision-capable one)");

    public IReadOnlyList<string> Deviations { get; } =
    [
        "The computer mode's approval policy is computer/approval.json rather than approval.yaml: this port reads JSON policy files only (approval.yaml is copied verbatim next to it; the approvers, tool patterns and order are identical).",
        "The task's sandbox is the mode's compose file (shell/, computer/ or multi_tool/compose.yaml) under the example folder, as in Python; the runner only knows one compose file per example, so this example substitutes the mode's file whenever the resolved sandbox is Docker. --sandbox local runs the shell mode's bash and python tools on this host; computer and multi-tool need their images (or --sandbox fake). --sandbox none is refused because every mode's tools need a sandbox.",
        "The operator's prompts read from an InputConsole parameter of the solvers (the process console by default, as Python's input_screen + rich Prompt.ask); under --fake the answers come from the mode's script (InterventionScript), so nothing waits at the terminal.",
        "The --fake scripted model, operator and sandbox (canned ls/python output for the shell mode; a fake computer_tool.py answering screenshots for the computer mode; fake inspect-sandbox-tools and inspect-tool-support JSON-RPC services for the multi-tool mode) are additions for running the demonstration offline, and a message limit of 40 guards the fake run. The Python example only runs through inspect eval.",
        "state.user_prompt.content = ... becomes a replacement of the last user message (messages are immutable records here); the transcript records the same InputEvents.",
        "Any -T mode other than shell and computer selects the multi-tool mode, exactly as Python's match statement does (Literal types are not checked at runtime there either).",
        "Input screens and the conversation display are plain text (── Title ── panels) rather than rich panels; the console reporter has no live progress region to pause.",
    ];

    /// <summary>Port of <c>@task def intervention(mode, approval)</c>, discoverable by the <c>inspectai</c> CLI (<c>-T mode=computer -T approval=true</c>).</summary>
    [Task(Intervention.TaskName)]
    public static EvalTask InterventionTask(string mode = Intervention.ShellMode, bool approval = false) => Intervention.Build(mode, approval);

    public Model CreateFakeModel(ExampleContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return InterventionScript.For(Mode(ctx)).Model();
    }

    /// <summary>The mode's scripted sandbox (see <see cref="InterventionScript.Sandbox"/>).</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return InterventionScript.For(Mode(ctx)).Sandbox();
    }

    /// <summary>The <c>-T mode</c> value (default <c>shell</c>).</summary>
    public static string Mode(ExampleContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return ctx.TaskArg(ModeArg, Intervention.ShellMode)!;
    }

    private static EvalTask Build(ExampleContext ctx)
    {
        var mode = Mode(ctx);
        var approval = ctx.TaskArgBool(ApprovalArg, false);
        var console = ctx.Fake ? InterventionScript.For(mode).Console(ctx.Out) : null;
        var task = Intervention.Build(mode, approval, ctx.ExampleDirectory, console);

        // The task's own sandbox is the mode's compose file; any other resolved sandbox (fake, local) replaces it.
        task = ctx.Sandbox switch
        {
            null => throw new PrerequisiteError($"{Intervention.TaskName} needs a sandbox (the {mode} mode's tools run in it): use --sandbox docker, --sandbox local or --sandbox fake"),
            { Type: "docker" } => task,
            var other => task with { Sandbox = other },
        };

        return ctx.Fake ? task with { MessageLimit = FakeMessageLimit } : task;
    }
}

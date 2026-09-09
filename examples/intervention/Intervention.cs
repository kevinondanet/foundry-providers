using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Eval.Tools.Builtin;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Intervention;

using BashSessionTool = InspectAzureAI.Eval.Tools.BashSession;
using ComputerTool = InspectAzureAI.Eval.Tools.Computer;
using TextEditorTool = InspectAzureAI.Eval.Tools.TextEditor;

/// <summary>
/// Port of <c>examples/intervention/intervention.py</c>: a prototype of an Inspect agent running in a Linux sandbox
/// with human intervention. The <c>intervention</c> task (<see cref="Build"/>) puts the model in one of three
/// sandboxes: <c>shell</c> (bash and python tools in an image built from <c>shell/Dockerfile</c>), <c>computer</c>
/// (the <c>computer()</c> desktop tool on <c>aisiuk/inspect-computer-tool</c>) or <c>multi-tool</c>
/// (<c>bash_session</c>, <c>text_editor</c> and the <c>web_browser</c> tools on <c>aisiuk/inspect-tool-support</c>).
/// The operator types the initial prompt (<see cref="UserPrompt"/>); whenever the model stops calling tools the
/// operator sends a message, presses enter to ask it to continue, or types <c>exit</c> (<see cref="AgentLoop"/>).
/// Deviation: the console the prompts read from is a parameter (<see cref="InputConsole"/>, null for the process
/// console) so a <c>--fake</c> run can script the operator; Python's <c>Prompt.ask</c> is
/// <see cref="ConsoleInput.AskAsync"/>; the computer mode's approval policy is <c>computer/approval.json</c> (this
/// port reads JSON policies only; <c>approval.yaml</c> is kept next to it verbatim).
/// </summary>
public static class Intervention
{
    /// <summary>The task name (<c>@task def intervention</c>).</summary>
    public const string TaskName = "intervention";

    /// <summary>The <c>Mode</c> literals, verbatim (<c>Literal["shell", "computer", "multi-tool"]</c>).</summary>
    public const string ShellMode = "shell";

    public const string ComputerMode = "computer";

    public const string MultiToolMode = "multi-tool";

    public static readonly IReadOnlyList<string> Modes = [ShellMode, ComputerMode, MultiToolMode];

    /// <summary>The input of the sample Python's <c>Task()</c> makes when no dataset is given (<c>Sample(input="prompt")</c>).</summary>
    public const string DefaultSampleInput = "prompt";

    /// <summary>The message appended when the operator presses enter, verbatim.</summary>
    public const string ContinueMessage = "Please continue working on this task.";

    /// <summary>The <c>user_prompt</c> screen's prompt, verbatim.</summary>
    public const string UserPromptText = "Please enter your initial prompt for the model:\n\n";

    /// <summary>The <c>ask_for_next_action</c> prompt, verbatim (Python's dedented text, stripped, plus two newlines).</summary>
    public const string NextActionPrompt =
        "The agent has stopped calling tools. Please either:\n\n"
        + "- Type a message to send to the agent\n"
        + "- Type nothing and hit enter to ask the agent to continue\n"
        + "- Type 'exit' to end the conversation"
        + "\n\n";

    /// <summary><c>SHELL_PROMPT</c>, verbatim (Python's <c>dedent</c> keeps the leading and trailing newline).</summary>
    public const string ShellPrompt = "\n" + """
        You are an extremely capable general purpose agent. Your task is to take instructions from the user and accomplish tasks without human input.

        You will first take instructions from the user, make a plan, and then accomplish the plan autonomously.

        You are in a full Ubuntu environment connected to the internet. You have access to a wide range of tools and libraries and can install more if necessary. You do not need access to sudo to run apt-get or anything else.

        Your "python" tool will execute a given script. It isn't saved and you aren't able to interact with the script to provide input or anything like that. Note that the Python tool isn't like a notebook - you need to print out the values and cannot just return them to be printed. If you do this, nothing will be returned, so make sure you print whatever variables you want returned in the output.

        If you do want a Python script to be persistent, you'll need to save it to a file using the bash tool. The bash tool also is not interactive, so you will need to interact with it in successive turns.

        You won't have many common tools installed by default, so you may need to install them yourself.

        Please reflect on what actions to take before you take them.
        """ + "\n";

    /// <summary><c>COMPUTER_PROMPT</c>, verbatim.</summary>
    public const string ComputerPrompt = "\n" + """
        You are an extremely capable general purpose agent. Your task is to take instructions from the user and accomplish tasks without human input.

        You are in a full Ubuntu environment connected to the internet.

        Please reflect on what actions to take before you take them.

        After each step carefully evaluate if you have achieved the right outcome. Explicitly show your thinking: "I have evaluated step X..." If not correct, try again. Only when you confirm a step was executed correctly should you move on to the next one.
        """ + "\n";

    /// <summary><c>MULTI_TOOL_PROMPT</c>, verbatim (including "a text_editor tools").</summary>
    public const string MultiToolPrompt = "\n" + """
        You are an extremely capable general purpose agent. Your task is to take instructions from the user and accomplish tasks without human input.

        You are in a full Ubuntu environment connected to the internet. You have a bash_session tool, a text_editor tools, and a set of web browser tools.

        You won't have many common tools installed by default, so you may need to install them yourself.

        Please reflect on what actions to take before you take them.
        """ + "\n";

    /// <summary>
    /// Port of <c>@task def intervention(mode: Mode = "shell", approval: bool = False)</c>: the sandbox is the mode's
    /// compose file under <paramref name="exampleDirectory"/> (Python resolves <c>shell/compose.yaml</c> against the task
    /// file); <paramref name="approval"/> adds the <c>human</c> approver, or <c>computer/approval.json</c> in computer mode.
    /// Any mode other than <c>shell</c> and <c>computer</c> is multi-tool (Python's <c>case _</c>).
    /// </summary>
    public static EvalTask Build(string mode = ShellMode, bool approval = false, string? exampleDirectory = null, InputConsole? console = null)
    {
        ArgumentNullException.ThrowIfNull(mode);
        var directory = exampleDirectory ?? DefaultExampleDirectory;
        return new EvalTask
        {
            Name = TaskName,
            Dataset = new MemoryDataset([new Sample(DefaultSampleInput)]),
            Solver = InterventionAgent(mode, console),
            Sandbox = new SandboxSpec("docker", Path.Combine(directory, ComposeFile(mode))),
            Approval = approval ? ApprovalOption.FromSpec(ApprovalSpec(mode, directory)) : null,
        };
    }

    /// <summary>Where the example's compose files live when nothing else is said: <c>AppContext.BaseDirectory/intervention</c>.</summary>
    public static string DefaultExampleDirectory => Path.Combine(AppContext.BaseDirectory, "intervention");

    /// <summary>The mode's compose file, relative to the example folder (<c>shell/compose.yaml</c>, <c>computer/compose.yaml</c>, <c>multi_tool/compose.yaml</c>).</summary>
    public static string ComposeFile(string mode) => mode switch
    {
        ShellMode => Path.Combine("shell", "compose.yaml"),
        ComputerMode => Path.Combine("computer", "compose.yaml"),
        _ => Path.Combine("multi_tool", "compose.yaml"),
    };

    /// <summary>The approval spec of <c>-T approval=true</c>: <c>human</c>, or the computer mode's policy file.</summary>
    public static string ApprovalSpec(string mode, string exampleDirectory) =>
        mode == ComputerMode ? Path.Combine(exampleDirectory, "computer", "approval.json") : "human";

    /// <summary>
    /// Port of <c>@solver def intervention_agent(mode)</c>: the mode's system prompt, the operator's prompt, the mode's
    /// tools and the intervention loop, chained.
    /// </summary>
    public static Solver InterventionAgent(string mode, InputConsole? console = null)
    {
        ArgumentNullException.ThrowIfNull(mode);
        return mode switch
        {
            ShellMode => Solvers.Chain(
                Solvers.SystemMessage(ShellPrompt),
                UserPrompt(console),
                Solvers.UseTools(SandboxTools.Bash(), SandboxTools.Python()),
                AgentLoop(console)),
            ComputerMode => Solvers.Chain(
                Solvers.SystemMessage(ComputerPrompt),
                UserPrompt(console),
                Solvers.UseTools(ComputerTool.Create()),
                AgentLoop(console)),
            _ => Solvers.Chain(
                Solvers.SystemMessage(MultiToolPrompt),
                UserPrompt(console),
                Solvers.UseTools([BashSessionTool.Create(), TextEditorTool.Create(), .. BuiltinTools.WebBrowser()]),
                AgentLoop(console)),
        };
    }

    /// <summary>
    /// Port of <c>@solver def user_prompt()</c>: asks the operator for the initial prompt on a "User Prompt" input
    /// screen and makes it the user prompt's content. Deviation: messages are immutable records here, so the last user
    /// message is replaced by a copy carrying the new content rather than mutated in place.
    /// </summary>
    public static Solver UserPrompt(InputConsole? console = null) => async (state, _, cancellationToken) =>
    {
        using var screen = InputScreen.Open("User Prompt", console: console);
        var prompt = await screen.AskAsync(UserPromptText, cancellationToken: cancellationToken).ConfigureAwait(false);
        var userPrompt = state.UserPrompt;
        state.Messages[state.Messages.LastIndexOf(userPrompt)] = userPrompt with { Content = prompt };
        return state;
    };

    /// <summary>
    /// Port of <c>@solver def agent_loop()</c>: generate (with tool calls and approvals) until the model stops
    /// calling tools, then ask the operator for the next action: <c>exit</c> ends the conversation, an empty line
    /// appends "Please continue working on this task.", anything else is sent as the next user message. A completed
    /// state (a limit was hit) ends the loop without asking.
    /// </summary>
    public static Solver AgentLoop(InputConsole? console = null) => async (state, generate, cancellationToken) =>
    {
        while (true)
        {
            // generate w/ tool calls, approvals, etc.
            state = await generate(state, cancellationToken: cancellationToken).ConfigureAwait(false);

            // check for completed
            if (state.Completed)
            {
                break;
            }

            // prompt for next action
            var nextAction = await AskForNextActionAsync(console, cancellationToken).ConfigureAwait(false);
            using (InputScreen.Open(console: console))
            {
                switch (nextAction.Trim().ToLowerInvariant())
                {
                    case "exit":
                        return state;
                    case "":
                        state.Messages.Add(new ChatMessageUser(ContinueMessage));
                        break;
                    default:
                        state.Messages.Add(new ChatMessageUser(nextAction));
                        break;
                }
            }
        }

        return state;
    };

    /// <summary>Port of <c>ask_for_next_action()</c>: the "Next Action" input screen, with an empty default.</summary>
    public static async Task<string> AskForNextActionAsync(InputConsole? console = null, CancellationToken cancellationToken = default)
    {
        using var screen = InputScreen.Open("Next Action", console: console);
        return await screen.AskAsync(NextActionPrompt, @default: "", cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

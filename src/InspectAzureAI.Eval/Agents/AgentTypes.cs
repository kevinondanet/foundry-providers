using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tools;

namespace InspectAzureAI.Eval.Agents;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>agent/_types.py</c> <c>AgentPrompt</c>: the pieces the react agent assembles into its system
/// message. Every piece is optional; <see cref="Default"/> carries Python's defaults and <see cref="None"/>
/// stands for Python's <c>prompt=None</c> (no system message at all).
/// </summary>
public sealed record AgentPrompt(
    string? Instructions = null,
    string? HandoffPrompt = AgentPrompt.DefaultHandoffPrompt,
    string? AssistantPrompt = AgentPrompt.DefaultAssistantPrompt,
    string? SubmitPrompt = AgentPrompt.DefaultSubmitPrompt)
{
    /// <summary>Port of <c>DEFAULT_HANDOFF_PROMPT</c> (used only when a handoff tool is present).</summary>
    public const string DefaultHandoffPrompt =
        "\nYou are part of a multi-agent system designed to make agent coordination and execution easy. Agents uses two primary abstraction: **Agents** and **Handoffs**. "
        + "An agent encompasses instructions and tools and can hand off a conversation to another agent when appropriate. Handoffs are achieved by calling a handoff function,"
        + "generally named `transfer_to_<agent_name>`. Transfers between agents are handled seamlessly in the background; do not mention or draw attention to these transfers in your conversation with the user.\n";

    /// <summary>Port of <c>PARALLEL_TOOLS_PROMPT</c>.</summary>
    public const string ParallelToolsPrompt =
        "Prioritize parallel tool calls: when operations are independent, run them in one response — e.g. reading several files or running several searches at once — rather than one at a time. Only sequence calls when one depends on another's result.";

    /// <summary>Port of <c>DEFAULT_ASSISTANT_PROMPT</c>.</summary>
    public const string DefaultAssistantPrompt =
        "\nYou are a helpful assistant attempting to submit the best possible answer. You have several tools available to help with finding the answer. "
        + "You will see the result of tool calls right after sending the message. " + ParallelToolsPrompt
        + " Do some reasoning before your actions, describing what tool calls you are going to use and how they fit into your plan.\n";

    /// <summary>Port of <c>DEFAULT_SUBMIT_PROMPT</c>; <c>{submit}</c> is the submit tool's name.</summary>
    public const string DefaultSubmitPrompt = "\nWhen you have completed the task and have an answer, call the {submit}() tool to report it.\n";

    /// <summary>Port of <c>DEFAULT_CONTINUE_PROMPT</c>; <c>{submit}</c> is the submit tool's name.</summary>
    public const string DefaultContinuePrompt =
        "\nPlease proceed to the next step using your best judgement. If you believe you have completed the task, please call the `{submit}()` tool with your final answer.\n";

    /// <summary>Port of <c>DEFAULT_CONTINUE_PROMPT_NO_SUBMIT</c>.</summary>
    public const string DefaultContinuePromptNoSubmit = "\nPlease proceed to the next step using your best judgement.\n";

    /// <summary>Python's <c>AgentPrompt()</c>: no instructions, default handoff, assistant and submit prompts.</summary>
    public static AgentPrompt Default { get; } = new();

    /// <summary>
    /// Python's <c>prompt=None</c>: the agent adds no system message. Compared by reference, so a
    /// hand-built prompt with every piece null still produces an (empty) system message like Python.
    /// </summary>
    public static AgentPrompt None { get; } = new(null, null, null, null);

    /// <summary>Python's <c>prompt="..."</c> shorthand: instructions with the default handoff, assistant and submit prompts.</summary>
    public static implicit operator AgentPrompt(string instructions) => new(instructions);
}

/// <summary>Port of <c>agent/_types.py</c> <c>AgentSubmit</c>: how the react agent's submit tool is configured.</summary>
public sealed record AgentSubmit
{
    /// <summary>Name for the submit tool (defaults to the tool's own name, "submit" for the built-in one).</summary>
    public string? Name { get; init; }

    /// <summary>Description of the submit tool (defaults to the tool's own description).</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Alternate implementation of the submit tool. It should return the answer it was given so the react
    /// agent can score it; <see cref="Name"/> and <see cref="Description"/> override the tool's own when set.
    /// </summary>
    public ToolDef? Tool { get; init; }

    /// <summary>Set the completion to the submitted answer only (default: append it to what the model wrote alongside the call).</summary>
    public bool AnswerOnly { get; init; }

    /// <summary>Delimiter between the model's own content and the submitted answer.</summary>
    public string AnswerDelimiter { get; init; } = "\n\n";

    /// <summary>
    /// Keep the submit tool call in the message history. Defaults to false so the final assistant message reads
    /// like a plain reply; leave it false in multi-agent systems, where a parent watching for its own submit
    /// tool would otherwise stop early.
    /// </summary>
    public bool KeepInMessages { get; init; }

    /// <summary>Python's <c>submit=False</c>: no submit tool; the agent stops when the model makes no tool calls.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Python's <c>submit=True</c> / <c>None</c>.</summary>
    public static AgentSubmit Default { get; } = new();

    /// <summary>Python's <c>submit=False</c>.</summary>
    public static AgentSubmit Disabled { get; } = new() { Enabled = false };
}

/// <summary>
/// Port of the <c>AgentContinue</c> return union (<c>bool | str | AgentState</c>): continue with the default
/// message, stop, continue with a custom message, or continue from a replacement state.
/// </summary>
public abstract record AgentContinueResult
{
    private AgentContinueResult()
    {
    }

    /// <summary>Python <c>True</c>: keep going; the default continue message is added only when the model made no tool calls.</summary>
    public static AgentContinueResult Continue { get; } = new ContinueResult();

    /// <summary>Python <c>False</c>: leave the loop.</summary>
    public static AgentContinueResult Stop { get; } = new StopResult();

    /// <summary>Python <c>str</c>: keep going after appending this user message (<c>{submit}</c> is replaced with the submit tool's name).</summary>
    public static AgentContinueResult Message(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new MessageResult(text);
    }

    /// <summary>Python <c>AgentState</c>: keep going with this state's messages and output.</summary>
    public static AgentContinueResult WithState(AgentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new StateResult(state);
    }

    public static implicit operator AgentContinueResult(bool value) => value ? Continue : Stop;

    public static implicit operator AgentContinueResult(string message) => Message(message);

    public static implicit operator AgentContinueResult(AgentState state) => WithState(state);

    /// <summary>Continue with the default message when the model made no tool calls.</summary>
    public sealed record ContinueResult : AgentContinueResult;

    /// <summary>Leave the loop.</summary>
    public sealed record StopResult : AgentContinueResult;

    /// <summary>Continue after appending <see cref="Text"/> as a user message.</summary>
    public sealed record MessageResult(string Text) : AgentContinueResult;

    /// <summary>Continue from <see cref="State"/>.</summary>
    public sealed record StateResult(AgentState State) : AgentContinueResult;
}

/// <summary>
/// Port of <c>agent/_types.py</c> <c>AgentContinue</c>: called on every iteration of the react loop to decide
/// whether it continues and what, if anything, is played back to the model.
/// </summary>
public delegate Task<AgentContinueResult> AgentContinue(AgentState state, CancellationToken cancellationToken);

/// <summary>Port of the callable form of <c>AgentAttempts.incorrect_message</c>: builds the reply to an incorrect submission from the state and its scores.</summary>
public delegate Task<string> AgentIncorrectMessage(AgentState state, IReadOnlyList<Score> scores, CancellationToken cancellationToken);

/// <summary>
/// Port of the <c>Agent</c> form of the react agent's <c>model</c> parameter: an agent with a <c>tools</c>
/// parameter that generates the next assistant message (appending it to the state and setting the output)
/// in place of <see cref="Model.GenerateAsync(IReadOnlyList{Provider.Core.ChatMessage}, IReadOnlyList{ToolDef}, Provider.Core.ToolChoice?, Provider.Core.GenerateConfig?, Provider.Core.StreamHandler?, CancellationToken)"/>.
/// </summary>
public delegate Task<AgentState> AgentModel(AgentState state, IReadOnlyList<ToolDef> tools, CancellationToken cancellationToken);

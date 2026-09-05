using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tools.Builtin;

public static partial class BuiltinTools
{
    /// <summary>The <c>todo_write</c> tool description (synthesised from Claude Code and Codex CLI practice).</summary>
    public const string TodoWriteDescription =
        "Update the task plan.\n\n"
        + "Use this tool to create and manage a structured task list for tracking progress on complex work. A good plan breaks the task into meaningful, logically ordered steps that are easy to verify as you go.\n\n"
        + "Provide a list of todo items, each with content and status. Optionally provide an explanation when making significant changes to the plan.\n\n"
        + "## When to Use\n\n"
        + "- The task requires multiple actions or has logical phases where sequencing matters.\n"
        + "- The work has ambiguity that benefits from outlining high-level goals.\n"
        + "- The user has asked you to do more than one thing in a single prompt.\n"
        + "- You generate additional steps while working and plan to do them before finishing.\n\n"
        + "## When NOT to Use\n\n"
        + "- The task is a single straightforward action.\n"
        + "- The work is trivial or can be completed in fewer than 3 steps.\n"
        + "- The task is purely conversational or informational.\n\n"
        + "Do not pad out simple work with filler steps or state the obvious. The content of your plan should not involve doing anything that you aren't capable of doing. Aim for 3-7 items unless the task genuinely needs more.\n\n"
        + "## Status Management\n\n"
        + "- **pending**: Step not yet started.\n"
        + "- **in_progress**: Currently working on this step.\n"
        + "- **completed**: Step finished successfully.\n\n"
        + "Update status in real-time as you work. Exactly one step should be in_progress at a time. Mark a step as in_progress before beginning it, and completed immediately after finishing — don't batch completions after the fact. Never jump a step from pending directly to completed.\n\n"
        + "Only mark a step as completed when you have fully accomplished it. Never mark a step as completed if:\n\n"
        + "- Errors remain unresolved.\n"
        + "- The implementation is partial or untested.\n"
        + "- You encountered blockers you haven't worked around.\n\n"
        + "If you cannot finish a step, keep it as in_progress and note the issue.\n\n"
        + "Remove steps that are no longer relevant from the list entirely. If understanding changes mid-task, update the plan before continuing and provide an explanation of the rationale.\n\n"
        + "Do not repeat the full contents of the plan after calling this tool — the harness already displays it. Instead, summarize the change and highlight any important context or next step.\n\n"
        + "## Plan Quality\n\n"
        + "High-quality plans:\n\n"
        + "- Identify relevant sources, inputs, and constraints to inspect\n"
        + "- Evaluate each candidate approach against the task requirements\n"
        + "- Cross-check important findings against available evidence\n"
        + "- Summarize conclusions, remaining uncertainty, and next action\n\n"
        + "Low-quality plans:\n\n"
        + "- Look things up\n"
        + "- Compare options\n"
        + "- Write summary\n\n"
        + "If you need to write a plan, only write high quality plans, not low quality ones.";

    /// <summary>The default <c>update_plan</c> tool description (taken from the GPT 5.1 system prompt for Codex).</summary>
    public const string UpdatePlanDescription =
        "Update the task plan.\n\n"
        + "You have access to an update_plan tool which tracks steps and progress and renders them to the user. Using the tool helps demonstrate that you've understood the task and convey how you're approaching it. Plans can help to make complex, ambiguous, or multi-phase work clearer and more collaborative for the user. A good plan should break the task into meaningful, logically ordered steps that are easy to verify as you go.\n\n"
        + "Provide an optional explanation and a list of plan items, each with a step and status. At most one step can be in_progress at a time.\n\n"
        + "Note that plans are not for padding out simple work with filler steps or stating the obvious. The content of your plan should not involve doing anything that you aren't capable of doing (i.e. don't try to test things that you can't test). Do not use plans for simple or single-step queries that you can just do or answer immediately.\n\n"
        + "Do not repeat the full contents of the plan after an update_plan call — the harness already displays it. Instead, summarize the change made and highlight any important context or next step.\n\n"
        + "Before running a command, consider whether or not you have completed the previous step, and make sure to mark it as completed before moving on to the next step. It may be the case that you complete all steps in your plan after a single pass of implementation. If this is the case, you can simply mark all the planned steps as completed. Sometimes, you may need to change plans in the middle of a task: call update_plan with the updated plan and make sure to provide an explanation of the rationale when doing so.\n\n"
        + "Maintain statuses in the tool: exactly one item in_progress at a time; mark items complete when done; post timely status transitions. Do not jump an item from pending to completed: always set it to in_progress first. Do not batch-complete multiple items after the fact. Finish with all items completed or explicitly canceled/deferred before ending the turn. Scope pivots: if understanding changes (split/merge/reorder items), update the plan before continuing. Do not let the plan go stale while coding.\n\n"
        + "Use a plan when:\n\n"
        + "- The task is non-trivial and will require multiple actions over a long time horizon.\n"
        + "- There are logical phases or dependencies where sequencing matters.\n"
        + "- The work has ambiguity that benefits from outlining high-level goals.\n"
        + "- You want intermediate checkpoints for feedback and validation.\n"
        + "- When the user asked you to do more than one thing in a single prompt\n"
        + "- The user has asked you to use the plan tool (aka \"TODOs\")\n"
        + "- You generate additional steps while working, and plan to do them before yielding to the user\n\n"
        + "### Examples\n\n"
        + "High-quality plans\n\n"
        + "Example 1:\n"
        + "- Add CLI entry with file args\n"
        + "- Parse Markdown via CommonMark library\n"
        + "- Apply semantic HTML template\n"
        + "- Handle code blocks, images, links\n"
        + "- Add error handling for invalid files\n\n"
        + "Example 2:\n"
        + "- Define CSS variables for colors\n"
        + "- Add toggle with localStorage state\n"
        + "- Refactor components to use variables\n"
        + "- Verify all views for readability\n"
        + "- Add smooth theme-change transition\n\n"
        + "Example 3:\n"
        + "- Set up Node.js + WebSocket server\n"
        + "- Add join/leave broadcast events\n"
        + "- Implement messaging with timestamps\n"
        + "- Add usernames + mention highlighting\n"
        + "- Persist messages in lightweight DB\n"
        + "- Add typing indicators + unread count\n\n"
        + "Low-quality plans\n\n"
        + "Example 1:\n"
        + "- Create CLI tool\n"
        + "- Add Markdown parser\n"
        + "- Convert to HTML\n\n"
        + "Example 2:\n"
        + "- Add dark mode toggle\n"
        + "- Save preference\n"
        + "- Make styles look good\n\n"
        + "Example 3:\n"
        + "- Create single-file HTML game\n"
        + "- Run quick sanity check\n"
        + "- Summarize usage instructions\n\n"
        + "If you need to write a plan, only write high quality plans, not low quality ones.";

    /// <summary>The step statuses of <c>TodoStep.status</c>.</summary>
    public static readonly IReadOnlyList<string> TodoStatuses = ["pending", "in_progress", "completed"];

    /// <summary>
    /// Port of <c>todo_write()</c> (<c>tool/_tools/_todo_write.py</c>): a planning tool that tracks steps and
    /// progress in longer horizon tasks. Each todo has a <c>content</c> and a <c>status</c> (one of
    /// <see cref="TodoStatuses"/>); the tool only records the call and answers "Todo list updated".
    /// </summary>
    public static ToolDef TodoWrite()
    {
        var parameters = PlanParameters(
            "todos",
            stepName: "content",
            stepDescription: "Step description.",
            statusEnum: TodoStatuses,
            explanationDescription: "Optional explanation of changes to the plan.");
        return new ToolDef("todo_write", TodoWriteDescription, parameters, (arguments, _) =>
        {
            ToolInputValidator.Validate(arguments, parameters);
            return Task.FromResult<ToolResult>("Todo list updated");
        })
        { Parallel = true };
    }

    /// <summary>
    /// Port of <c>update_plan()</c> (<c>tool/_tools/_update_plan.py</c>): the Codex CLI planning tool. Each
    /// plan item has a <c>step</c> and a free-form <c>status</c> (documented as pending, in_progress or
    /// completed but not enforced, as in Python); the tool answers "Plan updated".
    /// </summary>
    /// <param name="description">Override the default tool description (empty falls back to the default, as in Python).</param>
    public static ToolDef UpdatePlan(string? description = null)
    {
        var parameters = PlanParameters(
            "plan",
            stepName: "step",
            stepDescription: "Step name.",
            statusEnum: null,
            explanationDescription: "Optional explanation.");
        return new ToolDef("update_plan", string.IsNullOrEmpty(description) ? UpdatePlanDescription : description, parameters, (arguments, _) =>
        {
            ToolInputValidator.Validate(arguments, parameters);
            return Task.FromResult<ToolResult>("Plan updated");
        })
        { Parallel = true };
    }

    /// <summary>The schema pydantic derives for <c>list[TodoStep]</c> / <c>list[PlanStep]</c> plus the optional explanation.</summary>
    private static ToolParams PlanParameters(string listName, string stepName, string stepDescription, IReadOnlyList<string>? statusEnum, string explanationDescription) => new()
    {
        Properties = new Dictionary<string, ToolParam>
        {
            [listName] = new ToolParam
            {
                Type = ["array"],
                Description = "The list of steps.",
                Items = new ToolParam
                {
                    Type = ["object"],
                    Properties = new Dictionary<string, ToolParam>
                    {
                        [stepName] = ToolParam.Of("string", stepDescription),
                        ["status"] = new ToolParam
                        {
                            Type = ["string"],
                            Description = "One of: pending, in_progress, completed",
                            Enum = statusEnum?.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray(),
                        },
                    },
                    AdditionalProperties = false,
                    Required = [stepName, "status"],
                },
            },
            ["explanation"] = new ToolParam
            {
                Description = explanationDescription,
                AnyOf = [ToolParam.Of("string"), ToolParam.Of("null")],
            },
        },
        Required = [listName],
    };
}

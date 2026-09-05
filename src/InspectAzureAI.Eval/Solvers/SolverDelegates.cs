using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Solvers;

/// <summary>Port of the <c>tool_calls</c> literal of <c>solver/_solver.py</c> <c>Generate</c>: "loop", "single" or "none".</summary>
public enum ToolCallsMode
{
    /// <summary>Resolve tool calls and generate again until the model stops calling tools (Python "loop").</summary>
    Loop,

    /// <summary>Resolve at most one round of tool calls and return (Python "single").</summary>
    Single,

    /// <summary>Do not resolve tool calls at all (Python "none").</summary>
    None,
}

/// <summary>Port of <c>solver/_solver.py</c> <c>Generate</c>: generate with the task model and add the assistant message (and any tool results) to the state.</summary>
public delegate Task<TaskState> Generate(
    TaskState state,
    ToolCallsMode toolCalls = ToolCallsMode.Loop,
    GenerateConfig? config = null,
    CancellationToken cancellationToken = default);

/// <summary>Port of <c>solver/_solver.py</c> <c>Solver</c>: transforms a <see cref="TaskState"/>, optionally calling <paramref name="generate"/>.</summary>
public delegate Task<TaskState> Solver(TaskState state, Generate generate, CancellationToken cancellationToken);

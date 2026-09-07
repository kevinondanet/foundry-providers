// ============================================================================
//  LAYER 4: THE TASK-AUTHORING API (part 2: solvers and TaskState)
//  Python: inspect_ai/solver
//
//  A solver is an async function `(state, generate) -> state`. It receives
//  the sample's working memory (TaskState), does something — usually asks
//  the model — and returns the updated state. Solvers are composed with
//  `chain()`; each step is recorded as a StepEvent in the transcript.
//
//  Notice the second parameter. `generate` is the engine's tool-calling loop
//  (layer 3, TaskGenerate.cs) handed DOWN to the solver as a callback. The
//  solver calls it without importing `_eval`, which is how "solvers call the
//  model" holds even for the loop the engine owns. Dependency injection
//  instead of an upward import.
// ============================================================================
using inspect_ai._util.display;
using inspect_ai._util.registry;
using inspect_ai.dataset;
using inspect_ai.log;
using inspect_ai.model;
using inspect_ai.tool;

namespace inspect_ai.solver;

/// <summary>The sample's working memory (Python: TaskState).</summary>
public sealed class TaskState(Sample sample, int epoch = 1)
{
    public string SampleId { get; } = sample.Id;
    public int Epoch { get; } = epoch;
    public string Input { get; } = sample.Input;
    public string Target { get; } = sample.Target;

    /// <summary>The conversation so far. Solvers append to it; generate() appends the model's replies.</summary>
    public List<ChatMessage> Messages { get; } = new() { ChatMessage.User(sample.Input) };

    /// <summary>Tools the model may call, as set by use_tools().</summary>
    public List<Tool> Tools { get; } = new();

    /// <summary>The most recent model output, or null before the first generate.</summary>
    public ModelOutput? Output { get; set; }

    /// <summary>A solver may set this to stop the chain early.</summary>
    public bool Completed { get; set; }
}

/// <summary>Python: `Generate = Callable[[TaskState], Awaitable[TaskState]]` — injected by the engine.</summary>
public delegate Task<TaskState> Generate(TaskState state);

/// <summary>Python: `Solver = Callable[[TaskState, Generate], Awaitable[TaskState]]`.</summary>
public delegate Task<TaskState> Solver(TaskState state, Generate generate);

/// <summary>Python's `@solver` decorator.</summary>
public sealed class SolverAttribute(string name) : RegistryAttribute(RegistryType.Solver, name);

public static class Solvers
{
    /// <summary>Prepend a system message (Python: system_message()).</summary>
    [Solver("system_message")]
    public static Solver system_message(string text) => (state, _) =>
    {
        Display.Step("L4 solver", $"system_message: inserting system prompt ({text.Length} chars)");
        state.Messages.Insert(0, ChatMessage.System(text));
        return Task.FromResult(state);
    };

    /// <summary>Make tools available to the model (Python: use_tools()).</summary>
    [Solver("use_tools")]
    public static Solver use_tools(params Tool[] tools) => (state, _) =>
    {
        Display.Step("L4 solver", $"use_tools: {string.Join(", ", tools.Select(t => t.Name))}");
        state.Tools.AddRange(tools);
        return Task.FromResult(state);
    };

    /// <summary>Call the model, running tool calls until it stops (Python: generate()).
    /// The work is done by the injected `generate` callback from layer 3.</summary>
    [Solver("generate")]
    public static Solver generate() => (state, generate) =>
    {
        Display.Step("L4 solver", "generate: handing the state to the engine-provided generate loop");
        return generate(state);
    };

    /// <summary>Run solvers in order, stopping early if one marks the state complete (Python: chain()).</summary>
    public static Solver chain(params Solver[] solvers) => async (state, generate) =>
    {
        foreach (var solver in solvers)
        {
            // Solvers are lambdas; recover "system_message" from the compiler's "<system_message>b__0" name.
            var raw = solver.Method.Name;
            var name = raw.StartsWith('<') ? raw[1..raw.IndexOf('>')] : raw;
            Transcript.transcript().Emit(new StepEvent("L4 solver", name, "begin"));
            state = await solver(state, generate);
            Transcript.transcript().Emit(new StepEvent("L4 solver", name, "end"));
            if (state.Completed) break;
        }
        return state;
    };
}

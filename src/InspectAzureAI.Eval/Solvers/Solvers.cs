using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Solvers;

/// <summary>
/// Port of the built-in solvers of <c>solver/_chain.py</c>, <c>_use_tools.py</c> and the <c>generate()</c>
/// solver of <c>solver/_solver.py</c> (prompt solvers live in <c>PromptSolvers.cs</c>, the ReAct agent in
/// <c>BasicAgent.cs</c>).
/// </summary>
public static partial class Solvers
{
    /// <summary>
    /// Port of <c>chain()</c>: runs the solvers in turn, stopping early once a solver marks the state
    /// completed. Nested chains are unrolled like Python's <c>unroll</c>.
    /// </summary>
    public static Solver Chain(params Solver[] solvers)
    {
        ArgumentNullException.ThrowIfNull(solvers);
        var unrolled = new List<Solver>();
        foreach (var solver in solvers)
        {
            ArgumentNullException.ThrowIfNull(solver, nameof(solvers));
            if (solver.Target is ChainSolver nested)
            {
                unrolled.AddRange(nested.Solvers);
            }
            else
            {
                unrolled.Add(solver);
            }
        }

        return new ChainSolver(unrolled).SolveAsync;
    }

    /// <summary>Port of <c>use_tools(*tools)</c>: replaces the state's tools; <c>tool_choice</c> is left unchanged.</summary>
    public static Solver UseTools(params ToolDef[] tools) => UseTools(tools, null);

    /// <summary>
    /// Port of <c>use_tools(tools, tool_choice, append)</c>: an empty list leaves the tools untouched; a null
    /// <paramref name="toolChoice"/> leaves the choice untouched (Python defaults to "auto" there).
    /// </summary>
    public static Solver UseTools(IReadOnlyList<ToolDef> tools, ToolChoice? toolChoice = null, bool append = false)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var update = tools.ToArray();
        return (state, _, _) =>
        {
            if (update.Length > 0)
            {
                state.Tools = append ? [.. state.Tools, .. update] : [.. update];
            }

            if (toolChoice is not null)
            {
                state.ToolChoice = toolChoice;
            }

            return Task.FromResult(state);
        };
    }

    /// <summary>
    /// Port of the <c>generate()</c> solver: defers to the runner's <see cref="Generate"/> delegate.
    /// <paramref name="cache"/> is Python's <c>cache</c> kwarg (<c>true</c> converts to <see cref="CachePolicy.Default"/>).
    /// </summary>
    public static Solver Generate(ToolCallsMode toolCalls = ToolCallsMode.Loop, GenerateConfig? config = null, CachePolicy? cache = null) =>
        (state, generate, cancellationToken) => generate(state, toolCalls, config, cache, cancellationToken);

    /// <summary>Port of the <c>Chain</c> class; kept as an object so nested chains can be recognised and unrolled.</summary>
    private sealed class ChainSolver(IReadOnlyList<Solver> solvers)
    {
        public IReadOnlyList<Solver> Solvers { get; } = solvers;

        public async Task<TaskState> SolveAsync(TaskState state, Generate generate, CancellationToken cancellationToken)
        {
            foreach (var solver in Solvers)
            {
                state = await SolverTranscript.RunAsync(solver, state, generate, cancellationToken).ConfigureAwait(false);
                if (state.Completed)
                {
                    break;
                }
            }

            return state;
        }
    }
}

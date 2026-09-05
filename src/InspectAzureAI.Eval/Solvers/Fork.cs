using InspectAzureAI.Eval.Context;

namespace InspectAzureAI.Eval.Solvers;

/// <summary>Port of <c>solver/_fork.py</c>: <c>fork()</c>.</summary>
public static partial class Solvers
{
    /// <summary>
    /// Port of <c>fork(state, solver)</c>: runs <paramref name="solver"/> against an independent copy of
    /// <paramref name="state"/> (own message list, metadata, tools, choices and <see cref="Store"/>) inside a
    /// <c>subtask</c> span, with the ambient <see cref="SampleContext"/> store swapped for the copy's. The original
    /// state is untouched. Python reads the task's generate from a context variable; here the solver's own
    /// <paramref name="generate"/> is passed explicitly, so fork also works outside a running sample (then without a span).
    /// </summary>
    public static Task<TaskState> Fork(TaskState state, Solver solver, Generate generate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(solver);
        ArgumentNullException.ThrowIfNull(generate);
        return SolverSubtaskAsync(state, solver, generate, cancellationToken);
    }

    /// <summary>
    /// Port of <c>fork(state, solvers)</c>: every solver gets its own copy of <paramref name="state"/> and they run in
    /// parallel; the results are returned in the order of <paramref name="solvers"/>. The first failure propagates
    /// once every branch has settled.
    /// </summary>
    public static async Task<IReadOnlyList<TaskState>> Fork(TaskState state, IReadOnlyList<Solver> solvers, Generate generate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(solvers);
        ArgumentNullException.ThrowIfNull(generate);
        var branches = solvers.Select(solver =>
        {
            ArgumentNullException.ThrowIfNull(solver, nameof(solvers));
            return SolverSubtaskAsync(state, solver, generate, cancellationToken);
        }).ToArray();
        return await Task.WhenAll(branches).ConfigureAwait(false);
    }

    /// <summary>Port of <c>solver_subtask</c>: the copy, the branch context and the span; a chain is named "chain" like Python, any other solver "fork".</summary>
    private static async Task<TaskState> SolverSubtaskAsync(TaskState state, Solver solver, Generate generate, CancellationToken cancellationToken)
    {
        var copy = state.Copy();
        var parent = SampleContext.Current;
        if (parent is null)
        {
            return await solver(copy, generate, cancellationToken).ConfigureAwait(false);
        }

        var branch = new SampleContext
        {
            ActiveModel = parent.ActiveModel,
            Store = copy.Store,
            Transcript = parent.Transcript,
            Limits = parent.Limits,
            Sandboxes = parent.Sandboxes,
            SampleState = parent.SampleState,
            Scorer = parent.Scorer,
        };
        using var scope = SampleContext.Begin(branch);
        using var span = parent.Transcript.Span(solver.Target is ChainSolver ? "chain" : "fork", "subtask");
        return await solver(copy, generate, cancellationToken).ConfigureAwait(false);
    }
}

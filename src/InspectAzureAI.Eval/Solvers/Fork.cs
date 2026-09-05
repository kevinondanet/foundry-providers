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
    /// parallel; the results are returned in the order of <paramref name="solvers"/>. Like Python's <c>tg_collect</c>,
    /// the first failure cancels the other branches and propagates once they have settled.
    /// </summary>
    public static async Task<IReadOnlyList<TaskState>> Fork(TaskState state, IReadOnlyList<Solver> solvers, Generate generate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(solvers);
        ArgumentNullException.ThrowIfNull(generate);
        var branches = solvers.Select(solver =>
        {
            ArgumentNullException.ThrowIfNull(solver, nameof(solvers));
            return (Func<CancellationToken, Task<TaskState>>)(ct => SolverSubtaskAsync(state, solver, generate, ct));
        }).ToArray();
        return await TgCollect.RunAsync(branches, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Port of <c>solver_subtask</c>: the copy runs as a <see cref="Subtask"/> of type <c>fork</c> (own store, branch
    /// context, <c>subtask</c> span and <see cref="SubtaskEvent"/>); a plain solver also gets its <c>solver</c> span and
    /// <see cref="StateEvent"/> like Python, a chain records those per step. The subtask is named "chain" for a chain
    /// like Python and "fork" otherwise (Python uses the solver's registry name).
    /// </summary>
    private static async Task<TaskState> SolverSubtaskAsync(TaskState state, Solver solver, Generate generate, CancellationToken cancellationToken)
    {
        var copy = state.Copy();
        var isChain = solver.Target is ChainSolver;
        return await Subtask.RunAsync(
            isChain ? "chain" : "fork",
            ct => isChain ? solver(copy, generate, ct) : SolverTranscript.RunAsync(solver, copy, generate, ct),
            store: copy.Store,
            type: "fork",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

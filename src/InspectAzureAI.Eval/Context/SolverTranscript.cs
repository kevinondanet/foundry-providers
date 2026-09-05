using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Solvers;

namespace InspectAzureAI.Eval.Context;

/// <summary>
/// Port of <c>solver/_transcript.py</c> <c>SolverTranscript</c> / <c>solver_transcript</c>: snapshots a
/// <see cref="TaskState"/> (as <see cref="Jsonable.FromState"/>) before a solver runs and records the difference
/// as a <see cref="StateEvent"/> once it completes — the same points Python records them: after every step of a
/// chain, after each plan-level solver, and inside a fork branch.
/// </summary>
public sealed class SolverTranscript
{
    private readonly JsonObject _before;

    private readonly Transcript? _transcript;

    /// <summary>Snapshots <paramref name="beforeState"/>; events go to <paramref name="transcript"/>, or the ambient one at completion.</summary>
    public SolverTranscript(TaskState beforeState, Transcript? transcript = null)
    {
        ArgumentNullException.ThrowIfNull(beforeState);
        _before = Jsonable.FromState(beforeState);
        _transcript = transcript;
    }

    /// <summary>Port of <c>SolverTranscript.complete</c>: records a <see cref="StateEvent"/> when the state changed (nothing without a transcript).</summary>
    public void Complete(TaskState afterState)
    {
        ArgumentNullException.ThrowIfNull(afterState);
        var transcript = _transcript ?? SampleContext.Current?.Transcript;
        if (transcript is null)
        {
            return;
        }

        var changes = JsonChanges.Diff(_before, Jsonable.FromState(afterState));
        if (changes is not null)
        {
            transcript.Add(StateEvent.FromChanges(changes));
        }
    }

    /// <summary>
    /// Port of <c>async with solver_transcript(solver, state) as st: state = await solver(...); st.complete(state)</c>:
    /// runs <paramref name="solver"/> inside a span of type <c>solver</c> named <paramref name="name"/> (the
    /// solver's <see cref="InspectAzureAI.Eval.Solvers.Solvers.LogName"/> by default) and records the state change. Without an ambient
    /// context the solver simply runs.
    /// </summary>
    public static async Task<TaskState> RunAsync(Solver solver, TaskState state, Generate generate, CancellationToken cancellationToken, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(solver);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(generate);
        var transcript = SampleContext.Current?.Transcript;
        using var span = transcript?.Span(name ?? Solvers.Solvers.LogName(solver), "solver");
        var solverTranscript = transcript is null ? null : new SolverTranscript(state, transcript);
        var result = await solver(state, generate, cancellationToken).ConfigureAwait(false);
        solverTranscript?.Complete(result);
        return result;
    }
}

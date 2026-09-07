namespace InspectAzureAI.Eval.Runner.EvalSet;

/// <summary>Port of <c>hooks/_hooks.py</c> <c>EvalSetStart</c>: the id (stable across invocations on the same log directory) and log directory of a starting eval set.</summary>
public sealed record EvalSetStart(string EvalSetId, string LogDir);

/// <summary>Port of <c>hooks/_hooks.py</c> <c>EvalSetEnd</c>.</summary>
public sealed record EvalSetEnd(string EvalSetId, string LogDir);

/// <summary>
/// An explicit eval-set hook seam alongside <c>Hooks.OnEvalSetStartAsync</c> / <c>OnEvalSetEndAsync</c> of the registered
/// <see cref="InspectAzureAI.Eval.Hooks.Hooks"/> (which <see cref="EvalSet.RunAsync"/> notifies first): an implementation passed
/// through <see cref="EvalSetOptions.Hooks"/> sees start before any task runs and end after the final status is reported —
/// not when the run fails with an exception, as in Python.
/// </summary>
public interface IEvalSetHooks
{
    Task OnEvalSetStartAsync(EvalSetStart data, CancellationToken cancellationToken);

    Task OnEvalSetEndAsync(EvalSetEnd data, CancellationToken cancellationToken);
}

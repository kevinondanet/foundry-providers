namespace InspectAzureAI.Eval.Runner.EvalSet;

/// <summary>Port of <c>hooks/_hooks.py</c> <c>EvalSetStart</c>: the id (stable across invocations on the same log directory) and log directory of a starting eval set.</summary>
public sealed record EvalSetStart(string EvalSetId, string LogDir);

/// <summary>Port of <c>hooks/_hooks.py</c> <c>EvalSetEnd</c>.</summary>
public sealed record EvalSetEnd(string EvalSetId, string LogDir);

/// <summary>
/// The eval-set hook seam (port of <c>Hooks.on_eval_set_start</c> / <c>on_eval_set_end</c>). This port has no
/// registry of hooks, so an implementation is passed explicitly through <see cref="EvalSetOptions.Hooks"/>;
/// <see cref="EvalSet.RunAsync"/> emits start before any task runs and end after the final status is reported —
/// not when the run fails with an exception, as in Python.
/// </summary>
public interface IEvalSetHooks
{
    Task OnEvalSetStartAsync(EvalSetStart data, CancellationToken cancellationToken);

    Task OnEvalSetEndAsync(EvalSetEnd data, CancellationToken cancellationToken);
}

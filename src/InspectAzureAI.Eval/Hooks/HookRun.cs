using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Hooks;

/// <summary>
/// The run-level state behind the emissions of <c>_eval/eval.py</c> (<c>emit_run_start</c> / <c>emit_run_end</c>)
/// and <c>_eval/task/run.py</c> (<c>emit_task_start</c> / <c>emit_task_end</c>) for one <c>Eval.RunAsync</c>: the run
/// id (minted here so a failure before the spec exists still has one, as Python's <c>run_id = uuid()</c> does),
/// the hooks that see the run (the registry snapshot plus <c>EvalOptions.Hooks</c>), the logs produced, and an
/// idempotent end so the runner's wrapper can report an exception exactly once.
/// </summary>
internal sealed class HookRun
{
    private readonly List<EvalLog> _logs = [];

    private bool _ended;

    public HookRun(EvalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        EvalSetId = options.EvalSetId;
        RunId = ShortUuid.Generate();
        Hooks = options.Hooks is { Count: > 0 } extra ? [.. HookRegistry.All, .. extra] : HookRegistry.All;
    }

    public string? EvalSetId { get; }

    public string RunId { get; }

    /// <summary>The registry hooks followed by the run's own, resolved once at run start.</summary>
    public IReadOnlyList<Hooks> Hooks { get; }

    /// <summary>The task spec, once <see cref="StartAsync"/> has run (its <c>EvalId</c> and <c>Task</c> stamp every sample payload).</summary>
    public EvalSpec? Spec { get; private set; }

    /// <summary>The logs the run produced so far (one per <see cref="TaskEndAsync"/>).</summary>
    public IReadOnlyList<EvalLog> Logs => _logs;

    /// <summary>Emits run start (task names = the spec's task) then task start, in Python's order.</summary>
    public async Task StartAsync(EvalSpec spec, EvalPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(plan);
        Spec = spec;
        await HookEmitter.EmitRunStartAsync(EvalSetId, RunId, [spec.Task], Hooks, cancellationToken).ConfigureAwait(false);
        await HookEmitter.EmitTaskStartAsync(spec, plan, Hooks, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records the written log and emits task end. Not cancellable: the log exists whatever happened to the run.</summary>
    public async Task TaskEndAsync(EvalLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _logs.Add(log);
        await HookEmitter.EmitTaskEndAsync(log, Hooks, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Emits run end once, with the logs collected so far and the exception that escaped the run (if any).</summary>
    public async Task EndAsync(Exception? exception)
    {
        if (_ended)
        {
            return;
        }

        _ended = true;
        await HookEmitter.EmitRunEndAsync(EvalSetId, RunId, _logs.ToArray(), exception, Hooks, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>The per-attempt emitter for one sample; requires <see cref="StartAsync"/> to have run.</summary>
    public SampleHooks Sample(Sample sample, TaskState state, int attempt, bool isFirstAttempt)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(state);
        var spec = Spec ?? throw new InvalidOperationException("The run's hooks have not been started (HookRun.StartAsync).");
        return new SampleHooks(this, spec, sample, state, attempt, isFirstAttempt);
    }
}

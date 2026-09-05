using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Hooks;

/// <summary>
/// The run-level emissions of <c>_eval/eval.py</c> for one pass of an eval set. Python's <c>eval_set</c> calls
/// <c>eval()</c> once per pass with every task, so the tasks of a pass share one <c>run_id</c>, one
/// <c>run_start</c> naming every task and one <c>run_end</c> carrying the pass's logs; a <see cref="HookRun"/>
/// created with a group stamps the group's run id on its spec and emits task start/end only. The group's hooks are
/// resolved once, as a single run's are (the registry snapshot plus <c>EvalOptions.Hooks</c>).
/// </summary>
internal sealed class HookRunGroup
{
    private bool _ended;

    public HookRunGroup(string? evalSetId, EvalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        EvalSetId = evalSetId;
        RunId = ShortUuid.Generate();
        Hooks = HookRun.ResolveHooks(options);
    }

    public string? EvalSetId { get; }

    public string RunId { get; }

    /// <summary>The registry hooks followed by the run's own, resolved once at group creation.</summary>
    public IReadOnlyList<Hooks> Hooks { get; }

    /// <summary>Emits run start for the pass, naming every task it will run (Python's <c>emit_run_start(eval_set_id, run_id, resolved_tasks)</c>).</summary>
    public Task StartAsync(IReadOnlyList<string> taskNames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskNames);
        return HookEmitter.EmitRunStartAsync(EvalSetId, RunId, taskNames, Hooks, cancellationToken);
    }

    /// <summary>Emits run end once, with the pass's logs and the exception that escaped the pass (if any). Not cancellable, like <see cref="HookRun.EndAsync"/>.</summary>
    public async Task EndAsync(IReadOnlyList<EvalLog> logs, Exception? exception)
    {
        ArgumentNullException.ThrowIfNull(logs);
        if (_ended)
        {
            return;
        }

        _ended = true;
        await HookEmitter.EmitRunEndAsync(EvalSetId, RunId, logs, exception, Hooks, CancellationToken.None).ConfigureAwait(false);
    }
}

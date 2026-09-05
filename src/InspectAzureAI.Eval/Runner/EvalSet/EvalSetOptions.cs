namespace InspectAzureAI.Eval.Runner.EvalSet;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of the eval-set level arguments of <c>_eval/evalset.py</c> <c>eval_set()</c>. Everything a single eval
/// takes (model, limits, epochs, sample selection, error policy, log directory, reporter) comes from
/// <see cref="Eval"/>; the retry policy, the models to cross the tasks with and the directory rules live here.
/// </summary>
public sealed record EvalSetOptions
{
    /// <summary>The per-eval options every task of the set runs with; <see cref="EvalOptions.LogDir"/> is the set's storage scope.</summary>
    public required EvalOptions Eval { get; init; }

    /// <summary>Port of <c>model</c> as a list: every task runs once per model (default: <see cref="EvalOptions.Model"/> of <see cref="Eval"/>).</summary>
    public IReadOnlyList<Model>? Models { get; init; }

    /// <summary>
    /// Port of <c>retry_attempts</c> (default 10). With <see cref="RetryImmediate"/> it is the number of times a
    /// failing task is re-queued; otherwise the number of passes over the set before giving up.
    /// </summary>
    public int RetryAttempts { get; init; } = 10;

    /// <summary>Port of <c>retry_wait</c> (default 30 seconds): the wait before the second pass, doubled each pass and capped at one hour. Ignored with <see cref="RetryImmediate"/>.</summary>
    public TimeSpan? RetryWait { get; init; }

    /// <summary>Port of <c>retry_connections</c> (default 1.0, no reduction): <c>max_connections</c> is multiplied by this before each pass. Ignored with <see cref="RetryImmediate"/> or when adaptive connections are active.</summary>
    public double? RetryConnections { get; init; }

    /// <summary>Port of <c>retry_cleanup</c> (default true): delete the superseded log files of retried tasks.</summary>
    public bool RetryCleanup { get; init; } = true;

    /// <summary>
    /// Port of <c>retry_immediate</c> (default true): re-queue a task as soon as it fails, reusing its completed
    /// samples, instead of waiting for every task to finish and retrying the set with a backoff (the legacy batch
    /// mode, which also applies <see cref="RetryWait"/> and <see cref="RetryConnections"/>).
    /// </summary>
    public bool RetryImmediate { get; init; } = true;

    /// <summary>Port of <c>max_tasks</c>: tasks run in parallel (default: the greater of 10 and the number of models).</summary>
    public int? MaxTasks { get; init; }

    /// <summary>Port of <c>log_dir_allow_dirty</c>: tolerate logs of other eval sets in the log directory.</summary>
    public bool LogDirAllowDirty { get; init; }

    /// <summary>Port of <c>eval_set_id</c>: the set's id (default: the directory's recorded id, else a fresh one).</summary>
    public string? EvalSetId { get; init; }

    /// <summary>Hooks notified when the set starts and ends (see <see cref="IEvalSetHooks"/>).</summary>
    public IEvalSetHooks? Hooks { get; init; }

    /// <summary>The clock the batch-mode backoff waits on (default: the system clock); tests pass a fake.</summary>
    public TimeProvider? TimeProvider { get; init; }
}

/// <summary>Port of the <c>tuple[bool, list[EvalLog]]</c> <c>eval_set()</c> returns: whether every task succeeded, and one log per task and model.</summary>
public sealed record EvalSetResult(bool Success, IReadOnlyList<Log.EvalLog> Logs);

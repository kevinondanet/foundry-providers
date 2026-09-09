using System.Runtime.ExceptionServices;
using InspectAzureAI.Eval.Concurrency;
using InspectAzureAI.Eval.Hooks;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Runner.EvalSet;

using Concurrency = InspectAzureAI.Eval.Concurrency.Concurrency;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>_eval/task/resolved.py</c> <c>ResolvedTask</c> (and, with <see cref="PreviousLog"/> set,
/// <c>PreviousTask</c>): one task of an eval set paired with the model it runs against, its merged model roles, its
/// position in the set, the <c>task_id</c> its logs carry and the identifier that pairs it with those logs.
/// </summary>
public sealed record ResolvedTask(EvalTask Task, Model Model, ModelRoles? ModelRoles, int Sequence, string Id, string Identifier)
{
    /// <summary>Port of <c>PreviousTask.log</c>: the incomplete log of an earlier attempt whose completed samples this run reuses.</summary>
    public EvalLog? PreviousLog { get; init; }
}

/// <summary>
/// Port of <c>_eval/evalset.py</c> <c>eval_set()</c> and the task dispatcher of <c>_eval/run.py</c>
/// <c>run_task_retry_attempts</c>: runs a set of tasks (crossed with the models) until every one has a successful
/// log in the log directory. Each task is identified by <see cref="TaskIdentifier"/>; tasks whose latest log is
/// complete are returned as they are, tasks with an incomplete log are re-run reusing its completed samples, and
/// tasks without a log run for the first time. Failures are retried immediately per task
/// (<see cref="EvalSetOptions.RetryImmediate"/>) or by re-running the whole set with an exponential backoff.
/// </summary>
public static class EvalSet
{
    /// <summary>Python's default <c>retry_wait</c>.</summary>
    public static readonly TimeSpan DefaultRetryWait = TimeSpan.FromSeconds(30);

    /// <summary>Python's cap on a single backoff wait.</summary>
    public static readonly TimeSpan MaxRetryWait = TimeSpan.FromHours(1);

    /// <summary>
    /// Runs the set. Returns whether every task succeeded and one log per task and model — full logs for the tasks
    /// run in this call, headers (no samples) for those already complete in the directory. Cancellation waits for
    /// the tasks in flight to write their cancelled logs and then propagates. Deviation: Python's <c>eval_set(model=None)</c>
    /// runs tasks that carry their own <c>Task.model</c> and identifies each by that model; this port requires an eval-level
    /// model (<see cref="EvalSetOptions.Models"/> or <see cref="EvalOptions.Model"/>) — the eval-set model names the logs and
    /// enters the <see cref="TaskIdentifier"/> even for a task whose <see cref="EvalTask.Model"/> the runner then generates with.
    /// </summary>
    /// <exception cref="PrerequisiteError">No tasks, tasks that are not distinct, or foreign logs in the directory.</exception>
    public static async Task<EvalSetResult> RunAsync(IReadOnlyList<EvalTask> tasks, EvalSetOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(options);
        if (tasks.Count == 0)
        {
            throw new PrerequisiteError("Error: No inspect tasks were found.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(options.RetryAttempts, nameof(options));
        var retryWait = options.RetryWait ?? DefaultRetryWait;
        if (retryWait < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RetryWait must not be negative.");
        }

        var retryConnections = options.RetryConnections ?? 1.0;
        if (double.IsNaN(retryConnections) || retryConnections <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RetryConnections must be a positive number.");
        }

        var models = options.Models
            ?? (options.Eval.Model is { } defaultModel
                ? new[] { defaultModel }
                : throw new ArgumentException("An eval set needs a model: set EvalSetOptions.Models or EvalOptions.Model (a task-level EvalTask.Model does not identify the task in an eval set).", nameof(options)));
        if (models.Count == 0)
        {
            throw new ArgumentException("At least one model is required.", nameof(options));
        }

        var maxTasks = options.MaxTasks ?? Math.Max(models.Count, 10);
        if (maxTasks < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxTasks must be at least 1.");
        }

        var reporter = options.Eval.Reporter;
        var logDir = options.Eval.LogDir;
        Directory.CreateDirectory(logDir);
        var evalSetId = EvalSetInfo.EvalSetIdForLogDir(logDir, options.EvalSetId);
        var taskRetryAttempts = options.RetryImmediate ? options.RetryAttempts : 0;
        if (options.RetryImmediate && options.RetryAttempts == 0)
        {
            reporter?.Message("RetryImmediate has no effect when RetryAttempts is 0; no task-level retries will be performed.");
        }

        // adaptive connections subsume retry_connections: the controller scales down on retry signals itself
        var evalModel = options.Eval.Model ?? models[0];
        var adaptive = options.Eval.AdaptiveConnections ?? evalModel.AdaptiveConnections ?? AdaptiveConnections.FromConfigValue(evalModel.Config.AdaptiveConnections);
        if (Concurrency.AdaptiveActive(adaptive, evalModel.Config.MaxConnections, IsBatch(evalModel.Config.Batch)))
        {
            retryConnections = 1.0;
        }

        var maxConnections = StartingMaxConnections(models, evalModel.Config);
        var currentModels = models;
        var timeProvider = options.TimeProvider ?? TimeProvider.System;

        // Python's emit_eval_set_start / emit_eval_set_end reach the registered hooks; the IEvalSetHooks seam is notified after them
        var lifecycleHooks = HookRun.ResolveHooks(options.Eval);
        await HookEmitter.EmitEvalSetStartAsync(evalSetId, logDir, lifecycleHooks, cancellationToken).ConfigureAwait(false);
        if (options.Hooks is { } hooks)
        {
            await hooks.OnEvalSetStartAsync(new EvalSetStart(evalSetId, logDir), cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<EvalLog> results;
        if (options.RetryImmediate)
        {
            results = await TryEvalAsync().ConfigureAwait(false);
        }
        else
        {
            // Python's tenacity policy: stop_after_attempt(retry_attempts) passes, wait_exponential(retry_wait, max=1h)
            var attempts = Math.Max(1, options.RetryAttempts);
            for (var attempt = 1; ; attempt++)
            {
                results = await TryEvalAsync().ConfigureAwait(false);
                if (EvalSetLogs.AllEvalsSucceeded(results) || attempt >= attempts)
                {
                    break;
                }

                if (retryConnections != 1.0)
                {
                    maxConnections = Math.Max((int)Math.Round(maxConnections * retryConnections, MidpointRounding.ToEven), 1);
                    var reduced = maxConnections;
                    currentModels = currentModels.Select(model => model.WithConfig(model.Config with { MaxConnections = reduced })).ToList();
                }

                var wait = RetryWait(retryWait, attempt);
                reporter?.Message($"Evals not complete, waiting {Math.Round(wait.TotalSeconds)} seconds before retrying...");
                await Task.Delay(wait, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }

        if (options.RetryCleanup)
        {
            EvalSetLogs.CleanupOlderEvalLogs(logDir, results.Select(log => log.Eval.TaskId).ToHashSet(StringComparer.Ordinal), Warn);
        }

        var success = EvalSetLogs.AllEvalsSucceeded(results);
        reporter?.Message(success ? $"Completed all tasks in '{logDir}' successfully" : $"Did not successfully complete all tasks in '{logDir}'.");
        await HookEmitter.EmitEvalSetEndAsync(evalSetId, logDir, lifecycleHooks, cancellationToken).ConfigureAwait(false);
        if (options.Hooks is { } endHooks)
        {
            await endHooks.OnEvalSetEndAsync(new EvalSetEnd(evalSetId, logDir), cancellationToken).ConfigureAwait(false);
        }

        return new EvalSetResult(success, results);

        void Warn(string message) => reporter?.Message(message);

        // Port of try_eval: split the tasks into those with no log (first run), a complete log (returned as is)
        // and an incomplete log (re-run reusing its completed samples), then run what is left
        async Task<IReadOnlyList<EvalLog>> TryEvalAsync()
        {
            var resolvedTasks = ResolveTasks(tasks, currentModels, options.Eval);
            var allLogs = EvalSetLogs.ValidateEvalSetPrerequisites(resolvedTasks, EvalSetLogs.ListAllEvalLogs(logDir), options.LogDirAllowDirty);
            EvalSetInfo.Build(evalSetId, resolvedTasks, allLogs).Write(logDir);

            var limit = options.Eval.Limit;
            var reusable = allLogs
                .Where(log => !EvalSetLogs.ShuffleChanged(null, log.Header.Eval.Config, limit))
                .Select(log => log.TaskIdentifier)
                .ToHashSet(StringComparer.Ordinal);
            var pending = resolvedTasks.Where(task => !reusable.Contains(task.Identifier)).ToList();
            List<ResolvedTask> tasksToRun;
            List<EvalLog> successLogs = [];
            if (pending.Count == resolvedTasks.Count)
            {
                tasksToRun = pending;
            }
            else
            {
                var latest = EvalSetLogs.ListLatestEvalLogs(resolvedTasks, allLogs, options.Eval.Epochs, limit, options.Eval.SampleIds, options.RetryCleanup, Warn);
                successLogs = latest.Complete.Select(log => log.Header).ToList();
                var pendingIdentifiers = pending.Select(task => task.Identifier).ToHashSet(StringComparer.Ordinal);
                var failedLogs = latest.Incomplete.ToDictionary(log => log.TaskIdentifier, StringComparer.Ordinal);
                var failedTasks = resolvedTasks
                    .Where(task => failedLogs.ContainsKey(task.Identifier) && !pendingIdentifiers.Contains(task.Identifier))
                    .Select(task => AsPreviousTask(task, failedLogs[task.Identifier]))
                    .ToList();
                tasksToRun = [.. pending, .. failedTasks];
                if (tasksToRun.Count == 0)
                {
                    return successLogs;
                }
            }

            var runLogs = await RunEvalAsync(evalSetId, tasksToRun, taskRetryAttempts, maxTasks, options.Eval, Warn, cancellationToken).ConfigureAwait(false);
            return tasksToRun.Count == resolvedTasks.Count ? runLogs : [.. successLogs, .. runLogs];
        }
    }

    /// <summary>
    /// Port of <c>eval_resolve_tasks</c> for in-memory tasks: every task crossed with every model, in that order,
    /// with the roles merged (eval-level over task-level) and a fresh <c>task_id</c> each.
    /// </summary>
    public static IReadOnlyList<ResolvedTask> ResolveTasks(IReadOnlyList<EvalTask> tasks, IReadOnlyList<Model> models, EvalOptions evalOptions)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(evalOptions);
        var resolved = new List<ResolvedTask>(tasks.Count * models.Count);
        var evalRoles = ModelRoles.Resolve(evalOptions.ModelRoles);
        foreach (var task in tasks)
        {
            var roles = ModelRoles.Merge(ModelRoles.Resolve(task.ModelRoles), evalRoles);
            foreach (var model in models)
            {
                // the model's own config is the eval-level generate config of this run (see EvalModel in Eval.cs)
                var args = EvalSetArgsInTaskIdentifier.FromOptions(evalOptions) with { Config = model.Config };
                var identifier = TaskIdentifier.Compute(task, model, roles, args);
                resolved.Add(new ResolvedTask(task, model, roles, resolved.Count, ShortUuid.Generate(), identifier));
            }
        }

        return resolved;
    }

    /// <summary>Port of <c>as_previous_tasks</c>: the task re-run against its incomplete log, keeping that log's <c>task_id</c>.</summary>
    private static ResolvedTask AsPreviousTask(ResolvedTask task, EvalSetLog log)
    {
        var previous = EvalLogWriter.Read(log.Path);
        return task with { Id = previous.Eval.TaskId, PreviousLog = previous };
    }

    /// <summary>
    /// One pass over <paramref name="tasks"/> as one hook run: Python's <c>eval_set</c> calls <c>eval()</c> once per pass,
    /// so the pass's tasks share a run id, one run start naming every task and one run end carrying the pass's logs
    /// (the logs written so far when the pass throws).
    /// </summary>
    private static async Task<IReadOnlyList<EvalLog>> RunEvalAsync(
        string evalSetId,
        IReadOnlyList<ResolvedTask> tasks,
        int taskRetryAttempts,
        int maxTasks,
        EvalOptions evalOptions,
        Action<string> warn,
        CancellationToken cancellationToken)
    {
        var run = new HookRunGroup(evalSetId, evalOptions);
        await run.StartAsync(tasks.Select(task => task.Task.Name).ToList(), cancellationToken).ConfigureAwait(false);
        var logs = new List<EvalLog>();
        try
        {
            var results = await DispatchAsync(run, logs, evalSetId, tasks, taskRetryAttempts, maxTasks, evalOptions, warn, cancellationToken).ConfigureAwait(false);
            await run.EndAsync(results, null).ConfigureAwait(false);
            return results;
        }
        catch (Exception ex)
        {
            await run.EndAsync(logs, ex).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Port of <c>run_task_retry_attempts</c>: a model-balanced dispatcher that keeps up to <paramref name="maxTasks"/>
    /// tasks in flight, re-queues a task whose log comes back with an error (a fresh log entry, completed samples
    /// reused) until its retries are exhausted, and ends the run on cancellation. An exception escaping a task
    /// (a configuration error the runner raises before any sample runs) stops the dispatcher and propagates once
    /// the tasks in flight have finished.
    /// </summary>
    private static async Task<IReadOnlyList<EvalLog>> DispatchAsync(
        HookRunGroup run,
        List<EvalLog> logs,
        string evalSetId,
        IReadOnlyList<ResolvedTask> tasks,
        int taskRetryAttempts,
        int maxTasks,
        EvalOptions evalOptions,
        Action<string> warn,
        CancellationToken cancellationToken)
    {
        var pending = tasks.Select((task, index) => new PendingTask(index, task, taskRetryAttempts)).ToList();
        var results = new EvalLog?[tasks.Count];
        var inFlight = new Dictionary<Task<EvalLog>, PendingTask>();
        var modelCounts = new Dictionary<Model, int>(ReferenceEqualityComparer.Instance);
        foreach (var task in tasks)
        {
            modelCounts.TryAdd(task.Model, 0);
        }

        // the caller's cancellation and a task's configuration failure both stop the tasks in flight
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cancelled = false;
        Exception? failure = null;
        while (true)
        {
            while (!cancelled && failure is null && inFlight.Count < maxTasks && pending.Count > 0)
            {
                var item = PickBalanced(pending, modelCounts);
                modelCounts[item.Task.Model]++;
                inFlight[RunOneAsync(item)] = item;
            }

            if (inFlight.Count == 0)
            {
                break;
            }

            var completed = await Task.WhenAny(inFlight.Keys).ConfigureAwait(false);
            var finished = inFlight[completed];
            inFlight.Remove(completed);
            modelCounts[finished.Task.Model]--;
            EvalLog log;
            try
            {
                log = await completed.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                continue;
            }
            catch (Exception ex)
            {
                failure ??= ex;
                await abort.CancelAsync().ConfigureAwait(false);
                continue;
            }

            results[finished.Index] = log;
            logs.Add(log);
            if (log.Status == EvalStatus.Cancelled)
            {
                cancelled = true;
            }
            else if (log.Status == EvalStatus.Error && finished.RetriesRemaining > 0)
            {
                // re-queue under the same index with the failed log as the sample source (a fresh log entry,
                // completed samples reused); the previous attempt's log is superseded when the retry logs
                var retry = new PendingTask(finished.Index, finished.Task with { Id = log.Eval.TaskId, PreviousLog = log }, finished.RetriesRemaining - 1);
                pending.Add(retry);
                warn($"Retrying task '{finished.Task.Task.Name}' ({finished.Task.Model.Name}) — {retry.RetriesRemaining} retries remaining");
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        if (cancelled)
        {
            throw new OperationCanceledException("The eval set was cancelled.", cancellationToken);
        }

        return results.Select(log => log ?? throw new InvalidOperationException("A task produced no log.")).ToList();

        Task<EvalLog> RunOneAsync(PendingTask item)
        {
            var task = item.Task;
            var previous = task.PreviousLog;
            var runOptions = evalOptions with
            {
                Model = task.Model,
                TaskId = task.Id,
                EvalSetId = evalSetId,
                SampleSource = previous is null ? null : EvalSampleSource.FromLog(previous, task.Task.Dataset, warn),
                InitialModelUsage = previous?.Stats.ModelUsage is { Count: > 0 } usage ? usage : null,
            };
            return Eval.RunAsync(task.Task, runOptions, run, abort.Token);
        }
    }

    /// <summary>Port of <c>pick_balanced</c>: the earliest queued task among the least-used models (ties keep queue order, so the set's grouping survives a dispatch limit of 1).</summary>
    private static PendingTask PickBalanced(List<PendingTask> pending, Dictionary<Model, int> modelCounts)
    {
        var minCount = pending.Min(item => modelCounts[item.Task.Model]);
        var item = pending.First(candidate => modelCounts[candidate.Task.Model] == minCount);
        pending.Remove(item);
        return item;
    }

    /// <summary>Port of <c>starting_max_connections</c>: the explicit config value, else the smallest model default.</summary>
    internal static int StartingMaxConnections(IReadOnlyList<Model> models, GenerateConfig config) =>
        config.MaxConnections ?? models.Min(model => model.Api.MaxConnections());

    /// <summary>Port of tenacity's <c>wait_exponential(retry_wait, max=1h)</c> after pass <paramref name="attempt"/>: <c>retry_wait * 2^(attempt - 1)</c>, capped.</summary>
    internal static TimeSpan RetryWait(TimeSpan retryWait, int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var seconds = Math.Min(retryWait.TotalSeconds * Math.Pow(2, attempt - 1), MaxRetryWait.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private static bool IsBatch(object? batch) => batch switch
    {
        null => false,
        bool enabled => enabled,
        _ => true,
    };

    /// <summary>Port of <c>PendingTask</c>: a queued task with the retries it has left.</summary>
    private sealed record PendingTask(int Index, ResolvedTask Task, int RetriesRemaining);
}

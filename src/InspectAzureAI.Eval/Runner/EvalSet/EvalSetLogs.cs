using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Runner.EvalSet;

/// <summary>Port of the <c>Log</c> tuple of <c>_eval/evalset.py</c>: a log file, its header and the identifier of the task that wrote it.</summary>
public sealed record EvalSetLog(string Path, DateTimeOffset ModifiedAt, EvalLog Header, string TaskIdentifier);

/// <summary>Port of the <c>list_latest_eval_logs</c> result: the latest log per task split into complete and incomplete.</summary>
public sealed record LatestEvalLogs(IReadOnlyList<EvalSetLog> Complete, IReadOnlyList<EvalSetLog> Incomplete);

/// <summary>
/// Port of the log-directory functions of <c>_eval/evalset.py</c>: listing the logs of a directory with their task
/// identifiers, choosing the latest log per task (and deleting the older ones), and the predicates that decide
/// whether a log still needs work — its status, invalidation, a changed epoch count or reducer, or a sample
/// selection it does not cover.
/// </summary>
public static partial class EvalSetLogs
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}[:-]\d{2}[:-]\d{2}.*$")]
    private static partial Regex LogFilePattern();

    /// <summary>Port of <c>is_log_file</c> for the JSON format: a timestamp-prefixed <c>.json</c> file (the binary <c>.eval</c> format is not readable by this port).</summary>
    public static bool IsLogFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var name = System.IO.Path.GetFileName(path);
        return name.EndsWith(".json", StringComparison.Ordinal) && LogFilePattern().IsMatch(name);
    }

    /// <summary>Port of <c>list_all_eval_logs</c>: every log under <paramref name="logDir"/> (newest name first) with its header and task identifier; an unreadable log propagates its error, as in Python.</summary>
    public static IReadOnlyList<EvalSetLog> ListAllEvalLogs(string logDir, bool recursive = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(logDir);
        if (!Directory.Exists(logDir))
        {
            return [];
        }

        var files = Directory.EnumerateFiles(logDir, "*.json", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(IsLogFile)
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .ToList();
        var logs = new List<EvalSetLog>(files.Count);
        foreach (var file in files)
        {
            var header = EvalLogWriter.ReadHeader(file);
            logs.Add(new EvalSetLog(file, File.GetLastWriteTimeUtc(file), header, TaskIdentifier.Compute(header)));
        }

        return logs;
    }

    /// <summary>
    /// Port of <c>latest_completed_task_eval_logs</c>: the most recently written log of each <c>task_id</c>. With
    /// <paramref name="cleanupOlder"/> the others are deleted, except logs still <c>started</c> (kept for post-mortem
    /// debugging); a failed delete is reported through <paramref name="warn"/>.
    /// </summary>
    public static IReadOnlyList<EvalSetLog> LatestCompletedTaskEvalLogs(IReadOnlyList<EvalSetLog> logs, bool cleanupOlder = false, Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(logs);
        var byId = new Dictionary<string, List<EvalSetLog>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var log in logs)
        {
            var id = log.Header.Eval.TaskId;
            if (!byId.TryGetValue(id, out var group))
            {
                group = [];
                byId[id] = group;
                order.Add(id);
            }

            group.Add(log);
        }

        var latest = new List<EvalSetLog>(order.Count);
        foreach (var id in order)
        {
            // newest write first (Python sorts by mtime alone); an mtime tie — attempts written within one
            // filesystem timestamp tick — falls back to the log's own completion time, then the listing order
            var group = byId[id].OrderByDescending(log => log.ModifiedAt).ThenByDescending(log => log.Header.Stats.CompletedAt ?? log.Header.Eval.Created).ToList();
            latest.Add(group[0]);
            if (!cleanupOlder)
            {
                continue;
            }

            foreach (var older in group.Skip(1))
            {
                try
                {
                    if (older.Header.Status != EvalStatus.Started)
                    {
                        File.Delete(older.Path);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warn?.Invoke($"Error attempt to remove '{older.Path}': {ex.Message}");
                }
            }
        }

        return latest;
    }

    /// <summary>Port of <c>cleanup_older_eval_logs</c>: keeps only the latest log of each of <paramref name="taskIds"/>.</summary>
    public static void CleanupOlderEvalLogs(string logDir, IReadOnlySet<string> taskIds, Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        var logs = ListAllEvalLogs(logDir).Where(log => taskIds.Contains(log.Header.Eval.TaskId)).ToList();
        LatestCompletedTaskEvalLogs(logs, cleanupOlder: true, warn);
    }

    /// <summary>
    /// Port of <c>list_latest_eval_logs</c>: the latest log per task, complete when it succeeded, was not
    /// invalidated, has the requested epochs (and the task's reducers) and covers the selected samples; incomplete
    /// otherwise. Python's separate eval-level epochs check is folded into <see cref="LogSamplesComplete"/>, which
    /// knows the task: this runner applies the task's reducers whatever the eval-level epoch count is.
    /// </summary>
    public static LatestEvalLogs ListLatestEvalLogs(
        IReadOnlyList<ResolvedTask> tasks,
        IReadOnlyList<EvalSetLog> logs,
        int? epochs,
        int? limit,
        IReadOnlyList<object>? sampleIds,
        bool cleanupOlder,
        Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(logs);
        var complete = new List<EvalSetLog>();
        var incomplete = new List<EvalSetLog>();
        foreach (var log in LatestCompletedTaskEvalLogs(logs, cleanupOlder, warn))
        {
            if (log.Header.Status != EvalStatus.Success
                || log.Header.Invalidated
                || !LogSamplesComplete(log, tasks, epochs, limit, sampleIds))
            {
                incomplete.Add(log);
            }
            else
            {
                complete.Add(log);
            }
        }

        return new LatestEvalLogs(complete, incomplete);
    }

    /// <summary>
    /// Port of <c>log_samples_complete</c>: whether the log's planned sample runs cover every sample the task
    /// selects (its dataset under <paramref name="limit"/> / <paramref name="sampleIds"/>) for the effective epochs.
    /// </summary>
    public static bool LogSamplesComplete(EvalSetLog log, IReadOnlyList<ResolvedTask> tasks, int? epochs, int? limit, IReadOnlyList<object>? sampleIds)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(tasks);
        if (log.Header.Results is not { } results)
        {
            return false;
        }

        var task = tasks.FirstOrDefault(candidate => candidate.Identifier == log.TaskIdentifier)
            ?? throw new PrerequisiteError($"Could not find task for log '{log.Path}'.");
        var epochCount = epochs ?? task.Task.Epochs?.Count ?? 1;
        var reducers = EpochsReducerNames(task.Task.Epochs);
        if (EpochsChanged(epochCount, reducers, log.Header.Eval.Config))
        {
            return false;
        }

        var count = SamplesSelected(task.Task.Dataset, limit, sampleIds, task.Task.Name);
        return results.TotalSamples >= count * epochCount;
    }

    /// <summary>
    /// Port of <c>samples_selected</c>: how many dataset samples a <paramref name="limit"/> or
    /// <paramref name="sampleIds"/> selects (ids as glob patterns over normalised ids, <c>task:id</c> selectors
    /// scoped to <paramref name="task"/>, an unset id counting as its 1-based position). Like the runner, a
    /// non-positive limit selects everything.
    /// </summary>
    public static int SamplesSelected(IDataset dataset, int? limit, IReadOnlyList<object>? sampleIds, string task)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        if (sampleIds is not null)
        {
            var scoped = SampleIdFilter.ResolveForTask(task, sampleIds);
            if (scoped.Count == 0)
            {
                return 0;
            }

            var matchers = scoped.Select(SampleIdFilter.Normalise).Select(SampleIdFilter.GlobToRegex).ToList();
            var selected = 0;
            for (var position = 0; position < dataset.Count; position++)
            {
                var id = SampleIdFilter.Normalise(dataset[position].Id ?? position + 1);
                if (matchers.Any(matcher => matcher.IsMatch(id)))
                {
                    selected++;
                }
            }

            return selected;
        }

        return limit is > 0 and var first ? Math.Min(first, dataset.Count) : dataset.Count;
    }

    /// <summary>Port of <c>shuffle_changed</c>: a different shuffle only matters when a limit picks a subset of the samples.</summary>
    public static bool ShuffleChanged(object? sampleShuffle, EvalConfig config, int? limit)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (limit is null)
        {
            return false;
        }

        object? logged = config.SampleShuffleSeed is { } seed ? seed : config.SampleShuffle;
        return !Equals(sampleShuffle, logged);
    }

    /// <summary>
    /// Port of <c>epochs_changed</c>: nothing requested means unchanged; a request against a log without epochs, a
    /// different count, or different reducers (the unrecorded default and an explicit <c>["mean"]</c> being equal)
    /// means changed.
    /// </summary>
    public static bool EpochsChanged(int? epochs, IReadOnlyList<string>? reducers, EvalConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (epochs is null)
        {
            return false;
        }

        if (config.Epochs is null || epochs != config.Epochs)
        {
            return true;
        }

        var requested = Canonical(reducers);
        var logged = Canonical(config.EpochsReducer);
        return requested is null != (logged is null) || (requested is not null && !requested.SequenceEqual(logged!, StringComparer.Ordinal));

        static IReadOnlyList<string>? Canonical(IReadOnlyList<string>? names) =>
            names is null || names is ["mean"] ? null : names;
    }

    /// <summary>Port of <c>reducer_log_names</c> for a task's epochs: the reducer names, or null when there are none or one is unnamed (a custom delegate).</summary>
    public static IReadOnlyList<string>? EpochsReducerNames(Epochs? epochs) => EvalResultsBuilder.EpochsReducerNames(epochs?.Reducers);

    /// <summary>Port of <c>all_evals_succeeded</c>.</summary>
    public static bool AllEvalsSucceeded(IEnumerable<EvalLog> logs)
    {
        ArgumentNullException.ThrowIfNull(logs);
        return logs.All(log => log.Status == EvalStatus.Success && !log.Invalidated);
    }

    /// <summary>Port of <c>evals_cancelled</c>.</summary>
    public static bool EvalsCancelled(IEnumerable<EvalLog> logs)
    {
        ArgumentNullException.ThrowIfNull(logs);
        return logs.Any(log => log.Status == EvalStatus.Cancelled);
    }

    /// <summary>
    /// Port of <c>validate_eval_set_prerequisites</c>: every task must have a distinct identifier, and every log in
    /// the directory must belong to one of the tasks — unless <paramref name="logDirAllowDirty"/>, when foreign logs
    /// are dropped from the listing instead.
    /// </summary>
    public static IReadOnlyList<EvalSetLog> ValidateEvalSetPrerequisites(IReadOnlyList<ResolvedTask> tasks, IReadOnlyList<EvalSetLog> logs, bool logDirAllowDirty)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(logs);
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (!identifiers.Add(task.Identifier))
            {
                throw new PrerequisiteError(
                    $"The task '{task.Task.Name}' is not distinct.\n\nTasks in an eval set must have distinct names OR distinct combinations of name and task args (EvalTask.TaskArgs).");
            }
        }

        if (logDirAllowDirty)
        {
            return logs.Where(log => identifiers.Contains(log.TaskIdentifier)).ToList();
        }

        foreach (var log in logs)
        {
            if (!identifiers.Contains(log.TaskIdentifier))
            {
                throw new PrerequisiteError(
                    $"Existing log file '{System.IO.Path.GetFileName(log.Path)}' in log_dir is not associated with a task passed to the eval set "
                    + "(you must run the eval set in a fresh log directory). Set EvalSetOptions.LogDirAllowDirty to allow logs from other eval sets to be present in the log directory.");
            }
        }

        return logs;
    }
}

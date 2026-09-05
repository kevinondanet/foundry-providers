namespace InspectAzureAI.Eval.Log.EvalFormat;

/// <summary>
/// Port of <c>log/_recorders/recorder.py</c> <c>Recorder</c>: the lifecycle a running eval drives to persist its log
/// (<see cref="LogInitAsync"/>, <see cref="LogStartAsync"/>, one <see cref="LogSampleAsync"/> per completed sample,
/// <see cref="FlushAsync"/> at the buffer cadence, <see cref="LogFinishAsync"/> with the results). Disposing a
/// recorder releases the buffers of any eval it has not finished.
/// </summary>
public interface ILogRecorder : IAsyncDisposable
{
    /// <summary>The directory logs are written to (created on construction).</summary>
    string LogDir { get; }

    /// <summary>Port of <c>default_log_buffer</c>: how many completed samples to buffer before a flush.</summary>
    int DefaultLogBuffer(int sampleCount, bool highThroughput);

    /// <summary>Port of <c>is_writeable</c>.</summary>
    bool IsWriteable();

    /// <summary>
    /// Port of <c>log_init</c>: prepares the log for <paramref name="eval"/> at <paramref name="location"/> (or the
    /// recorder's own <c>{created}_{task}_{id}</c> name) and returns its path. An existing file at the location is
    /// re-used (its summaries and config updates seeded) unless <paramref name="clean"/> is set.
    /// </summary>
    Task<string> LogInitAsync(EvalSpec eval, string? location = null, bool clean = false, CancellationToken cancellationToken = default);

    /// <summary>Port of <c>log_start</c>.</summary>
    Task LogStartAsync(EvalSpec eval, EvalPlan plan, CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of <c>log_sample</c>: records a completed sample. <paramref name="writeThrough"/> persists it to the
    /// recorder's local tier immediately (keeping only an event-less copy in memory) rather than holding it until the
    /// next flush; recorders without such a tier ignore it.
    /// </summary>
    Task LogSampleAsync(EvalSpec eval, EvalSample sample, bool writeThrough = false, CancellationToken cancellationToken = default);

    /// <summary>Port of <c>sample_summaries</c>: every sample logged so far (ahead of disk), or null once the eval is finished.</summary>
    Task<IReadOnlyList<EvalSampleSummary>?> SampleSummariesAsync(EvalSpec eval, CancellationToken cancellationToken = default);

    /// <summary>Port of <c>buffered_sample</c>: a not-yet-flushed sample by id and epoch, or null.</summary>
    Task<EvalSample?> BufferedSampleAsync(EvalSpec eval, object id, int epoch, CancellationToken cancellationToken = default);

    /// <summary>Port of <c>log_config_update</c>: records a mid-run config change so it lands in the finished header.</summary>
    Task LogConfigUpdateAsync(EvalSpec eval, ConfigUpdate update, CancellationToken cancellationToken = default);

    /// <summary>Port of <c>flush</c>: writes the buffered samples to the log file.</summary>
    Task FlushAsync(EvalSpec eval, CancellationToken cancellationToken = default);

    /// <summary>Port of <c>log_finish</c>: writes the results and returns the finished log (its <see cref="EvalLog.Location"/> set).</summary>
    Task<EvalLog> LogFinishAsync(
        EvalSpec eval,
        EvalStatus status,
        EvalStats stats,
        EvalResults? results,
        IReadOnlyList<EvalSampleReductions>? reductions,
        EvalError? error = null,
        bool headerOnly = false,
        bool invalidated = false,
        IReadOnlyList<LogUpdate>? logUpdates = null,
        IReadOnlyList<ConfigUpdate>? configUpdates = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Port of <c>log/_recorders/create.py</c>: recorders by format or location.</summary>
public static class LogRecorders
{
    /// <summary>Port of <c>create_recorder_for_format</c>.</summary>
    public static ILogRecorder CreateForFormat(LogFormat format, string logDir) => format switch
    {
        LogFormat.Eval => new EvalRecorder(logDir),
        LogFormat.Json => new JsonRecorder(logDir),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, $"No recorder for format: {format}"),
    };

    /// <summary>Port of <c>create_recorder_for_location</c>.</summary>
    public static ILogRecorder CreateForLocation(string location, string logDir) => CreateForFormat(LogFormats.ForLocation(location), logDir);
}

/// <summary>Port of <c>FileRecorder._log_file_key</c> / <c>_log_file_path</c>: the <c>{created}_{task}_{id}</c> log file name (<c>INSPECT_EVAL_LOG_FILE_PATTERN</c> honoured).</summary>
public static class LogFileNaming
{
    /// <summary>Port of the <c>INSPECT_EVAL_LOG_FILE_PATTERN</c> default: <c>{task}_{id}</c> (<c>{model}</c> is also substituted).</summary>
    public const string DefaultPattern = "{task}_{id}";

    /// <summary>Port of <c>MODEL_NONE</c>: the model name that substitutes as an empty <c>{model}</c>.</summary>
    public const string ModelNone = "none/none";

    /// <summary>Port of <c>_log_file_key</c>: the file name without extension, from the created time, task and task id.</summary>
    public static string LogFileKey(EvalSpec eval)
    {
        ArgumentNullException.ThrowIfNull(eval);
        var pattern = Environment.GetEnvironmentVariable("INSPECT_EVAL_LOG_FILE_PATTERN");
        if (string.IsNullOrEmpty(pattern))
        {
            pattern = DefaultPattern;
        }

        var name = CleanFilenameComponent(CreatedText(eval.Created)) + "_" + pattern;
        name = name.Replace("{task}", CleanFilenameComponent(TaskDisplayName(eval.Task)), StringComparison.Ordinal);
        name = name.Replace("{id}", CleanFilenameComponent(eval.TaskId), StringComparison.Ordinal);
        var model = eval.Model == ModelNone ? "" : CleanFilenameComponent(eval.Model);
        return name.Replace("{model}", model, StringComparison.Ordinal);
    }

    /// <summary>Port of <c>_log_file_path</c>.</summary>
    public static string LogFilePath(string logDir, EvalSpec eval, LogFormat format)
    {
        ArgumentNullException.ThrowIfNull(logDir);
        return Path.Combine(logDir, LogFileKey(eval) + format.Extension());
    }

    /// <summary>Port of <c>_util/file.py</c> <c>clean_filename_component</c>: <c>_</c>, <c>/</c>, <c>:</c> and <c>+</c> become <c>-</c>.</summary>
    public static string CleanFilenameComponent(string component)
    {
        ArgumentNullException.ThrowIfNull(component);
        return component.Replace('_', '-').Replace('/', '-').Replace(':', '-').Replace('+', '-');
    }

    /// <summary>Port of <c>_util/task.py</c> <c>task_display_name</c>: the registry name without its package prefix (<c>hf/</c> names kept whole).</summary>
    public static string TaskDisplayName(string task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.StartsWith("hf/", StringComparison.Ordinal))
        {
            return task;
        }

        var slash = task.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? task : task[(slash + 1)..];
    }

    /// <summary>Port of <c>FileSystem.is_writeable</c> for a local directory: a probe file can be created in it.</summary>
    public static bool IsWriteable(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, "." + Path.GetRandomFileName());
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Python's <c>iso_now()</c> form of the created time (UTC, seconds precision), which the file name is derived from.</summary>
    private static string CreatedText(DateTimeOffset created) =>
        created.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", System.Globalization.CultureInfo.InvariantCulture);
}

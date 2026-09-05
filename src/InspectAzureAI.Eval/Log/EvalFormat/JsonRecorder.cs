using System.Collections.Concurrent;
using System.Text;

namespace InspectAzureAI.Eval.Log.EvalFormat;

/// <summary>
/// Port of <c>log/_recorders/json.py</c> <c>JSONRecorder</c>: the plain <c>.json</c> format. The whole log stays
/// in memory for the run and the file is rewritten (atomically) at every <see cref="FlushAsync"/> and at
/// <see cref="LogFinishAsync"/>; the JSON itself is <see cref="EvalLogWriter"/>'s.
/// </summary>
public sealed class JsonRecorder : ILogRecorder
{
    private static readonly object CacheSync = new();

    private static (string Location, DateTime Written, EvalLog Log)? _lastRead;

    private readonly ConcurrentDictionary<string, JsonLogFile> _data = new(StringComparer.Ordinal);

    public JsonRecorder(string logDir)
    {
        ArgumentNullException.ThrowIfNull(logDir);
        LogDir = logDir.TrimEnd('/', '\\');
        if (LogDir.Length == 0)
        {
            LogDir = ".";
        }

        Directory.CreateDirectory(LogDir);
    }

    public string LogDir { get; }

    /// <summary>Port of <c>handles_location</c>: <c>.json</c> paths.</summary>
    public static bool HandlesLocation(string location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return location.EndsWith(".json", StringComparison.Ordinal);
    }

    /// <summary>Port of <c>handles_bytes</c>: a leading <c>{</c>.</summary>
    public static bool HandlesBytes(ReadOnlySpan<byte> firstBytes) => firstBytes.Length >= 1 && firstBytes[0] == (byte)'{';

    /// <summary>Port of <c>default_log_buffer</c>: ~10 flushes over a high-throughput run, otherwise 10 samples (the whole file is rewritten each time).</summary>
    public int DefaultLogBuffer(int sampleCount, bool highThroughput) => highThroughput ? Math.Max(10, sampleCount / 10) : 10;

    public bool IsWriteable() => LogFileNaming.IsWriteable(LogDir);

    public string LogFileKey(EvalSpec eval) => LogFileNaming.LogFileKey(eval);

    public string LogFilePath(EvalSpec eval) => LogFileNaming.LogFilePath(LogDir, eval, LogFormat.Json);

    public Task<string> LogInitAsync(EvalSpec eval, string? location = null, bool clean = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eval);
        cancellationToken.ThrowIfCancellationRequested();
        // an absolute path, so the writes land where intended even if the working directory changes mid-task
        var file = location ?? Path.GetFullPath(LogFilePath(eval));
        _data[LogFileKey(eval)] = new JsonLogFile(file, eval);
        return Task.FromResult(file);
    }

    public Task LogStartAsync(EvalSpec eval, EvalPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        Log(eval).Plan = plan;
        return Task.CompletedTask;
    }

    /// <summary><paramref name="writeThrough"/> is ignored: the format holds the whole log in memory by design.</summary>
    public Task LogSampleAsync(EvalSpec eval, EvalSample sample, bool writeThrough = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        cancellationToken.ThrowIfCancellationRequested();
        var log = Log(eval);
        var summary = sample.Summary();
        lock (log.Sync)
        {
            log.Samples.Add(sample);
            log.Summaries.Add(summary);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EvalSampleSummary>?> SampleSummariesAsync(EvalSpec eval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eval);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_data.TryGetValue(LogFileKey(eval), out var log))
        {
            return Task.FromResult<IReadOnlyList<EvalSampleSummary>?>(null);
        }

        lock (log.Sync)
        {
            return Task.FromResult<IReadOnlyList<EvalSampleSummary>?>(log.Summaries.ToList());
        }
    }

    public Task<EvalSample?> BufferedSampleAsync(EvalSpec eval, object id, int epoch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eval);
        ArgumentNullException.ThrowIfNull(id);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_data.TryGetValue(LogFileKey(eval), out var log))
        {
            return Task.FromResult<EvalSample?>(null);
        }

        var key = EvalLogFormat.SampleKey(id, epoch);
        lock (log.Sync)
        {
            return Task.FromResult(log.Samples.FirstOrDefault(sample => EvalLogFormat.SampleKey(sample.Id, sample.Epoch) == key));
        }
    }

    public Task LogConfigUpdateAsync(EvalSpec eval, ConfigUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();
        var log = Log(eval);
        lock (log.Sync)
        {
            (log.ConfigUpdates ??= []).Add(update);
        }

        return Task.CompletedTask;
    }

    public Task FlushAsync(EvalSpec eval, CancellationToken cancellationToken = default)
    {
        var log = Log(eval);
        return WriteLogImplAsync(log.File, log.Compose(EvalStatus.Started), fsync: false, cancellationToken);
    }

    public async Task<EvalLog> LogFinishAsync(
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stats);
        var key = LogFileKey(eval);
        var log = Log(eval);
        var data = log.Compose(status) with
        {
            Stats = stats,
            Results = results,
            Invalidated = invalidated,
            LogUpdates = logUpdates,
            // null means "not supplied": keep the updates accumulated mid-run
            ConfigUpdates = configUpdates ?? log.ConfigUpdates,
            Error = error,
            Reductions = reductions is { Count: > 0 } ? reductions : null,
        };
        data = EvalLogEditing.RecomputeTagsAndMetadata(data);
        await WriteLogImplAsync(log.File, data, fsync: true, cancellationToken).ConfigureAwait(false);
        _data.TryRemove(key, out _);
        return data with { Location = log.File };
    }

    /// <summary>Port of <c>read_log</c> for the JSON format (see <see cref="EvalLogWriter.Deserialize"/>).</summary>
    public static EvalLog ReadLog(string location, bool headerOnly = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(location);
        var log = EvalLogWriter.Deserialize(File.ReadAllText(location, Encoding.UTF8)) with { Location = location };
        return headerOnly ? log with { Samples = null, Reductions = null } : log;
    }

    public static async Task<EvalLog> ReadLogAsync(string location, bool headerOnly = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(location);
        var log = EvalLogWriter.Deserialize(await File.ReadAllTextAsync(location, Encoding.UTF8, cancellationToken).ConfigureAwait(false)) with { Location = location };
        return headerOnly ? log with { Samples = null, Reductions = null } : log;
    }

    /// <summary>Port of <c>read_log_bytes</c>.</summary>
    public static EvalLog ReadLogBytes(Stream logBytes, bool headerOnly = false)
    {
        ArgumentNullException.ThrowIfNull(logBytes);
        using var text = new StreamReader(logBytes, Encoding.UTF8, leaveOpen: true);
        var log = EvalLogWriter.Deserialize(text.ReadToEnd());
        return headerOnly ? log with { Samples = null, Reductions = null } : log;
    }

    /// <summary>
    /// Port of <c>FileRecorder.read_log_sample</c>: the whole log is read (the last one is cached) and the sample
    /// found by exact id and epoch, then by textual id (so <c>"1"</c> finds the int <c>1</c>) or uuid; missing is a
    /// <see cref="KeyNotFoundException"/>.
    /// </summary>
    public static EvalSample ReadLogSample(string location, object? id = null, int epoch = 1, string? uuid = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(location);
        if (id is null && uuid is null)
        {
            throw new ArgumentException("You must specify an 'id' or 'uuid' to read", nameof(id));
        }

        var log = LogFileMaybeCached(location);
        if (log.Samples is not { Count: > 0 } samples)
        {
            throw new KeyNotFoundException($"No samples found in log {location}");
        }

        EvalSample? found = null;
        if (id is not null)
        {
            var key = EvalLogFormat.SampleKey(id, epoch);
            found = samples.FirstOrDefault(sample => EvalLogFormat.SampleKey(sample.Id, sample.Epoch) == key);
        }

        if (found is null)
        {
            var idText = id is null ? null : EvalLogFormat.IdText(id);
            found = samples.FirstOrDefault(sample =>
                (idText is not null && EvalLogFormat.IdText(sample.Id) == idText && sample.Epoch == epoch)
                || (uuid is not null && sample.Uuid == uuid));
        }

        return found ?? throw new KeyNotFoundException(id is null
            ? $"Sample with uuid '{uuid}' not found in log {location}"
            : $"Sample id {EvalLogFormat.IdText(id)} for epoch {epoch} not found in log {location}");
    }

    /// <summary>Port of <c>FileRecorder.read_log_sample_summaries</c>.</summary>
    public static IReadOnlyList<EvalSampleSummary> ReadLogSampleSummaries(string location)
    {
        var log = LogFileMaybeCached(location);
        return log.Samples is { Count: > 0 } samples ? samples.Select(sample => sample.Summary()).ToList() : [];
    }

    /// <summary>
    /// Port of <c>write_log</c>: writes the log (samples sorted) atomically. With <paramref name="headerOnly"/> the
    /// samples and reductions on disk are grafted onto the in-memory header (or dropped when the file does not
    /// exist yet), so sample-level state of the in-memory log never reaches disk.
    /// </summary>
    public static Task WriteLogAsync(string location, EvalLog log, bool headerOnly = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(location);
        ArgumentNullException.ThrowIfNull(log);
        if (headerOnly)
        {
            log = File.Exists(location)
                ? log with { Samples = ReadLog(location).Samples, Reductions = ReadLog(location).Reductions }
                : log with { Samples = null, Reductions = null };
        }

        return WriteLogImplAsync(location, log, fsync: true, cancellationToken);
    }

    /// <summary>Synchronous <see cref="WriteLogAsync"/> (the write runs on the thread pool, so a synchronization context cannot deadlock it).</summary>
    public static void WriteLog(string location, EvalLog log, bool headerOnly = false) => Task.Run(() => WriteLogAsync(location, log, headerOnly)).GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        _data.Clear();
        return ValueTask.CompletedTask;
    }

    private static async Task WriteLogImplAsync(string location, EvalLog log, bool fsync, CancellationToken cancellationToken)
    {
        if (log.Samples is { Count: > 1 } samples)
        {
            log = log with { Samples = EvalLogWriter.SortSamples(samples) };
        }

        var bytes = Encoding.UTF8.GetBytes(EvalLogWriter.Serialize(log));
        var destination = Path.GetFullPath(location);
        var directory = Path.GetDirectoryName(destination) ?? throw new IOException($"The log path '{location}' has no directory.");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
            await using (output.ConfigureAwait(false))
            {
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (fsync)
                {
                    output.Flush(flushToDisk: true);
                }
            }

            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>Port of <c>_log_file_maybe_cached</c>: the last log read for sample access is kept while the file is unchanged.</summary>
    private static EvalLog LogFileMaybeCached(string location)
    {
        var written = File.GetLastWriteTimeUtc(location);
        lock (CacheSync)
        {
            if (_lastRead is { } cached && cached.Location == location && cached.Written == written)
            {
                return cached.Log;
            }
        }

        var log = ReadLog(location);
        lock (CacheSync)
        {
            _lastRead = (location, written, log);
        }

        return log;
    }

    private JsonLogFile Log(EvalSpec eval)
    {
        ArgumentNullException.ThrowIfNull(eval);
        return _data.TryGetValue(LogFileKey(eval), out var log)
            ? log
            : throw new KeyNotFoundException($"No log has been initialised for eval '{LogFileKey(eval)}' (call LogInitAsync first).");
    }

    /// <summary>Port of <c>JSONLogFile</c>: the in-memory log of one eval. <see cref="Sync"/> guards the lists (samples complete on many threads).</summary>
    private sealed class JsonLogFile(string file, EvalSpec eval)
    {
        public string File { get; } = file;

        public EvalSpec Eval { get; } = eval;

        public EvalPlan Plan { get; set; } = new();

        public List<EvalSample> Samples { get; } = [];

        public List<EvalSampleSummary> Summaries { get; } = [];

        public List<ConfigUpdate>? ConfigUpdates { get; set; }

        public object Sync { get; } = new();

        public EvalLog Compose(EvalStatus status)
        {
            lock (Sync)
            {
                return new EvalLog
                {
                    Status = status,
                    Eval = Eval,
                    Plan = Plan,
                    Samples = Samples.Count > 0 ? Samples.ToList() : null,
                    ConfigUpdates = ConfigUpdates?.ToList(),
                };
            }
        }
    }
}

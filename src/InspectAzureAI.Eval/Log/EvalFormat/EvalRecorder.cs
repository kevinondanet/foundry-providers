using System.Collections.Concurrent;
using System.Globalization;

namespace InspectAzureAI.Eval.Log.EvalFormat;

/// <summary>A sample's identity within a log: its id (int or string) and epoch.</summary>
public readonly record struct SampleIdentity(object Id, int Epoch);

/// <summary>
/// Port of <c>log/_recorders/eval.py</c> <c>EvalRecorder</c>: the native <c>.eval</c> format. The log is a zip
/// written incrementally — <c>_journal/start.json</c> at <see cref="LogStartAsync"/>, one <c>samples/{id}_epoch_{n}.json</c>
/// member per sample plus a numbered summary batch under <c>_journal/summaries/</c> at every
/// <see cref="FlushAsync"/>, and <c>summaries.json</c>, <c>reductions.json</c> and <c>header.json</c> at
/// <see cref="LogFinishAsync"/>. Every flush copies the whole temp archive over the destination atomically, so
/// the file on disk is always a readable log. Reads (<see cref="ReadLog"/>, <see cref="ReadLogSample"/>, ...)
/// index the central directory and decode members written by Python (zstandard) or by this port (deflate).
/// </summary>
public sealed class EvalRecorder : ILogRecorder
{
    private readonly ConcurrentDictionary<string, ZipLogFile> _data = new(StringComparer.Ordinal);

    private bool _disposed;

    /// <summary>Creates the recorder for <paramref name="logDir"/> (created when missing, as <c>FileRecorder</c> does).</summary>
    public EvalRecorder(string logDir)
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

    /// <summary>Port of <c>handles_location</c>: <c>.eval</c> paths.</summary>
    public static bool HandlesLocation(string location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return location.EndsWith(".eval", StringComparison.Ordinal);
    }

    /// <summary>Port of <c>handles_bytes</c>: a ZIP local file header.</summary>
    public static bool HandlesBytes(ReadOnlySpan<byte> firstBytes) => ZipLogReader.IsZip(firstBytes);

    /// <summary>Port of <c>default_log_buffer</c>: ~20 flushes over a high-throughput run, otherwise between 1 and 10 samples scaled with the sample count.</summary>
    public int DefaultLogBuffer(int sampleCount, bool highThroughput) =>
        highThroughput ? Math.Max(10, sampleCount / 20) : Math.Max(1, Math.Min(sampleCount / 3, 10));

    public bool IsWriteable() => LogFileNaming.IsWriteable(LogDir);

    /// <summary>Port of <c>_log_file_key</c>.</summary>
    public string LogFileKey(EvalSpec eval) => LogFileNaming.LogFileKey(eval);

    /// <summary>Port of <c>_log_file_path</c>: <c>{log_dir}/{created}_{task}_{id}.eval</c>.</summary>
    public string LogFilePath(EvalSpec eval) => LogFileNaming.LogFilePath(LogDir, eval, LogFormat.Eval);

    public async Task<string> LogInitAsync(EvalSpec eval, string? location = null, bool clean = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eval);
        ObjectDisposedException.ThrowIf(_disposed, this);
        LogStart? start = null;
        IReadOnlyList<EvalSampleSummary> summaries = [];
        var summaryCounter = 0;
        IReadOnlyList<ConfigUpdate> configUpdates = [];
        var configUpdateCounter = 0;
        var destinationExists = false;
        if (!clean && location is not null && File.Exists(location))
        {
            destinationExists = true;
            (start, summaries, summaryCounter, configUpdates, configUpdateCounter) = await Task.Run(
                () =>
                {
                    using var reader = ZipLogReader.Open(location);
                    var names = reader.Names.ToHashSet(StringComparer.Ordinal);
                    var logStart = EvalLogReading.ReadStart(reader, names);
                    var (existing, counter) = EvalLogReading.ReadAllSummaries(reader);
                    var (updates, updateCounter) = EvalLogReading.ReadConfigUpdates(reader);
                    return (logStart, (IReadOnlyList<EvalSampleSummary>)existing, counter, (IReadOnlyList<ConfigUpdate>)updates, updateCounter);
                },
                cancellationToken).ConfigureAwait(false);
        }

        var file = location ?? LogFilePath(eval);
        var zip = new ZipLogFile(file);
        try
        {
            await zip.InitAsync(start, summaryCounter, summaries, configUpdateCounter, configUpdates, destinationExists, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await zip.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var key = LogFileKey(eval);
        if (_data.TryRemove(key, out var previous))
        {
            await previous.DisposeAsync().ConfigureAwait(false);
        }

        _data[key] = zip;
        return file;
    }

    public Task LogStartAsync(EvalSpec eval, EvalPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Log(eval).StartAsync(new LogStart(EvalLog.SchemaVersion, eval, plan), cancellationToken);
    }

    public Task LogSampleAsync(EvalSpec eval, EvalSample sample, bool writeThrough = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var log = Log(eval);
        return writeThrough ? log.BufferSampleWriteThroughAsync(sample, cancellationToken) : log.BufferSampleAsync(sample, cancellationToken);
    }

    public async Task<IReadOnlyList<EvalSampleSummary>?> SampleSummariesAsync(EvalSpec eval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eval);
        return _data.TryGetValue(LogFileKey(eval), out var log) ? await log.SampleSummariesAsync(cancellationToken).ConfigureAwait(false) : null;
    }

    public async Task<EvalSample?> BufferedSampleAsync(EvalSpec eval, object id, int epoch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eval);
        return _data.TryGetValue(LogFileKey(eval), out var log) ? await log.BufferedSampleAsync(id, epoch, cancellationToken).ConfigureAwait(false) : null;
    }

    /// <summary>Journals the update and, once the destination exists, flushes it out immediately so it survives a crash.</summary>
    public async Task LogConfigUpdateAsync(EvalSpec eval, ConfigUpdate update, CancellationToken cancellationToken = default)
    {
        var log = Log(eval);
        await log.RecordConfigUpdateAsync(update, cancellationToken).ConfigureAwait(false);
        if (log.DestinationWritten)
        {
            await log.FlushAsync(fsync: false, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task FlushAsync(EvalSpec eval, CancellationToken cancellationToken = default)
    {
        var log = Log(eval);
        await log.WriteBufferedSamplesAsync(cancellationToken).ConfigureAwait(false);
        await log.FlushAsync(fsync: false, cancellationToken).ConfigureAwait(false);
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
        await log.WriteBufferedSamplesAsync(cancellationToken).ConfigureAwait(false);
        await log.WriteAsync(EvalLogFormat.SummariesJson, log.Summaries, cancellationToken).ConfigureAwait(false);
        if (reductions is not null)
        {
            await log.WriteAsync(EvalLogFormat.ReductionsJson, reductions, cancellationToken).ConfigureAwait(false);
        }

        var logResults = new LogResults(status, stats, results, error);
        var start = log.LogStart ?? throw new InvalidOperationException("Log not properly initialised");
        // a caller-supplied list (a full-log rewrite) is authoritative; otherwise the journaled mid-run updates
        var allConfigUpdates = configUpdates ?? log.ConfigUpdates;
        var header = new EvalLog
        {
            Version = start.Version,
            Invalidated = invalidated,
            LogUpdates = logUpdates,
            ConfigUpdates = allConfigUpdates.Count > 0 ? allConfigUpdates : null,
            Eval = start.Eval,
            Plan = start.Plan,
            Results = logResults.Results,
            Stats = logResults.Stats,
            Status = logResults.Status,
            Error = logResults.Error,
        };
        await log.WriteAsync(EvalLogFormat.HeaderJson, EvalLogEditing.RecomputeTagsAndMetadata(header), cancellationToken).ConfigureAwait(false);
        await log.FlushAsync(fsync: true, cancellationToken).ConfigureAwait(false);
        var result = await log.CloseAsync(headerOnly, cancellationToken).ConfigureAwait(false);
        _data.TryRemove(key, out _);
        await log.DisposeAsync().ConfigureAwait(false);
        return result;
    }

    /// <summary>Port of <c>read_log</c>: the whole log, or its header (spec, plan, results, stats and reductions) only.</summary>
    public static EvalLog ReadLog(string location, bool headerOnly = false, ISet<string>? excludeFields = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(location);
        using var reader = ZipLogReader.Open(location);
        return EvalLogReading.ReadLog(reader, location, headerOnly, EvalJson.NormalizeExcludedFields(excludeFields));
    }

    public static Task<EvalLog> ReadLogAsync(string location, bool headerOnly = false, ISet<string>? excludeFields = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadLog(location, headerOnly, excludeFields), cancellationToken);

    /// <summary>Port of <c>read_log_bytes</c>: a log from a stream (no <see cref="EvalLog.Location"/>).</summary>
    public static EvalLog ReadLogBytes(Stream logBytes, bool headerOnly = false)
    {
        ArgumentNullException.ThrowIfNull(logBytes);
        using var reader = ZipLogReader.FromStream(logBytes);
        return EvalLogReading.ReadLog(reader, null, headerOnly);
    }

    /// <summary>Port of <c>read_log_sample</c>: one sample by <paramref name="id"/> and <paramref name="epoch"/>, or by <paramref name="uuid"/>; a missing sample is a <see cref="KeyNotFoundException"/>.</summary>
    public static EvalSample ReadLogSample(string location, object? id = null, int epoch = 1, string? uuid = null, ISet<string>? excludeFields = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(location);
        using var reader = ZipLogReader.Open(location);
        return EvalLogReading.ReadSample(reader, location, id, epoch, uuid, EvalJson.NormalizeExcludedFields(excludeFields));
    }

    public static Task<EvalSample> ReadLogSampleAsync(string location, object? id = null, int epoch = 1, string? uuid = null, ISet<string>? excludeFields = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadLogSample(location, id, epoch, uuid, excludeFields), cancellationToken);

    /// <summary>The member names of the zip at <paramref name="location"/>, in central-directory order (repeats included, as Python's <c>namelist()</c>).</summary>
    public static IReadOnlyList<string> ReadMemberNames(string location)
    {
        ArgumentException.ThrowIfNullOrEmpty(location);
        using var reader = ZipLogReader.Open(location);
        return reader.Entries.Select(entry => entry.Name).ToList();
    }

    /// <summary>Port of <c>AsyncZipReader.read_member_fully</c>: the decoded bytes of one member (the last record when the name repeats); a missing member is a <see cref="FileNotFoundException"/>.</summary>
    public static byte[] ReadMember(string location, string member)
    {
        ArgumentException.ThrowIfNullOrEmpty(location);
        ArgumentException.ThrowIfNullOrEmpty(member);
        using var reader = ZipLogReader.Open(location);
        return reader.Read(member);
    }

    /// <summary>Port of <c>read_log_sample_summaries</c>.</summary>
    public static IReadOnlyList<EvalSampleSummary> ReadLogSampleSummaries(string location)
    {
        ArgumentException.ThrowIfNullOrEmpty(location);
        using var reader = ZipLogReader.Open(location);
        return EvalLogReading.ReadAllSummaries(reader).Summaries;
    }

    /// <summary>Port of <c>read_log_sample_ids</c>: every (id, epoch), sorted by epoch then id (ints zero-padded to 20 digits).</summary>
    public static IReadOnlyList<SampleIdentity> ReadLogSampleIds(string location) =>
        ReadLogSampleSummaries(location)
            .Select(summary => new SampleIdentity(summary.Id, summary.Epoch))
            .OrderBy(sample => sample.Epoch)
            .ThenBy(sample => sample.Id is string text ? text : EvalLogFormat.IdText(sample.Id).PadLeft(20, '0'), StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Port of <c>write_log</c>: rewrites <paramref name="location"/> from <paramref name="log"/> (samples condensed),
    /// or with <paramref name="headerOnly"/> replaces just <c>header.json</c> inside the existing zip — samples on
    /// disk are untouched and sample changes on the in-memory log are discarded.
    /// </summary>
    public static async Task WriteLogAsync(string location, EvalLog log, bool headerOnly = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(location);
        ArgumentNullException.ThrowIfNull(log);
        if (headerOnly)
        {
            await Task.Run(() => ReplaceEvalHeaderInPlace(location, log), cancellationToken).ConfigureAwait(false);
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(location)) ?? ".";
        var recorder = new EvalRecorder(directory);
        await using (recorder.ConfigureAwait(false))
        {
            await recorder.LogInitAsync(log.Eval, location, clean: true, cancellationToken).ConfigureAwait(false);
            await recorder.LogStartAsync(log.Eval, log.Plan, cancellationToken).ConfigureAwait(false);
            foreach (var sample in log.Samples ?? [])
            {
                await recorder.LogSampleAsync(log.Eval, LogAttachments.CondenseSample(sample), cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            await recorder.LogFinishAsync(log.Eval, log.Status, log.Stats, log.Results, log.Reductions, log.Error, invalidated: log.Invalidated, logUpdates: log.LogUpdates, configUpdates: log.ConfigUpdates, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Synchronous <see cref="WriteLogAsync"/> (the write runs on the thread pool, so a synchronization context cannot deadlock it).</summary>
    public static void WriteLog(string location, EvalLog log, bool headerOnly = false) => Task.Run(() => WriteLogAsync(location, log, headerOnly)).GetAwaiter().GetResult();

    /// <summary>Port of <c>_eval_log_header</c>: the log without samples and reductions.</summary>
    public static EvalLog EvalLogHeader(EvalLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return EvalLogEditing.RecomputeTagsAndMetadata(log with { Samples = null, Reductions = null });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var key in _data.Keys.ToList())
        {
            if (_data.TryRemove(key, out var log))
            {
                await log.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Port of <c>_replace_eval_header_in_place</c>: reopens the zip in append mode, drops the old <c>header.json</c>
    /// record and appends the new one. Not atomic (an interruption can leave the central directory inconsistent),
    /// the same trade-off Python makes for infrequent header edits; the old header bytes stay as a small leak.
    /// </summary>
    private static void ReplaceEvalHeaderInPlace(string zipPath, EvalLog log)
    {
        var header = EvalJson.Serialize(EvalLogHeader(log));
        using var stream = new FileStream(zipPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var writer = ZipLogWriter.OpenAppend(stream);
        writer.RemoveMember(EvalLogFormat.HeaderJson);
        writer.AddMember(EvalLogFormat.HeaderJson, header);
        writer.WriteCentralDirectory();
    }

    private ZipLogFile Log(EvalSpec eval)
    {
        ArgumentNullException.ThrowIfNull(eval);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _data.TryGetValue(LogFileKey(eval), out var log)
            ? log
            : throw new KeyNotFoundException($"No log has been initialised for eval '{LogFileKey(eval)}' (call LogInitAsync first).");
    }
}

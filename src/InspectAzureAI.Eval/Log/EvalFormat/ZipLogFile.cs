using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Log.EvalFormat;

/// <summary>
/// Port of <c>ZipLogFile</c>: the temp-file zip an eval appends to while it runs. Samples are buffered with
/// their summaries until <see cref="WriteBufferedSamplesAsync"/> writes them (one member per sample plus a
/// numbered summary batch under <c>_journal/summaries/</c>); <see cref="FlushAsync"/> completes the archive and
/// copies it atomically over the destination; <see cref="CloseAsync"/> reads the header back and hands out the
/// samples lazily. A lock serialises the callers (samples complete on many threads).
/// </summary>
internal sealed class ZipLogFile : IAsyncDisposable
{
    private readonly string _file;

    private readonly FileStream _temp;

    private readonly ZipLogWriter _zip;

    private readonly SemaphoreSlim _lock = new(1, 1);

    private readonly List<BufferedSample> _samples = [];

    private readonly Dictionary<string, EvalSample> _streamingSamples = new(StringComparer.Ordinal);

    private int _summaryCounter;

    private List<EvalSampleSummary> _summaries = [];

    private int _configUpdateCounter;

    private List<ConfigUpdate> _configUpdates = [];

    private bool _destinationWritten;

    private bool _disposed;

    public ZipLogFile(string file)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        _file = file;
        var tempPath = Path.Combine(Path.GetTempPath(), "inspect-" + Path.GetRandomFileName() + ".eval");
        _temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
        _zip = new ZipLogWriter(_temp);
    }

    /// <summary>The destination log file.</summary>
    public string File => _file;

    public LogStart? LogStart { get; private set; }

    /// <summary>
    /// Whether the destination has been written at least once: after a successful flush, or from the start when
    /// seeded from an existing file. Gates the eager per-update flush of <c>log_config_update</c>.
    /// </summary>
    public bool DestinationWritten => _destinationWritten;

    public IReadOnlyList<ConfigUpdate> ConfigUpdates => _configUpdates;

    /// <summary>The consolidated summaries (journaled batches merged, one per (id, epoch)).</summary>
    public IReadOnlyList<EvalSampleSummary> Summaries => _summaries;

    public IReadOnlyList<string> MemberNames => _zip.Names;

    public async Task InitAsync(LogStart? logStart, int summaryCounter, IReadOnlyList<EvalSampleSummary> summaries, int configUpdateCounter, IReadOnlyList<ConfigUpdate>? configUpdates, bool destinationExists, CancellationToken cancellationToken)
    {
        using (await LockAsync(cancellationToken).ConfigureAwait(false))
        {
            _summaryCounter = summaryCounter;
            _summaries = [.. summaries];
            _configUpdateCounter = configUpdateCounter;
            _configUpdates = configUpdates is null ? [] : [.. configUpdates];
            LogStart = logStart;
            _destinationWritten = destinationExists;
        }
    }

    /// <summary>Port of <c>record_config_update</c>: journals one update as <c>_journal/config_updates/{n}.json</c>.</summary>
    public async Task RecordConfigUpdateAsync(ConfigUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        using (await LockAsync(cancellationToken).ConfigureAwait(false))
        {
            _configUpdateCounter++;
            ZipWriteStr(EvalLogFormat.JournalConfigUpdatePath(EvalLogFormat.JournalConfigUpdateFile(_configUpdateCounter)), update);
            _configUpdates.Add(update);
        }
    }

    public async Task StartAsync(LogStart start, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        using (await LockAsync(cancellationToken).ConfigureAwait(false))
        {
            LogStart = start;
            ZipWriteStr(EvalLogFormat.JournalPath(EvalLogFormat.StartJson), start);
        }
    }

    /// <summary>Port of <c>buffer_sample</c>: holds the sample (summary computed now) until the next flush, superseding any unflushed record for the same (id, epoch).</summary>
    public async Task BufferSampleAsync(EvalSample sample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var buffered = new BufferedSample(sample, sample.Summary());
        using (await LockAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = EvalLogFormat.SampleKey(sample.Id, sample.Epoch);
            _samples.RemoveAll(s => EvalLogFormat.SampleKey(s.Sample.Id, s.Sample.Epoch) == key);
            _streamingSamples.Remove(key);
            _samples.Add(buffered);
        }
    }

    /// <summary>
    /// Port of <c>buffer_sample_write_through</c>: writes the sample straight into the temp zip (journaling its
    /// summary) and keeps only an event-less copy in memory until the next flush makes it readable from disk.
    /// </summary>
    public async Task BufferSampleWriteThroughAsync(EvalSample sample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);
        using (await LockAsync(cancellationToken).ConfigureAwait(false))
        {
            ZipWriteStr(EvalLogFormat.SampleFilename(sample.Id, sample.Epoch), sample);
            _streamingSamples[EvalLogFormat.SampleKey(sample.Id, sample.Epoch)] = sample with { Events = [], EventsData = null };
            JournalSummary(sample);
        }
    }

    /// <summary>Port of <c>write_buffered_samples</c>: one member per buffered sample, then their summaries as the next journal batch.</summary>
    public async Task WriteBufferedSamplesAsync(CancellationToken cancellationToken)
    {
        using (await LockAsync(cancellationToken).ConfigureAwait(false))
        {
            var summaries = new List<EvalSampleSummary>();
            foreach (var buffered in _samples)
            {
                ZipWriteStr(EvalLogFormat.SampleFilename(buffered.Sample.Id, buffered.Sample.Epoch), buffered.Sample);
                summaries.Add(buffered.Summary);
                // serialising a large sample is a long synchronous stretch; yield between samples as Python checkpoints
                await Task.Yield();
            }

            _samples.Clear();
            if (summaries.Count > 0)
            {
                _summaryCounter++;
                ZipWriteStr(EvalLogFormat.JournalSummaryPath(EvalLogFormat.JournalSummaryFile(_summaryCounter)), summaries);
                var keys = summaries.Select(s => EvalLogFormat.SampleKey(s.Id, s.Epoch)).ToHashSet(StringComparer.Ordinal);
                _summaries.RemoveAll(s => keys.Contains(EvalLogFormat.SampleKey(s.Id, s.Epoch)));
                _summaries.AddRange(summaries);
            }
        }
    }

    /// <summary>Port of <c>sample_summaries</c>: the journaled summaries with the unflushed buffer layered on top (one row per (id, epoch)).</summary>
    public async Task<IReadOnlyList<EvalSampleSummary>> SampleSummariesAsync(CancellationToken cancellationToken)
    {
        using (await LockAsync(cancellationToken).ConfigureAwait(false))
        {
            return EvalLogReading.DedupeSummaries(_summaries.Concat(_samples.Select(s => s.Summary)));
        }
    }

    /// <summary>Port of <c>buffered_sample</c>: an unflushed full sample, else the event-less copy of a write-through sample, else null.</summary>
    public async Task<EvalSample?> BufferedSampleAsync(object id, int epoch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        using (await LockAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = EvalLogFormat.SampleKey(id, epoch);
            foreach (var buffered in _samples)
            {
                if (EvalLogFormat.SampleKey(buffered.Sample.Id, buffered.Sample.Epoch) == key)
                {
                    return buffered.Sample;
                }
            }

            return _streamingSamples.GetValueOrDefault(key);
        }
    }

    public async Task WriteAsync<T>(string filename, T data, CancellationToken cancellationToken)
    {
        using (await LockAsync(cancellationToken).ConfigureAwait(false))
        {
            ZipWriteStr(filename, data);
        }
    }

    /// <summary>
    /// Port of <c>flush</c>: completes the temp archive and copies it over the destination (a temp file in the
    /// same directory, then an atomic rename). <paramref name="fsync"/> makes the final write crash-durable; an
    /// intermediate snapshot skips it and, as Python's <c>write_local_snapshot</c>, tolerates the destination
    /// being held open by a reader (the write is skipped with a warning and retried at the next flush). Returns
    /// whether the destination was written. Cancellation mid-copy removes the partial temp file.
    /// </summary>
    public async Task<bool> FlushAsync(bool fsync, CancellationToken cancellationToken)
    {
        using (await LockAsync(cancellationToken).ConfigureAwait(false))
        {
            _zip.WriteCentralDirectory();
            var written = await WriteLocalSnapshotAsync(fsync, cancellationToken).ConfigureAwait(false);
            if (written)
            {
                _streamingSamples.Clear();
                _destinationWritten = true;
            }

            return written;
        }
    }

    /// <summary>
    /// Port of <c>close</c>: the header read back from the temp file, with (unless <paramref name="headerOnly"/>)
    /// samples that load from the destination on first access. The temp file is deleted.
    /// </summary>
    public async Task<EvalLog> CloseAsync(bool headerOnly, CancellationToken cancellationToken)
    {
        using (await LockAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                _zip.WriteCentralDirectory();
                _temp.Position = 0;
                using var reader = ZipLogReader.FromStream(_temp);
                var log = EvalLogReading.ReadLog(reader, _file, headerOnly: true);
                if (!headerOnly)
                {
                    var file = _file;
                    var loaded = new Lazy<EvalLog>(() => EvalLogFiles.ReadEvalLog(file), LazyThreadSafetyMode.ExecutionAndPublication);
                    log = log with { Samples = new LazyList<EvalSample>(() => loaded.Value.Samples ?? []) };
                    if (reader.Contains(EvalLogFormat.ReductionsJson))
                    {
                        log = log with { Reductions = new LazyList<EvalSampleReductions>(() => loaded.Value.Reductions ?? []) };
                    }
                }

                return log;
            }
            finally
            {
                _zip.Dispose();
                _disposed = true;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _zip.Dispose();
        }

        _lock.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Port of <c>_journal_summary</c>: journals one sample's summary and merges it into the consolidated list. Caller holds the lock.</summary>
    private void JournalSummary(EvalSample sample)
    {
        _summaryCounter++;
        var summary = sample.Summary();
        ZipWriteStr(EvalLogFormat.JournalSummaryPath(EvalLogFormat.JournalSummaryFile(_summaryCounter)), new List<EvalSampleSummary> { summary });
        var key = EvalLogFormat.SampleKey(summary.Id, summary.Epoch);
        _summaries.RemoveAll(s => EvalLogFormat.SampleKey(s.Id, s.Epoch) == key);
        _summaries.Add(summary);
    }

    /// <summary>Port of <c>_zip_writestr</c>: a repeated name is a deliberate superseding record (readers resolve names to the last entry).</summary>
    private void ZipWriteStr<T>(string filename, T data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _zip.AddMember(filename, EvalJson.Serialize(data));
    }

    private async Task<bool> WriteLocalSnapshotAsync(bool fsync, CancellationToken cancellationToken)
    {
        var destination = Path.GetFullPath(_file);
        var directory = Path.GetDirectoryName(destination) ?? throw new IOException($"The log path '{_file}' has no directory.");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
            await using (output.ConfigureAwait(false))
            {
                _temp.Position = 0;
                await _temp.CopyToAsync(output, 1 << 20, cancellationToken).ConfigureAwait(false);
                if (fsync)
                {
                    output.Flush(flushToDisk: true);
                }
            }

            System.IO.File.Move(temp, destination, overwrite: true);
            return true;
        }
        catch (Exception ex) when (!fsync && IsFileInUse(ex))
        {
            ProviderLogger.Warning($"Skipped intermediate log write for {_file} (file in use by another program): {ex.Message}");
            return false;
        }
        finally
        {
            if (System.IO.File.Exists(temp))
            {
                System.IO.File.Delete(temp);
            }
        }
    }

    /// <summary>
    /// The <c>PermissionError</c> filter of <c>write_local_snapshot</c>: an intermediate snapshot is skipped only when
    /// the destination is held open by another program (Windows denies the replace with access denied, a sharing or
    /// a lock violation; on Unix the equivalent is an <see cref="UnauthorizedAccessException"/>). Any other I/O
    /// failure (disk full, an unmounted volume, a directory in the way) propagates from the first affected flush.
    /// </summary>
    private static bool IsFileInUse(Exception ex) =>
        ex is UnauthorizedAccessException || (ex is IOException && ex.HResult is ErrorSharingViolation or ErrorLockViolation);

    private const int ErrorSharingViolation = unchecked((int)0x80070020);

    private const int ErrorLockViolation = unchecked((int)0x80070021);

    private async Task<Releaser> LockAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(_lock);
    }

    private readonly record struct BufferedSample(EvalSample Sample, EvalSampleSummary Summary);

    private readonly struct Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}

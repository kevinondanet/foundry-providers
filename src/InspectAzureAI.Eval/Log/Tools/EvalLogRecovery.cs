using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner.Scoring;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Log.Tools;

/// <summary>
/// Port of <c>log/_recover/_api.py</c> <c>RecoveryNotAvailable</c>: there is nothing to recover — the log is already
/// complete, is not a journaled <c>.eval</c> file, or a successful log for the task already exists where the
/// recovered file would go. A normal condition, not an error; opportunistic callers catch it silently.
/// </summary>
public sealed class RecoveryNotAvailableException(string message) : Exception(message);

/// <summary>Port of <c>log/_recover/_read.py</c> <c>CrashedEvalLog</c>: what a crashed <c>.eval</c> file still holds.</summary>
public sealed record CrashedEvalLog(string Location, int Version, EvalSpec Eval, EvalPlan Plan)
{
    /// <summary>Sample summaries from the journal flushes, in flush order (not de-duplicated, as Python).</summary>
    public IReadOnlyList<EvalSampleSummary> Summaries { get; init; } = [];

    /// <summary>Zip member names of the flushed samples (<c>samples/{id}_epoch_{n}.json</c>), sorted.</summary>
    public IReadOnlyList<string> SampleEntries { get; init; } = [];

    /// <summary>Mid-run config changes from the journal (see <see cref="EvalLog.ConfigUpdates"/>).</summary>
    public IReadOnlyList<ConfigUpdate> ConfigUpdates { get; init; } = [];
}

/// <summary>
/// Port of <c>RecoverableEvalLog</c>: a crashed log and what recovery can get back. This port has no sample buffer
/// database, so <see cref="CompletedSamples"/> and <see cref="InProgressSamples"/> (the buffer's counts in Python)
/// are always zero and <see cref="Source"/> is <c>journal</c>.
/// </summary>
public sealed record RecoverableEvalLog(EvalLogInfo Log, int FlushedSamples, int CompletedSamples, int InProgressSamples, int TotalSamples, string Source = EvalLogRecovery.JournalSource);

/// <summary>Port of <c>recover_eval_log</c>'s return plus <c>RecoveryStats</c>: the recovered log (header, samples lazily loaded from the file) and the counts of samples written and of those that failed (errored or cancelled).</summary>
public sealed record RecoveryResult(EvalLog Log, int SampleCount, int FailedCount);

/// <summary>
/// Port of <c>log/_recover</c> for the journal of an incomplete <c>.eval</c> file: a crashed eval leaves
/// <c>_journal/start.json</c>, the samples of every completed flush and their <c>_journal/summaries/{n}.json</c>
/// batches, but no <c>header.json</c>. Recovery streams those samples into a new, complete <c>.eval</c> (status
/// <c>error</c>, results and stats recomputed from the samples). Python additionally recovers the unflushed samples
/// from its SQLite sample buffer, which this port does not have: recovery here is journal-only, so samples buffered
/// since the last flush are lost (and unlike Python, the absence of a buffer is not a reason to refuse recovery).
/// </summary>
public static class EvalLogRecovery
{
    /// <summary>The <see cref="RecoverableEvalLog.Source"/> of this port's recovery.</summary>
    public const string JournalSource = "journal";

    /// <summary>Port of <c>_FLUSH_INTERVAL</c>: the recovered file is flushed to disk every this many samples to bound memory.</summary>
    public const int FlushInterval = 10;

    private const string RecoveredSuffix = "-recovered";

    /// <summary>Port of <c>default_output_path</c>: <c>name.eval</c> → <c>name-recovered.eval</c> (or <c>name-recovered</c> without the extension).</summary>
    public static string DefaultOutputPath(string location)
    {
        ArgumentException.ThrowIfNullOrEmpty(location);
        var extension = LogFormat.Eval.Extension();
        return location.EndsWith(extension, StringComparison.Ordinal)
            ? location[..^extension.Length] + RecoveredSuffix + extension
            : location + RecoveredSuffix;
    }

    /// <summary>
    /// Port of <c>read_crashed_eval_log</c>: the start data, journal summaries, config updates and sample member names
    /// of a crashed <c>.eval</c> file (sample data is not read).
    /// </summary>
    /// <exception cref="RecoveryNotAvailableException">The log is complete (has <c>header.json</c>) or is not a journaled log (no <c>_journal/start.json</c>) — Python's <c>ValueError</c>.</exception>
    public static Task<CrashedEvalLog> ReadCrashedEvalLogAsync(string location, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(location);
        return Task.Run(() => ReadCrashedEvalLog(location), cancellationToken);
    }

    /// <summary>Port of <c>read_flushed_sample</c>: one flushed sample of a crashed log, pool references resolved.</summary>
    internal static EvalSample ReadFlushedSample(ZipLogReader reader, string entryName) =>
        EvalJson.ParseSample(EvalLogReading.ReadObject(reader, entryName), entryName);

    /// <summary>
    /// Port of <c>write_recovered_eval_log</c>: streams the flushed samples of <paramref name="crashed"/> (and then
    /// <paramref name="extraSamples"/>, a caller's own source such as Python's buffer) into <paramref name="output"/>
    /// one at a time, condensing each, flushing every <see cref="FlushInterval"/>. Stats aggregate the samples' model
    /// and role usage (the start time is the earliest of the eval's creation and the samples' starts; completion is
    /// now); results are recomputed from the samples' scores with the header's scorers, reducers and metrics — a
    /// failure there is a <see cref="ProviderLogger"/> warning and no results, as in Python. The log ends with status
    /// <see cref="EvalStatus.Error"/> and an <see cref="EvalError"/> saying it was recovered.
    /// </summary>
    public static async Task<RecoveryResult> WriteRecoveredEvalLogAsync(CrashedEvalLog crashed, string output, IEnumerable<EvalSample>? extraSamples = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(crashed);
        ArgumentException.ThrowIfNullOrEmpty(output);
        var outputDir = Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".";
        var recorder = new EvalRecorder(outputDir);
        await using (recorder.ConfigureAwait(false))
        {
            await recorder.LogInitAsync(crashed.Eval, output, clean: true, cancellationToken).ConfigureAwait(false);
            await recorder.LogStartAsync(crashed.Eval, crashed.Plan, cancellationToken).ConfigureAwait(false);

            var sampleCount = 0;
            var failedCount = 0;
            var stats = new StatsAccumulator(crashed.Eval.Created);
            var scores = new List<IReadOnlyDictionary<string, SampleScore>>();

            async Task WriteSampleAsync(EvalSample sample)
            {
                stats.Add(sample.StartedAt, sample.ModelUsage, sample.RoleUsage);
                if (sample.Scores is { Count: > 0 } sampleScores)
                {
                    var entry = new OrderedDictionary<string, SampleScore>(StringComparer.Ordinal);
                    foreach (var (name, score) in sampleScores)
                    {
                        entry[name] = new SampleScore(score, sample.Id, sample.Metadata);
                    }

                    scores.Add(entry);
                }

                if (sample.Error is not null)
                {
                    failedCount++;
                }

                await recorder.LogSampleAsync(crashed.Eval, LogAttachments.CondenseSample(sample), cancellationToken: cancellationToken).ConfigureAwait(false);
                sampleCount++;
                if (sampleCount % FlushInterval == 0)
                {
                    await recorder.FlushAsync(crashed.Eval, cancellationToken).ConfigureAwait(false);
                }
            }

            if (crashed.SampleEntries.Count > 0)
            {
                using var reader = ZipLogReader.Open(crashed.Location);
                foreach (var entryName in crashed.SampleEntries)
                {
                    var sample = await Task.Run(() => ReadFlushedSample(reader, entryName), cancellationToken).ConfigureAwait(false);
                    await WriteSampleAsync(sample).ConfigureAwait(false);
                }
            }

            foreach (var sample in extraSamples ?? [])
            {
                await WriteSampleAsync(sample).ConfigureAwait(false);
            }

            var header = new EvalLog { Version = crashed.Version, Eval = crashed.Eval, Plan = crashed.Plan, Status = EvalStatus.Error };
            ComputedResults? computed = null;
            try
            {
                computed = HeaderScorers.ComputeResults(
                    sampleCount,
                    scores,
                    HeaderScorers.FromLog(header),
                    LogHeader.ReducersFromLogHeader(header),
                    LogHeader.MetricsFromLogHeader(header),
                    // failedCount covers errored (and, from a buffer, still-in-progress) samples, so the remainder is exactly the samples that completed cleanly
                    sampleCount - failedCount,
                    header.Eval.HeadlineMetric);
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException)
            {
                ProviderLogger.Warning($"Unable to recompute metrics for recovered log: {ex.Message}");
            }

            var error = new EvalError(
                "Eval recovered from crash",
                "Eval process crashed; log recovered from the .eval journal.\n",
                "Eval process crashed; log recovered from the .eval journal.\n");
            var log = await recorder.LogFinishAsync(
                crashed.Eval,
                EvalStatus.Error,
                stats.Stats(),
                computed?.Results,
                computed?.Reductions,
                error,
                configUpdates: crashed.ConfigUpdates.Count > 0 ? crashed.ConfigUpdates : null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new RecoveryResult(log, sampleCount, failedCount);
        }
    }

    /// <summary>
    /// Port of <c>recover_eval_log</c> / <c>recover_eval_log_async</c>: recovers a crashed <c>.eval</c> file into
    /// <paramref name="output"/> (default: <see cref="DefaultOutputPath"/> alongside it), or with
    /// <paramref name="overwrite"/> in place — written to the default sibling path first, then moved over the crashed
    /// file. Recovery is refused when the output directory already holds a successful log of the same task, since a
    /// newer recovered file (status <c>error</c>) would become the latest by modification time and interfere with
    /// eval set state; write elsewhere in that case.
    /// </summary>
    /// <exception cref="RecoveryNotAvailableException">The log is not a crashed journaled <c>.eval</c>, or a successful log of the task exists in the output directory.</exception>
    public static async Task<RecoveryResult> RecoverEvalLogAsync(string log, string? output = null, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(log);
        var crashed = await ReadCrashedEvalLogAsync(log, cancellationToken).ConfigureAwait(false);

        string? finalOutput;
        string writeOutput;
        if (overwrite)
        {
            finalOutput = log;
            writeOutput = DefaultOutputPath(log);
        }
        else
        {
            finalOutput = null;
            writeOutput = output ?? DefaultOutputPath(log);
        }

        var outputDir = Path.GetDirectoryName(Path.GetFullPath(writeOutput)) ?? ".";
        var taskId = crashed.Eval.TaskId;
        var existing = await EvalLogFiles.ListEvalLogsAsync(
            outputDir,
            filter: header => header.Status == EvalStatus.Success && string.Equals(header.Eval.TaskId, taskId, StringComparison.Ordinal),
            recursive: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (existing.Count > 0)
        {
            throw new RecoveryNotAvailableException(
                $"A successful log for task '{crashed.Eval.Task}' already exists in {outputDir}. Use output= to write the recovered file to a different location.");
        }

        var result = await WriteRecoveredEvalLogAsync(crashed, writeOutput, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (finalOutput is not null)
        {
            File.Delete(finalOutput);
            File.Move(writeOutput, finalOutput);
            var reread = await EvalLogFiles.ReadEvalLogAsync(finalOutput, cancellationToken: cancellationToken).ConfigureAwait(false);
            result = result with { Log = reread };
        }

        return result;
    }

    /// <summary>
    /// Port of <c>recoverable_eval_logs</c>: the crashed (status <c>started</c>) <c>.eval</c> logs under
    /// <paramref name="logDir"/> (<c>INSPECT_LOG_DIR</c> or <c>./logs</c> when null) that have a readable journal and
    /// no <c>-recovered.eval</c> sibling yet. Python also requires a sample buffer database; this port lists every
    /// journaled crashed log. A log whose journal cannot be read reports zero flushed and total samples, as Python.
    /// </summary>
    public static async Task<IReadOnlyList<RecoverableEvalLog>> RecoverableEvalLogsAsync(string? logDir = null, CancellationToken cancellationToken = default)
    {
        var directory = string.IsNullOrEmpty(logDir) ? Environment.GetEnvironmentVariable(LogFormats.LogDirEnvironmentVariable) is { Length: > 0 } env ? env : "./logs" : logDir;
        var crashedLogs = await EvalLogFiles.ListEvalLogsAsync(
            directory,
            formats: [LogFormat.Eval],
            filter: header => header.Status == EvalStatus.Started,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var result = new List<RecoverableEvalLog>();
        foreach (var info in crashedLogs)
        {
            if (File.Exists(DefaultOutputPath(info.Name)))
            {
                continue;
            }

            int flushed;
            int total;
            try
            {
                var crashed = await ReadCrashedEvalLogAsync(info.Name, cancellationToken).ConfigureAwait(false);
                flushed = crashed.SampleEntries.Count;
                total = (crashed.Eval.Dataset.Samples ?? 0) * (crashed.Eval.Config.Epochs ?? 1);
            }
            catch (RecoveryNotAvailableException)
            {
                continue;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                flushed = 0;
                total = 0;
            }

            result.Add(new RecoverableEvalLog(info, flushed, CompletedSamples: 0, InProgressSamples: 0, total));
        }

        return result;
    }

    private static CrashedEvalLog ReadCrashedEvalLog(string location)
    {
        using var reader = ZipLogReader.Open(location);
        var names = reader.Names.ToHashSet(StringComparer.Ordinal);
        if (names.Contains(EvalLogFormat.HeaderJson))
        {
            throw new RecoveryNotAvailableException($"Log is not crashed (has {EvalLogFormat.HeaderJson}): {location}");
        }

        var startPath = EvalLogFormat.JournalPath(EvalLogFormat.StartJson);
        if (!names.Contains(startPath))
        {
            throw new RecoveryNotAvailableException($"Log is invalid (missing {startPath}): {location}");
        }

        var start = EvalLogReading.ReadStart(reader, names) ?? throw new InvalidDataException($"{startPath} could not be read: {location}");
        var summaries = ReadJournalSummaries(reader, names);
        var (configUpdates, _) = EvalLogReading.ReadConfigUpdates(reader);
        var sampleEntries = names.Where(EvalLogFormat.IsSampleEntry).Order(StringComparer.Ordinal).ToList();
        return new CrashedEvalLog(location, start.Version, start.Eval, start.Plan)
        {
            Summaries = summaries,
            SampleEntries = sampleEntries,
            ConfigUpdates = configUpdates,
        };
    }

    /// <summary>Port of <c>_read_journal_summaries</c>: the consolidated <c>summaries.json</c> if present (it should not be, for a crashed log), else the journal batches in index order.</summary>
    private static List<EvalSampleSummary> ReadJournalSummaries(ZipLogReader reader, IReadOnlySet<string> names)
    {
        if (names.Contains(EvalLogFormat.SummariesJson))
        {
            return ParseSummaries(reader, EvalLogFormat.SummariesJson);
        }

        var prefix = EvalLogFormat.JournalSummaryPath() + "/";
        var batches = new List<(int Index, string Name)>();
        foreach (var name in names)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
            {
                var stem = name.Split('/')[^1].Split('.')[0];
                if (int.TryParse(stem, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index))
                {
                    batches.Add((index, name));
                }
            }
        }

        var summaries = new List<EvalSampleSummary>();
        foreach (var (_, name) in batches.OrderBy(batch => batch.Index))
        {
            summaries.AddRange(ParseSummaries(reader, name));
        }

        return summaries;
    }

    private static List<EvalSampleSummary> ParseSummaries(ZipLogReader reader, string member)
    {
        var node = EvalLogReading.ReadMember(reader, member);
        return node is System.Text.Json.Nodes.JsonArray
            ? EvalJson.Deserialize<List<EvalSampleSummary>>(node, member)
            : throw new InvalidDataException($"Expected a list of summaries in {member}, got {node.GetValueKind()}");
    }

    /// <summary>Port of <c>_StatsAccumulator</c>: the earliest start seen and the model/role usage summed over the streamed samples.</summary>
    private sealed class StatsAccumulator(DateTimeOffset startedAt)
    {
        private readonly Dictionary<string, ModelUsage> _modelUsage = new(StringComparer.Ordinal);

        private readonly Dictionary<string, ModelUsage> _roleUsage = new(StringComparer.Ordinal);

        private DateTimeOffset _startedAt = startedAt;

        public void Add(DateTimeOffset? sampleStartedAt, IReadOnlyDictionary<string, ModelUsage> modelUsage, IReadOnlyDictionary<string, ModelUsage> roleUsage)
        {
            if (sampleStartedAt is { } started && started < _startedAt)
            {
                _startedAt = started;
            }

            foreach (var (model, usage) in modelUsage)
            {
                _modelUsage[model] = (_modelUsage.TryGetValue(model, out var current) ? current : new ModelUsage()) + usage;
            }

            foreach (var (role, usage) in roleUsage)
            {
                _roleUsage[role] = (_roleUsage.TryGetValue(role, out var current) ? current : new ModelUsage()) + usage;
            }
        }

        public EvalStats Stats() => new()
        {
            StartedAt = _startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            ModelUsage = _modelUsage,
            RoleUsage = _roleUsage,
        };
    }
}

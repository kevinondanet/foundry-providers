using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InspectAzureAI.Eval.Log.EvalFormat;

/// <summary>Port of <c>log/_file.py</c> <c>EvalLogInfo</c>: file info and task identifiers of an eval log.</summary>
public sealed record EvalLogInfo(string Name, string Type, long Size, double? Mtime, string Task, string TaskId, string? Suffix);

/// <summary>Port of <c>_util/file.py</c> <c>FileInfo</c>: one entry of a directory listing (<see cref="Mtime"/> is seconds since the Unix epoch).</summary>
public sealed record FileEntry(string Name, string Type, long Size, double? Mtime);

/// <summary>
/// Port of <c>log/_file.py</c>: reading and writing logs in either format (<see cref="ReadEvalLog(string, bool, ResolveAttachments, LogFormat?, ISet{string}?)"/>,
/// <see cref="WriteEvalLog"/>), single-sample and summary reads, and listing a log directory
/// (<see cref="ListEvalLogs"/>). Local paths only: the S3 / fsspec branches of the Python module are not ported.
/// </summary>
public static partial class EvalLogFiles
{
    /// <summary>Port of <c>list_eval_logs</c>: the log files under <paramref name="logDir"/> (<c>INSPECT_LOG_DIR</c> or <c>logs</c> when null), newest first.</summary>
    /// <param name="logDir">Log directory.</param>
    /// <param name="formats">Formats to list (all when null).</param>
    /// <param name="filter">Keeps only logs whose header (samples not loaded) passes.</param>
    /// <param name="recursive">List subdirectories too.</param>
    /// <param name="descending">Newest first (by modification time).</param>
    public static IReadOnlyList<EvalLogInfo> ListEvalLogs(string? logDir = null, IReadOnlyCollection<LogFormat>? formats = null, Func<EvalLog, bool>? filter = null, bool recursive = true, bool descending = true)
    {
        var directory = logDir ?? LogFormats.DefaultLogDir;
        var logs = Directory.Exists(directory) ? LogFilesFromLs(ListDirectory(directory, recursive), formats, descending) : [];
        return filter is null ? logs : logs.Where(log => filter(ReadEvalLog(log.Name, headerOnly: true))).ToList();
    }

    /// <summary>Port of <c>list_eval_logs_async</c>.</summary>
    public static Task<IReadOnlyList<EvalLogInfo>> ListEvalLogsAsync(string? logDir = null, IReadOnlyCollection<LogFormat>? formats = null, Func<EvalLog, bool>? filter = null, bool recursive = true, bool descending = true, CancellationToken cancellationToken = default) =>
        Task.Run(() => ListEvalLogs(logDir, formats, filter, recursive, descending), cancellationToken);

    /// <summary>
    /// Port of <c>read_eval_log</c>. The format follows the extension unless <paramref name="format"/> is given;
    /// <paramref name="headerOnly"/> leaves <see cref="EvalLog.Samples"/> null; <paramref name="resolveAttachments"/>
    /// puts attachment content back into the samples; <paramref name="excludeFields"/> skips sample fields
    /// (<c>.eval</c> only). Missing dataset sample ids are filled in from the samples, as in Python.
    /// </summary>
    public static EvalLog ReadEvalLog(string logFile, bool headerOnly = false, ResolveAttachments resolveAttachments = ResolveAttachments.None, LogFormat? format = null, ISet<string>? excludeFields = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(logFile);
        var log = (format ?? LogFormats.ForLocation(logFile)) switch
        {
            LogFormat.Eval => EvalRecorder.ReadLog(logFile, headerOnly, excludeFields),
            _ => JsonRecorder.ReadLog(logFile, headerOnly),
        };
        return FinishRead(log, resolveAttachments);
    }

    /// <summary>Port of <c>read_eval_log</c> for an <see cref="EvalLogInfo"/> from <see cref="ListEvalLogs"/>.</summary>
    public static EvalLog ReadEvalLog(EvalLogInfo logFile, bool headerOnly = false, ResolveAttachments resolveAttachments = ResolveAttachments.None, LogFormat? format = null, ISet<string>? excludeFields = null)
    {
        ArgumentNullException.ThrowIfNull(logFile);
        return ReadEvalLog(logFile.Name, headerOnly, resolveAttachments, format, excludeFields);
    }

    /// <summary>Port of <c>read_eval_log</c> on bytes: the format is detected from the first bytes unless given; the result has no <see cref="EvalLog.Location"/>.</summary>
    public static EvalLog ReadEvalLog(Stream logBytes, bool headerOnly = false, ResolveAttachments resolveAttachments = ResolveAttachments.None, LogFormat? format = null)
    {
        ArgumentNullException.ThrowIfNull(logBytes);
        if (!logBytes.CanSeek)
        {
            var memory = new MemoryStream();
            logBytes.CopyTo(memory);
            memory.Position = 0;
            logBytes = memory;
        }

        if (format is null)
        {
            var start = logBytes.Position;
            Span<byte> first = stackalloc byte[4];
            var read = logBytes.Read(first);
            logBytes.Position = start;
            format = LogFormats.ForBytes(first[..read]);
        }

        var log = format == LogFormat.Eval ? EvalRecorder.ReadLogBytes(logBytes, headerOnly) : JsonRecorder.ReadLogBytes(logBytes, headerOnly);
        return FinishRead(log, resolveAttachments);
    }

    /// <summary>Port of <c>read_eval_log_async</c>.</summary>
    public static Task<EvalLog> ReadEvalLogAsync(string logFile, bool headerOnly = false, ResolveAttachments resolveAttachments = ResolveAttachments.None, LogFormat? format = null, ISet<string>? excludeFields = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadEvalLog(logFile, headerOnly, resolveAttachments, format, excludeFields), cancellationToken);

    /// <summary>Port of <c>read_eval_log_headers</c>: the header of every log file.</summary>
    public static IReadOnlyList<EvalLog> ReadEvalLogHeaders(IEnumerable<string> logFiles)
    {
        ArgumentNullException.ThrowIfNull(logFiles);
        return logFiles.Select(file => ReadEvalLog(file, headerOnly: true)).ToList();
    }

    public static IReadOnlyList<EvalLog> ReadEvalLogHeaders(IEnumerable<EvalLogInfo> logFiles)
    {
        ArgumentNullException.ThrowIfNull(logFiles);
        return ReadEvalLogHeaders(logFiles.Select(info => info.Name));
    }

    /// <summary>
    /// Port of <c>read_eval_log_sample</c>: one sample by <paramref name="id"/> and <paramref name="epoch"/>, or by
    /// <paramref name="uuid"/>. Neither given is an <see cref="ArgumentException"/>; a sample that is not in the log
    /// is a <see cref="KeyNotFoundException"/> (Python's <c>IndexError</c>).
    /// </summary>
    public static EvalSample ReadEvalLogSample(string logFile, object? id = null, int epoch = 1, string? uuid = null, ResolveAttachments resolveAttachments = ResolveAttachments.None, LogFormat? format = null, ISet<string>? excludeFields = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(logFile);
        var sample = (format ?? LogFormats.ForLocation(logFile)) switch
        {
            LogFormat.Eval => EvalRecorder.ReadLogSample(logFile, id, epoch, uuid, excludeFields),
            _ => JsonRecorder.ReadLogSample(logFile, id, epoch, uuid),
        };
        return resolveAttachments == ResolveAttachments.None ? sample : LogAttachments.ResolveSampleAttachments(sample, resolveAttachments);
    }

    public static Task<EvalSample> ReadEvalLogSampleAsync(string logFile, object? id = null, int epoch = 1, string? uuid = null, ResolveAttachments resolveAttachments = ResolveAttachments.None, LogFormat? format = null, ISet<string>? excludeFields = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadEvalLogSample(logFile, id, epoch, uuid, resolveAttachments, format, excludeFields), cancellationToken);

    /// <summary>Port of <c>read_eval_log_sample_summaries</c>.</summary>
    public static IReadOnlyList<EvalSampleSummary> ReadEvalLogSampleSummaries(string logFile, LogFormat? format = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(logFile);
        return (format ?? LogFormats.ForLocation(logFile)) switch
        {
            LogFormat.Eval => EvalRecorder.ReadLogSampleSummaries(logFile),
            _ => JsonRecorder.ReadLogSampleSummaries(logFile),
        };
    }

    /// <summary>
    /// Port of <c>read_eval_log_samples</c>: the samples one at a time, in dataset order and epoch order. A log
    /// without dataset sample ids, or (with <paramref name="allSamplesRequired"/>) one that did not succeed or was
    /// invalidated, is an <see cref="InvalidOperationException"/>; a missing sample is skipped unless required.
    /// </summary>
    public static IEnumerable<EvalSample> ReadEvalLogSamples(string logFile, bool allSamplesRequired = true, ResolveAttachments resolveAttachments = ResolveAttachments.None, LogFormat? format = null, ISet<string>? excludeFields = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(logFile);
        var header = ReadEvalLog(logFile, headerOnly: true, format: format);
        if (header.Eval.Dataset.SampleIds is not { } sampleIds)
        {
            throw new InvalidOperationException("This log file does not include sample_ids (fully reading and re-writing the log will add sample_ids)");
        }

        if (allSamplesRequired && (header.Status != EvalStatus.Success || header.Invalidated))
        {
            throw new InvalidOperationException($"This log does not have all samples (status={header.Status.ToString().ToLowerInvariant()}). Specify allSamplesRequired=false to read the samples that exist.");
        }

        return Enumerate();

        IEnumerable<EvalSample> Enumerate()
        {
            var epochs = header.Eval.Config.Epochs ?? 1;
            foreach (var sampleId in sampleIds)
            {
                for (var epoch = 1; epoch <= epochs; epoch++)
                {
                    EvalSample sample;
                    try
                    {
                        sample = ReadEvalLogSample(logFile, sampleId, epoch, resolveAttachments: resolveAttachments, format: format, excludeFields: excludeFields);
                    }
                    catch (KeyNotFoundException) when (!allSamplesRequired)
                    {
                        continue;
                    }

                    yield return sample;
                }
            }
        }
    }

    /// <summary>
    /// Port of <c>write_eval_log</c>: writes <paramref name="log"/> to <paramref name="location"/> (its own
    /// <see cref="EvalLog.Location"/> when null) in the format the extension selects unless <paramref name="format"/>
    /// is given. <paramref name="headerOnly"/> updates only the header: a <c>.eval</c> gets its <c>header.json</c>
    /// replaced in place, a <c>.json</c> is rewritten with the samples already on disk.
    /// </summary>
    public static void WriteEvalLog(EvalLog log, string? location = null, LogFormat? format = null, bool headerOnly = false) =>
        Task.Run(() => WriteEvalLogAsync(log, location, format, headerOnly)).GetAwaiter().GetResult();

    /// <summary>Port of <c>write_eval_log_async</c>.</summary>
    public static Task WriteEvalLogAsync(EvalLog log, string? location = null, LogFormat? format = null, bool headerOnly = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        location ??= log.Location;
        if (string.IsNullOrEmpty(location))
        {
            throw new ArgumentException("EvalLog passed to write_eval_log does not have a location, so you must pass an explicit location", nameof(location));
        }

        return (format ?? LogFormats.ForLocation(location)) switch
        {
            LogFormat.Eval => EvalRecorder.WriteLogAsync(location, log, headerOnly, cancellationToken),
            _ => JsonRecorder.WriteLogAsync(location, log, headerOnly, cancellationToken),
        };
    }

    /// <summary>Port of <c>write_log_dir_manifest</c>: a JSON dictionary of log headers keyed by log file name relative to the directory.</summary>
    public static void WriteLogDirManifest(string logDir, string filename = "logs.json", string? outputDir = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(logDir);
        var directory = Path.GetFullPath(logDir);
        var logs = ListEvalLogs(directory);
        var names = logs.Select(log => ManifestEvalLogName(log, directory, Path.DirectorySeparatorChar.ToString())).ToList();
        var headers = ReadEvalLogHeaders(logs);
        var manifest = new Dictionary<string, EvalLog>(StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++)
        {
            manifest[names[i]] = headers[i];
        }

        var output = outputDir ?? directory;
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, filename), JsonSerializer.Serialize(manifest, EvalLogWriter.Options), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Port of <c>manifest_eval_log_name</c>: the log name relative to the directory, with forward slashes.</summary>
    public static string ManifestEvalLogName(EvalLogInfo info, string logDir, string sep)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(logDir);
        ArgumentNullException.ThrowIfNull(sep);
        if (!logDir.EndsWith(sep, StringComparison.Ordinal))
        {
            logDir += sep;
        }

        return info.Name.Replace(logDir, "", StringComparison.Ordinal).Replace('\\', '/');
    }

    /// <summary>Port of <c>eval_log_json</c>: the log as Python-format JSON text.</summary>
    public static string EvalLogJson(EvalLog log) => EvalLogWriter.Serialize(log);

    /// <summary>Port of <c>log_files_from_ls</c>: the log files of a listing (sorted by modification time), resolved to <see cref="EvalLogInfo"/>.</summary>
    public static IReadOnlyList<EvalLogInfo> LogFilesFromLs(IReadOnlyList<FileEntry> ls, IReadOnlyCollection<LogFormat>? formats = null, bool descending = true, bool sort = true)
    {
        ArgumentNullException.ThrowIfNull(ls);
        var extensions = (formats ?? LogFormats.All).Select(format => format.Extension()).ToList();
        IEnumerable<FileEntry> ordered = ls;
        if (sort)
        {
            ordered = descending ? ls.OrderByDescending(file => file.Mtime ?? 0) : ls.OrderBy(file => file.Mtime ?? 0);
        }

        return ordered.Where(file => file.Type == "file" && IsLogFile(file.Name, extensions)).Select(LogFileInfo).ToList();
    }

    /// <summary>Port of <c>is_log_file</c>: any <c>.eval</c>, or a timestamp-prefixed name with one of <paramref name="extensions"/>.</summary>
    public static bool IsLogFile(string file, IReadOnlyCollection<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(extensions);
        var name = file.Replace('\\', '/').Split('/')[^1];
        if (name.EndsWith(LogFormat.Eval.Extension(), StringComparison.Ordinal))
        {
            return true;
        }

        return LogFilePattern().IsMatch(name) && extensions.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Port of <c>log_file_info</c>: task, task id and suffix parsed from a <c>{timestamp}_{task}_{id}</c> name;
    /// other names fall back to the log header (empty fields when it cannot be read, as Python degrades).
    /// </summary>
    public static EvalLogInfo LogFileInfo(FileEntry info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var parts = FilenameParts(info);
        var (task, taskId, suffix) = TryParseFilename(parts);
        if (task is null)
        {
            (task, taskId, suffix) = TryReadHeader(info.Name);
        }

        return new EvalLogInfo(info.Name, info.Type, info.Size, info.Mtime, task ?? "", taskId ?? "", suffix);
    }

    /// <summary>Port of <c>_try_parse_filename</c>.</summary>
    internal static (string? Task, string? TaskId, string? Suffix) TryParseFilename(IReadOnlyList<string> parts)
    {
        if (parts.Count < 2 || !TimestampPrefix().IsMatch(parts[0]))
        {
            return (null, null, null);
        }

        if (parts.Count == 2)
        {
            return (parts[1], "", null);
        }

        // 3+ parts: {ts}_{task}_{id} or {ts}_{task}_{model}_{id}
        var lastIndex = parts.Count > 3 ? 3 : 2;
        var part3 = parts[lastIndex].Split('-');
        return (parts[1], part3[0], part3.Length > 1 ? part3[1] : null);
    }

    private static (string? Task, string? TaskId, string? Suffix) TryReadHeader(string name)
    {
        try
        {
            var log = ReadEvalLog(name, headerOnly: true);
            return (log.Eval.Task, log.Eval.TaskId, null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            return (null, null, null);
        }
    }

    private static List<string> FilenameParts(FileEntry info)
    {
        var basename = Path.GetFileNameWithoutExtension(info.Name.Replace('\\', '/'));
        return basename.Split('_').ToList();
    }

    private static List<FileEntry> ListDirectory(string directory, bool recursive)
    {
        var entries = new List<FileEntry>();
        foreach (var path in Directory.EnumerateFiles(directory, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
        {
            var file = new System.IO.FileInfo(path);
            entries.Add(new FileEntry(Path.GetFullPath(path), "file", file.Length, (file.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds));
        }

        return entries;
    }

    /// <summary>Port of the tail of <c>read_eval_log_async</c>: attachments resolved when requested and missing dataset sample ids filled in.</summary>
    private static EvalLog FinishRead(EvalLog log, ResolveAttachments resolveAttachments)
    {
        if (log.Samples is { } samples)
        {
            if (resolveAttachments != ResolveAttachments.None)
            {
                log = log with { Samples = samples.Select(sample => LogAttachments.ResolveSampleAttachments(sample, resolveAttachments)).ToList() };
            }

            if (log.Eval.Dataset.SampleIds is null)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var ids = new List<object>();
                foreach (var sample in log.Samples!)
                {
                    if (seen.Add(EvalLogFormat.SampleKey(sample.Id, 0)))
                    {
                        ids.Add(sample.Id);
                    }
                }

                log = log with { Eval = log.Eval with { Dataset = log.Eval.Dataset with { SampleIds = ids } } };
            }
        }

        return log;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}[:-]\d{2}[:-]\d{2}.*$")]
    private static partial Regex LogFilePattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}[:-]\d{2}[:-]\d{2}")]
    private static partial Regex TimestampPrefix();
}

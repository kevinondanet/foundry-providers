using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Log.Tools;

/// <summary>
/// Port of <c>log/_convert.py</c> <c>convert_eval_logs</c>: converts a log file, or every log file under a
/// directory (their relative paths preserved), to the <c>.eval</c> or <c>.json</c> format. A file already in the
/// target format is re-written (a full read/write, which normalises deprecated constructs and adds sample summaries),
/// as Python does.
/// </summary>
public static class LogConversion
{
    /// <summary>
    /// Port of <c>convert_eval_logs</c>. Without <paramref name="stream"/> each log is read whole and written back;
    /// with it the samples are streamed one at a time through the target recorder (<paramref name="streamConcurrency"/>
    /// samples in flight, all of them when null; the output is flushed every that many samples), which bounds memory
    /// for large logs. Attachments are resolved when <paramref name="resolveAttachments"/> asks for it; otherwise the
    /// pooled messages and calls are resolved and re-condensed so the pools are rebuilt (Python's
    /// <c>resolve_sample_events_data</c> before <c>condense_sample</c>).
    /// </summary>
    /// <exception cref="PrerequisiteError"><paramref name="path"/> does not exist.</exception>
    /// <exception cref="IOException">An output file exists and <paramref name="overwrite"/> is not set (Python's <c>FileExistsError</c>).</exception>
    public static async Task ConvertEvalLogsAsync(
        string path,
        LogFormat to,
        string outputDir,
        bool overwrite = false,
        ResolveAttachments resolveAttachments = ResolveAttachments.None,
        bool stream = false,
        int? streamConcurrency = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(outputDir);
        if (streamConcurrency is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(streamConcurrency), streamConcurrency, "The stream concurrency must be at least 1.");
        }

        var pathIsDir = Directory.Exists(path);
        if (!pathIsDir && !File.Exists(path))
        {
            throw new PrerequisiteError($"Error: path '{path}' does not exist.");
        }

        outputDir = outputDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (outputDir.Length == 0)
        {
            outputDir = Path.DirectorySeparatorChar.ToString();
        }

        Directory.CreateDirectory(outputDir);

        async Task ConvertFileAsync(string inputFile)
        {
            var inputName = StripExtension(inputFile);
            string targetDir;
            string outputBasename;
            if (pathIsDir)
            {
                var inputDir = Path.GetDirectoryName(inputName.Replace('\\', '/')) ?? "";
                targetDir = Path.Combine(outputDir, inputDir);
                inputFile = Path.Combine(path, inputFile);
                outputBasename = inputName;
            }
            else
            {
                targetDir = outputDir;
                outputBasename = Path.GetFileName(inputName);
            }

            Directory.CreateDirectory(targetDir);
            var outputFile = Path.Combine(outputDir, outputBasename + to.Extension());
            if (File.Exists(outputFile) && !overwrite)
            {
                throw new IOException($"Output file {outputFile} already exists (use --overwrite to overwrite existing files)");
            }

            if (stream)
            {
                // Flushes must not replace the source while deferred sample reads still need it.
                var temporaryFile = Path.Combine(targetDir, $".{Guid.NewGuid():N}{to.Extension()}");
                try
                {
                    await StreamConvertFileAsync(inputFile, temporaryFile, outputDir, resolveAttachments, streamConcurrency, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(temporaryFile, outputFile, overwrite);
                }
                finally
                {
                    File.Delete(temporaryFile);
                }
            }
            else
            {
                var log = EvalLogFiles.ReadEvalLog(inputFile, resolveAttachments: resolveAttachments);
                await EvalLogFiles.WriteEvalLogAsync(log, outputFile, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        if (!pathIsDir)
        {
            await ConvertFileAsync(path).ConfigureAwait(false);
            return;
        }

        var root = Path.GetFullPath(path);
        foreach (var log in EvalLogFiles.ListEvalLogs(root, formats: null, recursive: true, descending: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ConvertFileAsync(Path.GetRelativePath(root, log.Name)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Port of <c>_stream_convert_file</c>. Python opens the output with <c>log_init</c>'s default, which seeds the
    /// new file with the summaries of any file already at the output path; this port always starts clean (the
    /// overwrite decision has been made by then). Python's streaming path also drops the log's <c>error</c>; this port
    /// keeps it.
    /// </summary>
    private static async Task StreamConvertFileAsync(string inputFile, string outputFile, string outputDir, ResolveAttachments resolveAttachments, int? streamConcurrency, CancellationToken cancellationToken)
    {
        var header = EvalLogFiles.ReadEvalLog(inputFile, headerOnly: true, resolveAttachments: resolveAttachments);
        var source = SampleSource(inputFile);
        var concurrentLimit = streamConcurrency ?? Math.Max(1, source.Count);
        var recorder = LogRecorders.CreateForLocation(outputFile, outputDir);
        await using (recorder.ConfigureAwait(false))
        {
            await recorder.LogInitAsync(header.Eval, outputFile, clean: true, cancellationToken).ConfigureAwait(false);
            await recorder.LogStartAsync(header.Eval, header.Plan, cancellationToken).ConfigureAwait(false);

            using var semaphore = new SemaphoreSlim(concurrentLimit, concurrentLimit);
            var processed = 0;

            async Task ConvertSampleAsync(Func<EvalSample> read)
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var sample = await Task.Run(read, cancellationToken).ConfigureAwait(false);
                    if (resolveAttachments != ResolveAttachments.None)
                    {
                        sample = LogAttachments.ResolveSampleAttachments(sample, resolveAttachments);
                    }

                    sample = LogAttachments.CondenseSample(sample);
                    await recorder.LogSampleAsync(header.Eval, sample, cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (Interlocked.Increment(ref processed) % concurrentLimit == 0)
                    {
                        await recorder.FlushAsync(header.Eval, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }

            await Task.WhenAll(source.Select(ConvertSampleAsync)).ConfigureAwait(false);
            await recorder.LogFinishAsync(
                header.Eval,
                header.Status,
                header.Stats,
                header.Results,
                header.Reductions,
                header.Error,
                invalidated: header.Invalidated,
                logUpdates: header.LogUpdates,
                configUpdates: header.ConfigUpdates,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Port of <c>read_log_sample_ids</c> + <c>read_log_sample</c>: one reader per sample. An <c>.eval</c> file is read
    /// sample by sample (pool references resolved, as Python's <c>resolve_sample_events_data</c>); a <c>.json</c>
    /// file can only be read whole, as in Python.
    /// </summary>
    private static List<Func<EvalSample>> SampleSource(string inputFile)
    {
        if (LogFormats.ForLocation(inputFile) == LogFormat.Eval)
        {
            return EvalRecorder.ReadLogSampleIds(inputFile)
                .Select(identity => (Func<EvalSample>)(() => EvalRecorder.ReadLogSample(inputFile, identity.Id, identity.Epoch)))
                .ToList();
        }

        var samples = EvalLogFiles.ReadEvalLog(inputFile).Samples ?? [];
        return samples.Select(sample => (Func<EvalSample>)(() => sample)).ToList();
    }

    private static string StripExtension(string file)
    {
        var extension = Path.GetExtension(file);
        return extension.Length > 0 ? file[..^extension.Length] : file;
    }
}

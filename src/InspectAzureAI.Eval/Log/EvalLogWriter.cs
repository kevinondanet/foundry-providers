using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InspectAzureAI.Eval.Log.Json;

namespace InspectAzureAI.Eval.Log;

/// <summary>
/// Port of <c>log/_file.py</c> <c>write_eval_log</c> / <c>read_eval_log</c> and <c>log/_recorders/json.py</c>
/// <c>JSONRecorder</c> for the plain JSON format: snake_case property names in Python's field order, indented,
/// nulls omitted (<c>exclude_none</c>), samples sorted by epoch then id, and non-finite numbers as the
/// <c>NaN</c> / <c>Infinity</c> constants (<c>ser_json_inf_nan="constants"</c>). Reading validates the format
/// version, tolerates the constants, and recomputes <see cref="EvalLog.Tags"/> / <see cref="EvalLog.Metadata"/>
/// from the log's edits, as Python's validators do.
/// </summary>
public static class EvalLogWriter
{
    /// <summary>The serializer options used for logs; reusable for any of the log's member types.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>Port of <c>sort_samples</c>: epoch, then id (strings as is, ints zero-padded to 20 digits).</summary>
    public static IReadOnlyList<EvalSample> SortSamples(IEnumerable<EvalSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return samples
            .OrderBy(sample => sample.Epoch)
            .ThenBy(sample => sample.Id is string text ? text : Convert.ToString(sample.Id, System.Globalization.CultureInfo.InvariantCulture)!.PadLeft(20, '0'), StringComparer.Ordinal)
            .ToList();
    }

    public static void Write(EvalLog log, string path)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, Serialize(ForWrite(log)), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static async Task WriteAsync(EvalLog log, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, Serialize(ForWrite(log)), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
    }

    public static EvalLog Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Deserialize(File.ReadAllText(path, Encoding.UTF8)) with { Location = path };
    }

    /// <summary>Port of <c>read_eval_log(location, resolve_attachments=...)</c>: the log with each sample's attachments resolved (see <see cref="LogAttachments"/>).</summary>
    public static EvalLog Read(string path, ResolveAttachments resolveAttachments) => ResolveAttachments(Read(path), resolveAttachments);

    public static async Task<EvalLog> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Deserialize(await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false)) with { Location = path };
    }

    public static async Task<EvalLog> ReadAsync(string path, ResolveAttachments resolveAttachments, CancellationToken cancellationToken = default) =>
        ResolveAttachments(await ReadAsync(path, cancellationToken).ConfigureAwait(false), resolveAttachments);

    /// <summary>Port of <c>read_eval_log(location, header_only=True)</c>: the log without <see cref="EvalLog.Samples"/> and <see cref="EvalLog.Reductions"/>.</summary>
    public static EvalLog ReadHeader(string path) => Read(path) with { Samples = null, Reductions = null };

    public static async Task<EvalLog> ReadHeaderAsync(string path, CancellationToken cancellationToken = default) =>
        await ReadAsync(path, cancellationToken).ConfigureAwait(false) with { Samples = null, Reductions = null };

    /// <summary>Every sample of <paramref name="log"/> passed through <see cref="LogAttachments.ResolveSampleAttachments"/>.</summary>
    public static EvalLog ResolveAttachments(EvalLog log, ResolveAttachments mode)
    {
        ArgumentNullException.ThrowIfNull(log);
        return mode == Log.ResolveAttachments.None || log.Samples is null
            ? log
            : log with { Samples = log.Samples.Select(sample => LogAttachments.ResolveSampleAttachments(sample, mode)).ToList() };
    }

    /// <summary>Port of <c>eval_log_json</c>: the log as Python-format JSON text, with tags and metadata recomputed from its edits.</summary>
    public static string Serialize(EvalLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return JsonSerializer.Serialize(EvalLogEditing.RecomputeTagsAndMetadata(log), Options);
    }

    /// <summary>
    /// Port of <c>_parse_json_log</c>: applies the <see cref="LegacyLogMigrations"/>, rejects a version newer than
    /// <see cref="EvalLog.SchemaVersion"/> with <see cref="InvalidDataException"/>, normalises the version, and
    /// recomputes tags and metadata. Malformed JSON is a <see cref="JsonException"/>.
    /// </summary>
    public static EvalLog Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var root = JsonNode.Parse(PythonJsonFormat.SanitizeNonFinite(json)) as JsonObject ?? throw new JsonException("The eval log must be a JSON object.");
        LegacyLogMigrations.Apply(root);
        var log = root.Deserialize<EvalLog>(Options) ?? throw new JsonException("The eval log is empty.");
        if (log.Version > EvalLog.SchemaVersion)
        {
            throw new InvalidDataException($"Unable to read version {log.Version} of log format.");
        }

        return EvalLogEditing.RecomputeTagsAndMetadata(log with { Version = EvalLog.SchemaVersion });
    }

    /// <summary>Port of <c>JSONRecorder.write_log</c>'s preparation: samples sorted as Python sorts them.</summary>
    private static EvalLog ForWrite(EvalLog log) =>
        log.Samples is { Count: > 1 } samples ? log with { Samples = SortSamples(samples) } : log;

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Python writes non-ASCII text verbatim; the default encoder would escape it (and '+', '<', ...)
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters =
            {
                new JsonStringEnumConverter<EvalStatus>(JsonNamingPolicy.SnakeCaseLower),
                new PlainObjectConverter(),
                new PythonDoubleConverter(),
                new IsoDateTimeOffsetConverter(),
                new PythonJsonNodeConverterFactory(),
                new StopReasonConverter(),
                new ToolChoiceConverterFactory(),
                new TargetConverter(),
                new SampleInputConverter(),
                new ContentConverterFactory(),
                new MessageContentConverter(),
                new ChatMessageConverterFactory(),
                new ScoreValueConverterFactory(),
                new ScoreConverter(),
                new ModelCallConverter(),
                new ModelOutputConverter(),
                new TranscriptEventConverterFactory(),
                new EvalSpecConverter(),
                new EvalConfigConverter(),
                new SandboxSpecConverter(),
                new SampleConverter(),
                new LogEditConverterFactory(),
                new ScoreEditConverter(),
                new LoggingMessageConverter(),
                new EvalSampleScoreConverter(),
                new MessageRangeConverter(),
                new EvalPlanStepConverter(),
                new ToolInfoConverter(),
                new TimelineNodeConverterFactory(),
                new CallRefConverter(),
            },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

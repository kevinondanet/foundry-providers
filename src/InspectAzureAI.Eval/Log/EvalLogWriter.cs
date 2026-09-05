using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using InspectAzureAI.Eval.Log.Json;

namespace InspectAzureAI.Eval.Log;

/// <summary>
/// Port of <c>log/_file.py</c> <c>write_eval_log</c> / <c>read_eval_log</c> for the plain JSON format:
/// snake_case property names, indented, nulls omitted. Python emits the non-standard <c>NaN</c> / <c>Infinity</c>
/// constants (<c>ser_json_inf_nan="constants"</c>); this writer keeps the file valid JSON by writing every
/// non-finite number as <c>null</c>, which reads back as NaN where the field is a non-nullable double or a
/// score value (unscored samples) and as null elsewhere.
/// </summary>
public static class EvalLogWriter
{
    /// <summary>The serializer options used for logs; reusable for any of the log's member types.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static void Write(EvalLog log, string path)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, Serialize(log), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static async Task WriteAsync(EvalLog log, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, Serialize(log), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
    }

    public static EvalLog Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Deserialize(File.ReadAllText(path, Encoding.UTF8)) with { Location = path };
    }

    public static async Task<EvalLog> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Deserialize(await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false)) with { Location = path };
    }

    public static string Serialize(EvalLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return JsonSerializer.Serialize(log, Options);
    }

    public static EvalLog Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<EvalLog>(json, Options) ?? throw new JsonException("The eval log is empty.");
    }

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
                new NonFiniteDoubleConverter(),
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
            },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

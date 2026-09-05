namespace InspectAzureAI.Eval.Log.EvalFormat;

/// <summary>Port of the <c>Literal["eval", "json"]</c> log formats (<c>ALL_LOG_FORMATS</c> in <c>_util/constants.py</c>); <see cref="Eval"/> is Python's <c>DEFAULT_LOG_FORMAT</c>.</summary>
public enum LogFormat
{
    /// <summary>The native <c>.eval</c> zip format (incremental, 5-8x smaller than JSON).</summary>
    Eval,

    /// <summary>The plain <c>.json</c> format written by <see cref="EvalLogWriter"/>.</summary>
    Json,
}

/// <summary>Port of the format lookups of <c>log/_recorders/create.py</c> plus the <c>INSPECT_LOG_FORMAT</c> / <c>INSPECT_LOG_DIR</c> environment defaults of the CLI.</summary>
public static class LogFormats
{
    /// <summary>Port of <c>DEFAULT_LOG_FORMAT</c>.</summary>
    public const LogFormat Default = LogFormat.Eval;

    /// <summary>The environment variables the CLI reads the log format from, in precedence order.</summary>
    public static readonly IReadOnlyList<string> FormatEnvironmentVariables = ["INSPECT_LOG_FORMAT", "INSPECT_EVAL_LOG_FORMAT"];

    /// <summary>The environment variable naming the default log directory (<c>./logs</c> when unset).</summary>
    public const string LogDirEnvironmentVariable = "INSPECT_LOG_DIR";

    /// <summary>Port of <c>ALL_LOG_FORMATS</c>.</summary>
    public static readonly IReadOnlyList<LogFormat> All = [LogFormat.Eval, LogFormat.Json];

    /// <summary>The Python literal (<c>"eval"</c> / <c>"json"</c>).</summary>
    public static string Name(this LogFormat format) => format switch
    {
        LogFormat.Eval => "eval",
        LogFormat.Json => "json",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown log format."),
    };

    /// <summary>The file extension, including the dot.</summary>
    public static string Extension(this LogFormat format) => "." + format.Name();

    /// <summary>Parses a Python format literal; case-insensitive, as <c>INSPECT_LOG_FORMAT=EVAL</c> is accepted by the CLI.</summary>
    public static LogFormat Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Trim().ToLowerInvariant() switch
        {
            "eval" => LogFormat.Eval,
            "json" => LogFormat.Json,
            _ => throw new ArgumentException($"No recorder for format: {text}", nameof(text)),
        };
    }

    /// <summary>The format named by <c>INSPECT_LOG_FORMAT</c> (then <c>INSPECT_EVAL_LOG_FORMAT</c>), or null when neither is set; an unknown value is an <see cref="ArgumentException"/>.</summary>
    public static LogFormat? FromEnvironment()
    {
        foreach (var variable in FormatEnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return Parse(value);
            }
        }

        return null;
    }

    /// <summary>Port of <c>os.environ.get("INSPECT_LOG_DIR", "./logs")</c>: the log directory to use when none is given.</summary>
    public static string DefaultLogDir
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(LogDirEnvironmentVariable);
            return string.IsNullOrWhiteSpace(value) ? "logs" : value;
        }
    }

    /// <summary>Port of <c>recorder_type_for_location</c>: the format a location's extension selects.</summary>
    public static LogFormat ForLocation(string location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (location.EndsWith(".eval", StringComparison.Ordinal))
        {
            return LogFormat.Eval;
        }

        if (location.EndsWith(".json", StringComparison.Ordinal))
        {
            return LogFormat.Json;
        }

        throw new ArgumentException($"No recorder for location: {location}", nameof(location));
    }

    /// <summary>Port of <c>recorder_type_for_bytes</c>: a ZIP local header (<c>PK\x03\x04</c>) is <see cref="LogFormat.Eval"/>, a <c>{</c> is <see cref="LogFormat.Json"/>.</summary>
    public static LogFormat ForBytes(ReadOnlySpan<byte> firstBytes)
    {
        if (ZipLogReader.IsZip(firstBytes))
        {
            return LogFormat.Eval;
        }

        if (firstBytes.Length >= 1 && firstBytes[0] == (byte)'{')
        {
            return LogFormat.Json;
        }

        throw new ArgumentException($"No recorder for bytes: {Convert.ToHexString(firstBytes)}", nameof(firstBytes));
    }
}

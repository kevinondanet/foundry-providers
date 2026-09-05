using System.CommandLine;
using InspectAzureAI.Cli.Args;

namespace InspectAzureAI.Cli.Commands;

/// <summary>The resolved common options of a command.</summary>
internal sealed record CommonValues(string LogDir, string LogLevel, string Display);

/// <summary>
/// Port of <c>_cli/common.py</c> <c>common_options</c> / <c>process_common_options</c>: <c>--log-level</c>,
/// <c>--log-dir</c>, <c>--display</c>, <c>--no-ansi</c>, <c>--traceback-locals</c>, <c>--env</c>, <c>--debug</c>,
/// <c>--debug-port</c> and <c>--debug-errors</c>. <c>--env</c> sets process environment variables; the debugger options
/// have no .NET equivalent and are refused; the display type only selects between the plain reporter and none.
/// </summary>
internal sealed class CommonOptions
{
    public const string DefaultLogLevel = "warning";

    public const string DefaultDisplay = "full";

    public const string DefaultLogDir = "./logs";

    public static readonly string[] LogLevels = ["debug", "trace", "http", "sandbox", "info", "warning", "error", "critical", "notset"];

    public static readonly string[] Displays = ["full", "conversation", "rich", "plain", "log", "none"];

    public Option<string?> LogLevel { get; } = Opt.Choice("--log-level", $"Set the log level (defaults to '{DefaultLogLevel}')", LogLevels, "INSPECT_LOG_LEVEL", caseInsensitive: true);

    public Option<string?> LogDir { get; } = Opt.String("--log-dir", "Directory for log files.", "INSPECT_LOG_DIR");

    public Option<string?> Display { get; } = Opt.Choice("--display", $"Set the display type (defaults to '{DefaultDisplay}')", Displays, "INSPECT_DISPLAY", caseInsensitive: true);

    public Option<bool> NoAnsi { get; } = Opt.Flag("--no-ansi", "Do not print ANSI control characters.", "INSPECT_NO_ANSI", hidden: true);

    public Option<bool> TracebackLocals { get; } = Opt.Flag("--traceback-locals", "Include values of local variables in tracebacks (note that this can leak private data e.g. API keys so should typically only be enabled for targeted debugging).", "INSPECT_TRACEBACK_LOCALS");

    public Option<string[]> Env { get; } = Opt.Multi("--env", "Define an environment variable e.g. --env NAME=value (--env can be specified multiple times)", "INSPECT_EVAL_ENV");

    public Option<bool> Debug { get; } = Opt.Flag("--debug", "Wait to attach debugger (not supported by inspectai: attach a .NET debugger instead).", "INSPECT_DEBUG");

    public Option<int?> DebugPort { get; } = Opt.Int("--debug-port", "Port number for debugger (not supported by inspectai).", "INSPECT_DEBUG_PORT");

    public Option<bool> DebugErrors { get; } = Opt.Flag("--debug-errors", "Raise task errors (rather than logging them) so they can be debugged (not supported by inspectai).", "INSPECT_DEBUG_ERRORS");

    public IEnumerable<Option> All => [LogLevel, LogDir, Display, NoAnsi, TracebackLocals, Env, Debug, DebugPort, DebugErrors];

    public void AddTo(Command command)
    {
        foreach (var option in All)
        {
            command.Options.Add(option);
        }
    }

    /// <summary>Port of <c>process_common_options</c>: applies <c>--env</c>, refuses the debugger options, and returns the resolved values (the log dir stripped of trailing separators as <c>clean_log_dir</c> does).</summary>
    public CommonValues Process(ParseResult result)
    {
        foreach (var (name, value) in CliArgs.ParseCliArgs(result.GetValue(Env)))
        {
            Environment.SetEnvironmentVariable(name, value is null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
        }

        if (result.GetValue(TracebackLocals))
        {
            Environment.SetEnvironmentVariable("INSPECT_TRACEBACK_LOCALS", "1");
        }

        if (result.GetValue(Debug) || Opt.Specified(result, DebugPort))
        {
            throw new UsageError("--debug / --debug-port are not supported by inspectai (they attach Python's debugpy); attach a .NET debugger to the process instead.");
        }

        if (result.GetValue(DebugErrors))
        {
            throw new UsageError("--debug-errors is not supported by inspectai: sample errors are always recorded in the log.");
        }

        var logDir = (result.GetValue(LogDir) ?? DefaultLogDir).TrimEnd('/', '\\');
        var display = result.GetValue(NoAnsi) ? "rich" : (result.GetValue(Display) ?? DefaultDisplay).Trim().ToLowerInvariant();
        return new CommonValues(logDir.Length == 0 ? DefaultLogDir : logDir, (result.GetValue(LogLevel) ?? DefaultLogLevel).ToLowerInvariant(), display);
    }
}

using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;

namespace InspectAzureAI.Eval.Analysis;

/// <summary>
/// Port of the <c>logs</c> argument of the Python table functions (<c>LogPaths | EvalLog | Sequence[EvalLog]</c>):
/// log file paths, log directories, <see cref="EvalLogInfo"/> listings, or in-memory <see cref="EvalLog"/> objects.
/// Converts implicitly from a path, an array or list of paths, a log, an array or list of logs, and log infos.
/// Passing null to a table reader means the active log directory (<c>INSPECT_LOG_DIR</c>, else <c>logs</c>).
/// </summary>
public sealed class LogSource
{
    private readonly IReadOnlyList<string>? _paths;
    private readonly IReadOnlyList<EvalLog>? _logs;

    private LogSource(IReadOnlyList<string>? paths, IReadOnlyList<EvalLog>? logs)
    {
        _paths = paths;
        _logs = logs;
    }

    /// <summary>The in-memory logs, when the source is logs rather than paths.</summary>
    public IReadOnlyList<EvalLog>? Logs => _logs;

    /// <summary>The paths (files or directories), when the source is paths rather than logs.</summary>
    public IReadOnlyList<string>? Paths => _paths;

    public static LogSource FromPaths(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new LogSource(paths.ToList(), null);
    }

    public static LogSource FromLogInfos(IEnumerable<EvalLogInfo> infos)
    {
        ArgumentNullException.ThrowIfNull(infos);
        return new LogSource(infos.Select(info => info.Name).ToList(), null);
    }

    public static LogSource FromLogs(IEnumerable<EvalLog> logs)
    {
        ArgumentNullException.ThrowIfNull(logs);
        return new LogSource(null, logs.ToList());
    }

    public static implicit operator LogSource(string path) => FromPaths([path]);

    public static implicit operator LogSource(string[] paths) => FromPaths(paths);

    public static implicit operator LogSource(List<string> paths) => FromPaths(paths);

    public static implicit operator LogSource(EvalLog log) => FromLogs([log]);

    public static implicit operator LogSource(EvalLog[] logs) => FromLogs(logs);

    public static implicit operator LogSource(List<EvalLog> logs) => FromLogs(logs);

    public static implicit operator LogSource(EvalLogInfo info) => FromLogInfos([info]);

    public static implicit operator LogSource(EvalLogInfo[] infos) => FromLogInfos(infos);

    /// <summary>
    /// Port of <c>resolve_logs</c>: in-memory logs pass through; null lists the active log directory; directories
    /// are expanded recursively and, like single files, filtered to log files (<c>.eval</c>, or a timestamp-named
    /// <c>.json</c>) in listing order. A path that does not exist is a <see cref="FileNotFoundException"/>.
    /// </summary>
    internal static ResolvedLogs Resolve(LogSource? source)
    {
        if (source?._logs is { } logs)
        {
            return new ResolvedLogs(null, logs);
        }

        var paths = source?._paths ?? EvalLogFiles.ListEvalLogs().Select(info => info.Name).ToList();
        var entries = new List<FileEntry>();
        foreach (var path in paths)
        {
            var local = path.StartsWith("file://", StringComparison.Ordinal) ? new Uri(path).LocalPath : path;
            if (Directory.Exists(local))
            {
                foreach (var file in Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    entries.Add(Entry(file));
                }
            }
            else if (File.Exists(local))
            {
                entries.Add(Entry(local));
            }
            else
            {
                throw new FileNotFoundException($"Log path not found: {path}", path);
            }
        }

        return new ResolvedLogs(EvalLogFiles.LogFilesFromLs(entries, sort: false).Select(info => info.Name).ToList(), null);
    }

    private static FileEntry Entry(string file)
    {
        var info = new FileInfo(file);
        return new FileEntry(Path.GetFullPath(file), "file", info.Length, (info.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds);
    }
}

/// <summary>The outcome of <see cref="LogSource.Resolve"/>: either log file paths or in-memory logs.</summary>
internal sealed record ResolvedLogs(IReadOnlyList<string>? Paths, IReadOnlyList<EvalLog>? Logs)
{
    public int Count => Paths?.Count ?? Logs?.Count ?? 0;

    public bool IsLogs => Logs is not null;
}

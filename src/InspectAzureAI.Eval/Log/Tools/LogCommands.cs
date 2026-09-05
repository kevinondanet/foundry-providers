using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Log.Tools;

/// <summary>
/// Port of the non-interactive parts of <c>_cli/log.py</c>: the <c>list</c>, <c>dump</c>, <c>headers</c>,
/// <c>schema</c> and <c>recover --list --json</c> commands as functions returning the text they print (the caller
/// prints it). <c>convert</c> is <see cref="LogConversion"/>, <c>recover</c> is <see cref="EvalLogRecovery"/>.
/// </summary>
public static class LogCommands
{
    /// <summary>
    /// Port of <c>log_list</c>: the logs under <paramref name="logDir"/> (<c>INSPECT_LOG_DIR</c> or <c>logs</c> when
    /// null), optionally only those with <paramref name="status"/>, names relative to the current directory unless
    /// <paramref name="absolute"/>.
    /// </summary>
    public static IReadOnlyList<EvalLogInfo> ListLogs(string? logDir = null, EvalStatus? status = null, bool absolute = false, bool recursive = true)
    {
        var logs = EvalLogFiles.ListEvalLogs(logDir, filter: status is { } wanted ? log => log.Status == wanted : null, recursive: recursive);
        if (absolute)
        {
            return logs;
        }

        var current = Directory.GetCurrentDirectory();
        return logs.Select(log => log with { Name = Path.GetRelativePath(current, log.Name) }).ToList();
    }

    /// <summary>Port of <c>log list</c> without <c>--json</c>: one log name per line.</summary>
    public static string ListLogsText(string? logDir = null, EvalStatus? status = null, bool absolute = false, bool recursive = true) =>
        string.Join("\n", ListLogs(logDir, status, absolute, recursive).Select(log => log.Name));

    /// <summary>
    /// Port of <c>log list --json</c>: the listing as Python's <c>json.dumps([info.model_dump() ...], indent=2)</c>
    /// (<c>name</c>, <c>type</c>, <c>size</c>, <c>mtime</c>, <c>task</c>, <c>task_id</c>, <c>suffix</c>; nulls kept,
    /// non-ASCII escaped). <c>mtime</c> is in milliseconds as Python reports it (<see cref="EvalLogInfo.Mtime"/> holds seconds).
    /// </summary>
    public static string ListLogsJson(string? logDir = null, EvalStatus? status = null, bool absolute = false, bool recursive = true)
    {
        var array = new JsonArray();
        foreach (var log in ListLogs(logDir, status, absolute, recursive))
        {
            array.Add(new JsonObject
            {
                ["name"] = log.Name,
                ["type"] = log.Type,
                ["size"] = log.Size,
                ["mtime"] = log.Mtime is { } mtime ? JsonValue.Create(mtime * 1000) : null,
                ["task"] = log.Task,
                ["task_id"] = log.TaskId,
                ["suffix"] = log.Suffix,
            });
        }

        return PythonJson.Dumps(array, indent: 2);
    }

    /// <summary>Port of <c>dump</c>: the log (or, with <paramref name="headerOnly"/>, everything but its samples) as Python-format JSON.</summary>
    public static string Dump(string path, bool headerOnly = false, ResolveAttachments resolveAttachments = ResolveAttachments.None)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return EvalLogFiles.EvalLogJson(EvalLogFiles.ReadEvalLog(path, headerOnly, resolveAttachments));
    }

    /// <summary>
    /// Port of <c>headers</c>: the headers of <paramref name="files"/> as a JSON array in Python's
    /// <c>json.dumps(to_jsonable_python(headers, exclude_none=True), indent=2)</c> form — the log JSON with
    /// non-ASCII text escaped.
    /// </summary>
    public static string HeadersJson(IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var headers = EvalLogFiles.ReadEvalLogHeaders(files);
        if (headers.Count == 0)
        {
            return "[]";
        }

        var items = headers.Select(header => Indent(EnsureAscii(EvalLogFiles.EvalLogJson(header)), 2));
        return "[\n" + string.Join(",\n", items) + "\n]";
    }

    /// <summary>Port of <c>schema</c>: the log file JSON schema (<see cref="ViewerAssets.ResolveSchemaPath"/>).</summary>
    public static string SchemaJson(string? schemaPath = null) => File.ReadAllText(ViewerAssets.ResolveSchemaPath(schemaPath), Encoding.UTF8);

    /// <summary>Port of <c>recover --list --json</c>: the recoverable logs with their sample counts and source.</summary>
    public static async Task<string> RecoverableLogsJsonAsync(string? logDir = null, CancellationToken cancellationToken = default)
    {
        var array = new JsonArray();
        foreach (var recoverable in await EvalLogRecovery.RecoverableEvalLogsAsync(logDir, cancellationToken).ConfigureAwait(false))
        {
            array.Add(new JsonObject
            {
                ["name"] = recoverable.Log.Name,
                ["task"] = recoverable.Log.Task,
                ["total_samples"] = recoverable.TotalSamples,
                ["flushed_samples"] = recoverable.FlushedSamples,
                ["completed_samples"] = recoverable.CompletedSamples,
                ["in_progress_samples"] = recoverable.InProgressSamples,
                ["source"] = recoverable.Source,
            });
        }

        return PythonJson.Dumps(array, indent: 2);
    }

    /// <summary>
    /// Python's <c>ensure_ascii</c> over JSON text: every character outside <c>0x20..0x7e</c> inside a string literal
    /// becomes a lower-case <c>\uXXXX</c> escape (astral characters as their surrogate pair); an escape already in the
    /// text (System.Text.Json writes surrogate pairs as upper-case <c>\uD83D\uDCA1</c>) is lower-cased too.
    /// </summary>
    internal static string EnsureAscii(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var sb = new StringBuilder(json.Length);
        var inString = false;
        for (var i = 0; i < json.Length; i++)
        {
            var ch = json[i];
            if (!inString)
            {
                inString = ch == '"';
                sb.Append(ch);
                continue;
            }

            switch (ch)
            {
                case '\\' when i + 5 < json.Length && json[i + 1] == 'u' && Uri.IsHexDigit(json[i + 2]) && Uri.IsHexDigit(json[i + 3]) && Uri.IsHexDigit(json[i + 4]) && Uri.IsHexDigit(json[i + 5]):
                    sb.Append("\\u").Append(json.AsSpan(i + 2, 4).ToString().ToLowerInvariant());
                    i += 5;
                    break;
                case '\\' when i + 1 < json.Length:
                    sb.Append(ch).Append(json[++i]);
                    break;
                case '"':
                    inString = false;
                    sb.Append(ch);
                    break;
                default:
                    if (ch < 0x20 || ch > 0x7e)
                    {
                        sb.Append("\\u").Append(((int)ch).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    private static string Indent(string text, int spaces)
    {
        var prefix = new string(' ', spaces);
        return string.Join("\n", text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => prefix + line));
    }
}

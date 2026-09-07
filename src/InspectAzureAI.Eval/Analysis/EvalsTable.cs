using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;

namespace InspectAzureAI.Eval.Analysis;

/// <summary>
/// Port of <c>analysis/_dataframe/evals/table.py</c> <c>evals_df</c>: one row per eval log (header reads only),
/// with <c>eval_id</c> and <c>log</c> always included and duplicate eval ids dropped.
/// </summary>
public static class EvalsTable
{
    /// <summary>The <c>eval_id</c> column, always present and first.</summary>
    public const string EvalIdColumn = "eval_id";

    /// <summary>The <c>log</c> column: the path of the log file each row was read from.</summary>
    public const string LogColumn = "log";

    /// <summary>Suffix given to eval columns that collide with sample columns on merge.</summary>
    internal const string EvalSuffix = "_eval";

    /// <summary>
    /// Reads the evals table. <paramref name="logs"/> is one or more log files, directories or logs (null for the
    /// active log directory); <paramref name="columns"/> defaults to <see cref="EvalColumns.Default"/>. The first
    /// column error is a <see cref="ColumnImportException"/> (Python's <c>strict=True</c>).
    /// </summary>
    public static Table Read(LogSource? logs = null, IReadOnlyList<Column>? columns = null, CancellationToken cancellationToken = default) =>
        ReadEvals(LogSource.Resolve(logs), columns ?? EvalColumns.Default, strict: true, cancellationToken).Table;

    /// <summary>Reads the evals table, collecting column errors instead of raising them (Python's <c>strict=False</c>).</summary>
    public static TableImport ReadWithErrors(LogSource? logs = null, IReadOnlyList<Column>? columns = null, CancellationToken cancellationToken = default)
    {
        var import = ReadEvals(LogSource.Resolve(logs), columns ?? EvalColumns.Default, strict: false, cancellationToken);
        return new TableImport(import.Table, import.Errors);
    }

    public static Task<Table> ReadAsync(LogSource? logs = null, IReadOnlyList<Column>? columns = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(logs, columns, cancellationToken), cancellationToken);

    public static Task<TableImport> ReadWithErrorsAsync(LogSource? logs = null, IReadOnlyList<Column>? columns = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadWithErrors(logs, columns, cancellationToken), cancellationToken);

    /// <summary>Port of <c>_read_evals_df</c>: the table, the logs it was read from (headers) and the total sample count.</summary>
    internal static EvalsImport ReadEvals(ResolvedLogs logs, IReadOnlyList<Column> columns, bool strict, CancellationToken cancellationToken)
    {
        var resolved = EnsureEvalData(RecordImporter.ResolveDuplicateColumns(columns));
        var errors = new List<ColumnError>();
        var evalIds = new HashSet<string>(StringComparer.Ordinal);
        var evalLogs = new List<EvalLog>();
        var records = new List<IReadOnlyDictionary<string, object?>>();
        var totalSamples = 0;

        foreach (var log in ReadHeaders(logs, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = RecordImporter.Import(log, ImportTarget.ForLog(log), resolved, strict, errors);
            var evalId = record.TryGetValue(EvalIdColumn, out var id) && id is not null ? Table.FormatCell(id) : "";
            if (evalIds.Add(evalId))
            {
                evalLogs.Add(log);
                records.Add(record);
                totalSamples += log.Eval.Dataset.SampleIds?.Count ?? log.Eval.Dataset.Samples ?? 100;
            }
        }

        var table = ReorderColumns(Table.FromRecords(records), resolved);
        return new EvalsImport(table, evalLogs, totalSamples, errors);
    }

    /// <summary>Port of <c>ensure_eval_data</c>: <c>eval_id</c> and <c>log</c> appended when the columns lack them.</summary>
    internal static List<Column> EnsureEvalData(IEnumerable<Column> columns)
    {
        var result = columns.ToList();
        if (!result.Any(column => column.Name == EvalIdColumn))
        {
            result.AddRange(EvalColumns.Id);
        }

        if (!result.Any(column => column.Name == LogColumn))
        {
            result.AddRange(EvalColumns.LogPath);
        }

        return result;
    }

    /// <summary>Port of <c>reorder_evals_df_columns</c>: <c>eval_id</c>, then the columns in specification order, then the rest sorted.</summary>
    internal static Table ReorderColumns(Table table, IReadOnlyList<Column> columns)
    {
        var actual = table.Columns;
        var ordered = new List<string>();
        if (actual.Contains(EvalIdColumn))
        {
            ordered.Add(EvalIdColumn);
        }

        foreach (var column in columns)
        {
            if (column.Name == EvalIdColumn)
            {
                continue;
            }

            ordered.AddRange(RecordImporter.ResolveColumns(column.Name, EvalSuffix, actual, ordered));
        }

        return table.Select(RecordImporter.AddUnreferencedColumns(actual, ordered));
    }

    private static IEnumerable<EvalLog> ReadHeaders(ResolvedLogs logs, CancellationToken cancellationToken)
    {
        if (logs.Logs is { } inMemory)
        {
            return inMemory;
        }

        return logs.Paths!.Select(path =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return EvalLogFiles.ReadEvalLog(path, headerOnly: true);
        });
    }
}

/// <summary>The result of <see cref="EvalsTable.ReadEvals"/>.</summary>
internal sealed record EvalsImport(Table Table, IReadOnlyList<EvalLog> Logs, int TotalSamples, List<ColumnError> Errors);

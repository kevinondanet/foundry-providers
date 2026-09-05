using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Analysis;

/// <summary>
/// Port of <c>analysis/_dataframe/samples/table.py</c> <c>samples_df</c>: one row per sample across a set of
/// logs, joined with the evals table on <c>eval_id</c>. <c>sample_id</c> (the sample's uuid, or a stable id derived
/// from the eval id, sample id and epoch) and <c>eval_id</c> are always included. Summary columns read the sample
/// summaries; a <see cref="SampleColumn.Full"/> column makes every log load in full.
/// </summary>
public static class SamplesTable
{
    /// <summary>The <c>sample_id</c> column, always present.</summary>
    public const string SampleIdColumn = "sample_id";

    /// <summary>Suffix given to sample columns that collide with eval or detail columns on merge.</summary>
    internal const string SampleSuffix = "_sample";

    /// <summary>
    /// Reads the samples table. <paramref name="columns"/> defaults to <see cref="SampleColumns.Summary"/>;
    /// <paramref name="full"/> reads the unabbreviated sample <c>metadata</c> (slower: full samples are loaded);
    /// <paramref name="excludeFields"/> skips sample fields when loading <c>.eval</c> logs. The first column error is
    /// a <see cref="ColumnImportException"/>.
    /// </summary>
    public static Table Read(LogSource? logs = null, IReadOnlyList<Column>? columns = null, bool full = false, ISet<string>? excludeFields = null, CancellationToken cancellationToken = default) =>
        ReadSamples(LogSource.Resolve(logs), columns ?? SampleColumns.Summary, full, strict: true, detail: null, excludeFields, cancellationToken).Table;

    /// <summary>Reads the samples table, collecting column errors instead of raising them.</summary>
    public static TableImport ReadWithErrors(LogSource? logs = null, IReadOnlyList<Column>? columns = null, bool full = false, ISet<string>? excludeFields = null, CancellationToken cancellationToken = default) =>
        ReadSamples(LogSource.Resolve(logs), columns ?? SampleColumns.Summary, full, strict: false, detail: null, excludeFields, cancellationToken);

    public static Task<Table> ReadAsync(LogSource? logs = null, IReadOnlyList<Column>? columns = null, bool full = false, ISet<string>? excludeFields = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(logs, columns, full, excludeFields, cancellationToken), cancellationToken);

    public static Task<TableImport> ReadWithErrorsAsync(LogSource? logs = null, IReadOnlyList<Column>? columns = null, bool full = false, ISet<string>? excludeFields = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadWithErrors(logs, columns, full, excludeFields, cancellationToken), cancellationToken);

    /// <summary>
    /// Port of <c>_read_samples_df_serial</c> (the messages and events tables pass a <paramref name="detail"/> that
    /// blows each sample out into its messages or events). The evals part is always read strictly, as in Python.
    /// </summary>
    internal static TableImport ReadSamples(ResolvedLogs logs, IReadOnlyList<Column> columns, bool full, bool strict, SampleDetail? detail, ISet<string>? excludeFields, CancellationToken cancellationToken)
    {
        var evalColumns = new List<Column>();
        var sampleColumns = new List<Column>();
        var detailColumns = new List<Column>();
        foreach (var column in columns)
        {
            switch (column)
            {
                case EvalColumn:
                    evalColumns.Add(column);
                    break;
                case SampleColumn sampleColumn:
                    sampleColumns.Add(full && !sampleColumn.Full && sampleColumn.Name == "metadata_*" ? sampleColumn.AsFull() : sampleColumn);
                    break;
                case var _ when detail is not null && detail.IsDetailColumn(column):
                    detailColumns.Add(column);
                    break;
                default:
                    throw new ArgumentException($"Unexpected column type passed to samples_df: {column.GetType().Name}", nameof(columns));
            }
        }

        evalColumns = EvalsTable.EnsureEvalData(RecordImporter.ResolveDuplicateColumns(evalColumns));
        sampleColumns = RecordImporter.ResolveDuplicateColumns(sampleColumns);
        detailColumns = RecordImporter.ResolveDuplicateColumns(detailColumns);
        var requireFullSamples = detail is not null || detailColumns.Count > 0 || sampleColumns.Any(column => column is SampleColumn { Full: true });

        var evals = EvalsTable.ReadEvals(logs, evalColumns, strict: true, cancellationToken);
        var evalIds = evals.Table.HasColumn(EvalsTable.EvalIdColumn) ? evals.Table.Column(EvalsTable.EvalIdColumn) : [];
        var errors = new List<ColumnError>();
        var sampleRecords = new List<IReadOnlyDictionary<string, object?>>();
        var detailRecords = new List<IReadOnlyDictionary<string, object?>>();

        for (var i = 0; i < evals.Logs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evalLog = evals.Logs[i];
            var evalId = Table.FormatCell(evalIds[i]);
            foreach (var (sample, target) in ReadLogSamples(logs.IsLogs, evalLog, requireFullSamples, excludeFields))
            {
                var record = RecordImporter.Import(evalLog, target, sampleColumns, strict, errors);
                var sampleId = sample.Uuid ?? SampleColumns.AutoSampleId(evalId, sample.Id, sample.Epoch);
                sampleRecords.Add(WithIds(record, (EvalsTable.EvalIdColumn, evalId), (SampleIdColumn, sampleId)));

                if (detail is null)
                {
                    continue;
                }

                var fullSample = target.Sample ?? throw new InvalidOperationException("Messages and events can only be read from full samples.");
                var items = detail.Items(fullSample);
                for (var index = 0; index < items.Count; index++)
                {
                    var detailRecord = RecordImporter.Import(evalLog, detail.Target(items[index]), detailColumns, strict, errors);
                    var idField = detail.IdColumn;
                    if (!detailRecord.TryGetValue(idField, out var detailId) || detailId is null)
                    {
                        detailRecord[idField] = SampleColumns.AutoDetailId(sampleId, detail.Name, index);
                    }

                    var withIds = WithIds(detailRecord, (SampleIdColumn, sampleId));
                    withIds["order"] = (long)(index + 1);
                    detailRecords.Add(withIds);
                }
            }
        }

        var samplesTable = Table.FromRecords(sampleRecords);
        if (samplesTable.RowCount > 0)
        {
            samplesTable = samplesTable.DistinctBy(SampleIdColumn);
        }

        if (detail is not null)
        {
            var detailsTable = Table.FromRecords(detailRecords);
            if (detailsTable.RowCount > 0)
            {
                samplesTable = detailsTable.DistinctBy(detail.IdColumn).LeftMerge(samplesTable, SampleIdColumn, "_" + detail.Name, SampleSuffix);
            }
        }

        if (samplesTable.RowCount > 0)
        {
            samplesTable = samplesTable.LeftMerge(evals.Table, EvalsTable.EvalIdColumn, SampleSuffix, EvalsTable.EvalSuffix);
            samplesTable = ReorderColumns(samplesTable, evalColumns, sampleColumns, detailColumns, detail?.Name ?? "");
        }

        return new TableImport(samplesTable, errors);
    }

    /// <summary>
    /// Port of <c>sample_messages_from_events</c>: every message seen in the input or output of the sample's model
    /// events, once (by id, or by a hash of its text when it has none), with consecutive duplicate assistant messages
    /// (same text and tool functions, as the agent bridge produces) collapsed, then filtered.
    /// </summary>
    internal static IReadOnlyList<ChatMessage> SampleMessagesFromEvents(IReadOnlyList<TranscriptEvent> events, Func<ChatMessage, bool>? filter)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var messages = new List<ChatMessage>();
        foreach (var @event in events.OfType<ModelEvent>())
        {
            var eventMessages = @event.Output.Empty ? @event.Input : [.. @event.Input, @event.Output.Message];
            foreach (var message in eventMessages)
            {
                var id = message.Id ?? MurmurHash3.Hash(Extract.MessageAsStr(message));
                if (ids.Add(id))
                {
                    messages.Add(message);
                }
            }
        }

        var reduced = new List<ChatMessage>();
        foreach (var message in messages)
        {
            if (message.Role == "assistant" && reduced.Count > 0 && reduced[^1].Role == "assistant" && message.Text == reduced[^1].Text
                && ToolFunctions(message).SequenceEqual(ToolFunctions(reduced[^1]), StringComparer.Ordinal))
            {
                continue;
            }

            reduced.Add(message);
        }

        return filter is null ? reduced : reduced.Where(filter).ToList();
    }

    private static IEnumerable<string> ToolFunctions(ChatMessage message) =>
        (message as ChatMessageAssistant)?.ToolCalls?.Select(call => call.Function) ?? [];

    /// <summary>Port of <c>read_samples_async</c>: in-memory samples, else full samples or summaries from the log's location.</summary>
    private static IEnumerable<(SampleIdentity Sample, ImportTarget Target)> ReadLogSamples(bool inMemory, EvalLog evalLog, bool requireFull, ISet<string>? excludeFields)
    {
        if (inMemory && evalLog.Samples is { Count: > 0 } samples)
        {
            return samples.Select(sample => (new SampleIdentity(sample.Id, sample.Epoch, sample.Uuid), ImportTarget.ForSample(sample)));
        }

        var location = evalLog.Location ?? throw new InvalidOperationException($"The log for eval '{evalLog.Eval.EvalId}' has no samples and no location to read them from.");
        if (requireFull)
        {
            var fullLog = EvalLogFiles.ReadEvalLog(location, resolveAttachments: ResolveAttachments.Full, excludeFields: excludeFields);
            return (fullLog.Samples ?? []).Select(sample => (new SampleIdentity(sample.Id, sample.Epoch, sample.Uuid), ImportTarget.ForSample(sample)));
        }

        return EvalLogFiles.ReadEvalLogSampleSummaries(location).Select(summary => (new SampleIdentity(summary.Id, summary.Epoch, summary.Uuid), ImportTarget.ForSummary(summary)));
    }

    /// <summary>Python's <c>ids | record</c>: the id fields first, a record field of the same name keeping the record's value.</summary>
    private static Dictionary<string, object?> WithIds(Dictionary<string, object?> record, params (string Name, object? Value)[] ids)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in ids)
        {
            result[name] = value;
        }

        foreach (var (name, value) in record)
        {
            result[name] = value;
        }

        return result;
    }

    /// <summary>Port of <c>reorder_samples_df_columns</c>: detail id, <c>sample_id</c>, <c>eval_id</c>, eval columns, sample columns, detail columns, then the rest sorted.</summary>
    private static Table ReorderColumns(Table table, IReadOnlyList<Column> evalColumns, IReadOnlyList<Column> sampleColumns, IReadOnlyList<Column> detailColumns, string detailName)
    {
        var actual = table.Columns;
        var ordered = new List<string>();
        if (detailName.Length > 0 && actual.Contains(detailName + "_id"))
        {
            ordered.Add(detailName + "_id");
        }

        if (actual.Contains(SampleIdColumn))
        {
            ordered.Add(SampleIdColumn);
        }

        if (actual.Contains(EvalsTable.EvalIdColumn))
        {
            ordered.Add(EvalsTable.EvalIdColumn);
        }

        AddResolved(evalColumns, EvalsTable.EvalSuffix);
        AddResolved(sampleColumns, SampleSuffix);
        AddResolved(detailColumns, "_" + detailName);
        return table.Select(RecordImporter.AddUnreferencedColumns(actual, ordered));

        void AddResolved(IReadOnlyList<Column> columns, string suffix)
        {
            foreach (var column in columns)
            {
                if (column.Name is EvalsTable.EvalIdColumn or SampleIdColumn)
                {
                    continue;
                }

                ordered.AddRange(RecordImporter.ResolveColumns(column.Name, suffix, actual, ordered));
            }
        }
    }

    private sealed record SampleIdentity(object Id, int Epoch, string? Uuid);
}

/// <summary>Port of <c>MessagesDetail</c> / <c>EventsDetail</c>: how the messages and events tables blow a sample out into rows.</summary>
internal abstract class SampleDetail
{
    protected SampleDetail(string name)
    {
        Name = name;
    }

    /// <summary>The entity name (<c>message</c> or <c>event</c>): the id column is <c>{name}_id</c> and colliding columns get the <c>_{name}</c> suffix.</summary>
    public string Name { get; }

    public string IdColumn => Name + "_id";

    public abstract bool IsDetailColumn(Column column);

    public abstract IReadOnlyList<object> Items(EvalSample sample);

    public abstract ImportTarget Target(object item);
}

internal sealed class MessagesDetail(Func<ChatMessage, bool>? filter) : SampleDetail("message")
{
    public override bool IsDetailColumn(Column column) => column is MessageColumn;

    public override IReadOnlyList<object> Items(EvalSample sample) => SamplesTable.SampleMessagesFromEvents(sample.Events, filter);

    public override ImportTarget Target(object item) => ImportTarget.ForMessage((ChatMessage)item);
}

internal sealed class EventsDetail(Func<TranscriptEvent, bool>? filter) : SampleDetail("event")
{
    public override bool IsDetailColumn(Column column) => column is EventColumn;

    public override IReadOnlyList<object> Items(EvalSample sample) => (filter is null ? sample.Events : sample.Events.Where(filter)).ToList();

    public override ImportTarget Target(object item) => ImportTarget.ForEvent((TranscriptEvent)item);
}

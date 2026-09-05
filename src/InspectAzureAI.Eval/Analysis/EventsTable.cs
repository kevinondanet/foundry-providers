using InspectAzureAI.Eval.Context;

namespace InspectAzureAI.Eval.Analysis;

/// <summary>
/// Port of <c>analysis/_dataframe/events/table.py</c> <c>events_df</c>: one row per transcript event across a set
/// of logs, with <c>event_id</c>, <c>sample_id</c>, <c>eval_id</c> and <c>order</c> always included and eval / sample
/// columns joined in. Events are heterogeneous, so the default columns are only <see cref="EventColumns.Info"/>;
/// combine <see cref="EventColumns.ModelEvent"/> or <see cref="EventColumns.ToolEvent"/> with a matching filter.
/// </summary>
public static class EventsTable
{
    /// <summary>Reads the events table; <paramref name="columns"/> defaults to <see cref="EventColumns.Info"/>, <paramref name="filter"/> keeps only matching events.</summary>
    public static Table Read(LogSource? logs = null, IReadOnlyList<Column>? columns = null, Func<TranscriptEvent, bool>? filter = null, CancellationToken cancellationToken = default) =>
        SamplesTable.ReadSamples(LogSource.Resolve(logs), columns ?? EventColumns.Info, full: false, strict: true, new EventsDetail(filter), excludeFields: null, cancellationToken).Table;

    /// <summary>Reads the events table, collecting column errors instead of raising them.</summary>
    public static TableImport ReadWithErrors(LogSource? logs = null, IReadOnlyList<Column>? columns = null, Func<TranscriptEvent, bool>? filter = null, CancellationToken cancellationToken = default) =>
        SamplesTable.ReadSamples(LogSource.Resolve(logs), columns ?? EventColumns.Info, full: false, strict: false, new EventsDetail(filter), excludeFields: null, cancellationToken);

    public static Task<Table> ReadAsync(LogSource? logs = null, IReadOnlyList<Column>? columns = null, Func<TranscriptEvent, bool>? filter = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(logs, columns, filter, cancellationToken), cancellationToken);

    public static Task<TableImport> ReadWithErrorsAsync(LogSource? logs = null, IReadOnlyList<Column>? columns = null, Func<TranscriptEvent, bool>? filter = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadWithErrors(logs, columns, filter, cancellationToken), cancellationToken);
}

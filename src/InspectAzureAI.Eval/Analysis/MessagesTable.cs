using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Analysis;

/// <summary>
/// Port of <c>analysis/_dataframe/messages/table.py</c> <c>messages_df</c>: one row per message across a set of
/// logs (the messages seen by the sample's model events, deduplicated), with <c>message_id</c>, <c>sample_id</c>,
/// <c>eval_id</c> and <c>order</c> always included and eval / sample columns joined in.
/// </summary>
public static class MessagesTable
{
    /// <summary>Reads the messages table; <paramref name="columns"/> defaults to <see cref="MessageColumns.Default"/>, <paramref name="filter"/> keeps only matching messages.</summary>
    public static Table Read(LogSource? logs = null, IReadOnlyList<Column>? columns = null, Func<ChatMessage, bool>? filter = null, CancellationToken cancellationToken = default) =>
        SamplesTable.ReadSamples(LogSource.Resolve(logs), columns ?? MessageColumns.Default, full: false, strict: true, new MessagesDetail(filter), excludeFields: null, cancellationToken).Table;

    /// <summary>Reads the messages table, collecting column errors instead of raising them.</summary>
    public static TableImport ReadWithErrors(LogSource? logs = null, IReadOnlyList<Column>? columns = null, Func<ChatMessage, bool>? filter = null, CancellationToken cancellationToken = default) =>
        SamplesTable.ReadSamples(LogSource.Resolve(logs), columns ?? MessageColumns.Default, full: false, strict: false, new MessagesDetail(filter), excludeFields: null, cancellationToken);

    public static Task<Table> ReadAsync(LogSource? logs = null, IReadOnlyList<Column>? columns = null, Func<ChatMessage, bool>? filter = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(logs, columns, filter, cancellationToken), cancellationToken);

    public static Task<TableImport> ReadWithErrorsAsync(LogSource? logs = null, IReadOnlyList<Column>? columns = null, Func<ChatMessage, bool>? filter = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadWithErrors(logs, columns, filter, cancellationToken), cancellationToken);
}

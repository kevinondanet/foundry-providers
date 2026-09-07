namespace InspectAzureAI.Eval.Context;

/// <summary>
/// Port of <c>util/_subtask.py</c> <c>subtask()</c>: runs a function with its own <see cref="Store"/> (a fresh one
/// unless given) as the ambient store, inside a span of type <c>subtask</c>, recording a <see cref="SubtaskEvent"/>
/// that is added as pending when the subtask starts and updated in place with the result, completion time and
/// working time when it finishes. The event, the span and every event the subtask records nest under the current
/// span; the subtask store's changes are reported by the span's <see cref="StoreEvent"/>. On failure or cancellation
/// the span still ends and the ambient store and context are restored, while the event stays pending as in Python.
/// </summary>
public static class Subtask
{
    /// <summary>
    /// Runs <paramref name="subtask"/> as a subtask named <paramref name="name"/>. <paramref name="input"/> is what
    /// the event logs as the subtask's input (Python derives it from the call arguments); <paramref name="type"/> is
    /// the event's optional type (<c>"fork"</c> for forks). Without an ambient <see cref="SampleContext"/> there is
    /// no ambient store to swap and no transcript, so the function simply runs.
    /// </summary>
    public static async Task<TResult> RunAsync<TResult>(
        string name,
        Func<CancellationToken, Task<TResult>> subtask,
        IReadOnlyDictionary<string, object?>? input = null,
        Store? store = null,
        string? type = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(subtask);
        var parent = SampleContext.Current;
        if (parent is null)
        {
            return await subtask(cancellationToken).ConfigureAwait(false);
        }

        using var contextScope = SampleContext.Begin(parent.WithStore(store ?? new Store()));
        var transcript = parent.Transcript;
        using var span = transcript.Span(name, "subtask");
        var waitingStart = parent.Limits.WaitingTime;
        var pending = transcript.Record(new SubtaskEvent(name, Jsonable.FromDictionary(input ?? new Dictionary<string, object?>(StringComparer.Ordinal))) { Type = type, Pending = true });

        var result = await subtask(cancellationToken).ConfigureAwait(false);

        var completed = DateTimeOffset.UtcNow;
        var waitingEnd = parent.Limits.WaitingTime;
        transcript.Update(pending with
        {
            Result = Jsonable.FromValue(result),
            Completed = completed,
            WorkingTime = (completed - pending.Timestamp).TotalSeconds - (waitingEnd - waitingStart).TotalSeconds,
            Pending = null,
        });
        return result;
    }

    /// <summary>A subtask without a result (the event's <c>result</c> is null).</summary>
    public static async Task RunAsync(
        string name,
        Func<CancellationToken, Task> subtask,
        IReadOnlyDictionary<string, object?>? input = null,
        Store? store = null,
        string? type = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subtask);
        await RunAsync<object?>(
            name,
            async ct =>
            {
                await subtask(ct).ConfigureAwait(false);
                return null;
            },
            input,
            store,
            type,
            cancellationToken).ConfigureAwait(false);
    }
}

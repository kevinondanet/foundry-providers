using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Context;

/// <summary>
/// Port of <c>util/_store.py</c> <c>store_changes</c> and <c>log/_transcript.py</c> <c>track_store_changes</c>:
/// the RFC 6902 changes between two store snapshots, and a scope that records them as a <see cref="StoreEvent"/>.
/// <see cref="Transcript.Span"/> opens one such scope around every span, which is how store changes reach the log.
/// </summary>
public static class StoreChanges
{
    /// <summary>Port of <c>store_changes(before, after)</c>: the changes, or null when the stores are equal.</summary>
    public static IReadOnlyList<JsonChange>? Between(Store before, Store after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return JsonChanges.Diff(Jsonable.FromStore(before), Jsonable.FromStore(after));
    }

    /// <summary>Port of <c>store_changes(before, after)</c> over snapshots taken with <see cref="Jsonable.FromStore"/>.</summary>
    public static IReadOnlyList<JsonChange>? Between(JsonObject before, JsonObject after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return JsonChanges.Diff(before, after);
    }

    /// <summary>
    /// Port of <c>track_store_changes()</c>: snapshots <paramref name="store"/> (the ambient sample store by default)
    /// now and, when disposed, records a <see cref="StoreEvent"/> to <paramref name="transcript"/> (the ambient
    /// transcript by default) if the store changed. Without a store or a transcript the scope does nothing. Python
    /// skips the event when the tracked block raises; a <c>using</c> cannot tell, so the event is recorded either way.
    /// </summary>
    public static IDisposable Track(Store? store = null, Transcript? transcript = null)
    {
        var context = SampleContext.Current;
        store ??= context?.Store;
        transcript ??= context?.Transcript;
        return store is null || transcript is null ? NoopScope.Instance : new TrackScope(store, transcript, Jsonable.FromStore(store));
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class TrackScope(Store store, Transcript transcript, JsonObject before) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (JsonChanges.Diff(before, Jsonable.FromStore(store)) is { } changes)
            {
                transcript.Add(StoreEvent.FromChanges(changes));
            }
        }
    }
}

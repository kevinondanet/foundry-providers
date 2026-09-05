using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Log.Json;

namespace InspectAzureAI.Eval.Context;

/// <summary>
/// Port of <c>util/_store.py</c> <c>store_from_events</c> / <c>store_from_events_as</c>: rebuilds a <see cref="Store"/>
/// by replaying the <see cref="StoreEvent"/>s of a transcript. Only events at the root or directly inside a
/// root-level span are applied: an enclosing span's event already carries every change its nested spans made, so
/// applying the nested ones too would replay them twice.
/// </summary>
public static class StoreReplay
{
    /// <summary>Port of <c>store_from_events</c>.</summary>
    public static Store StoreFromEvents(IEnumerable<TranscriptEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var data = new JsonObject();
        foreach (var node in EventTree.Build(events))
        {
            switch (node)
            {
                case EventTreeSpan span:
                    foreach (var child in span.Children)
                    {
                        if (child is EventTreeItem { Event: StoreEvent storeEvent })
                        {
                            data = ApplyStoreEvent(data, storeEvent);
                        }
                    }

                    break;
                case EventTreeItem { Event: StoreEvent rootEvent }:
                    data = ApplyStoreEvent(data, rootEvent);
                    break;
            }
        }

        return new Store(PlainJson.ToDictionary(data));
    }

    /// <summary>
    /// Port of <c>store_from_events_as</c>: the replayed store narrowed to <typeparamref name="TModel"/>'s keys (for
    /// <paramref name="instance"/> when given, otherwise only the un-instanced keys) and bound to a detached store.
    /// </summary>
    public static TModel StoreFromEventsAs<TModel>(IEnumerable<TranscriptEvent> events, string? instance = null) where TModel : StoreModel, new()
    {
        var reconstructed = StoreFromEvents(events);
        var prefix = typeof(TModel).Name + ":";
        var detached = new Store();
        foreach (var (key, value) in reconstructed.ToDictionary())
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var unprefixed = key[prefix.Length..];
            if (instance is not null)
            {
                if (!unprefixed.StartsWith(instance + ":", StringComparison.Ordinal))
                {
                    continue;
                }

                unprefixed = unprefixed[(instance.Length + 1)..];
            }
            else if (unprefixed.Contains(':'))
            {
                continue;
            }

            detached.Set(prefix + (instance is null ? "" : instance + ":") + unprefixed, value);
        }

        return StoreModel.Create<TModel>(detached, instance);
    }

    /// <summary>Port of <c>_apply_store_event</c>: applies the event's changes in place (each validated by <see cref="JsonChanges.ToPatchOp"/>).</summary>
    public static JsonObject ApplyStoreEvent(JsonObject data, StoreEvent storeEvent)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(storeEvent);
        var ops = storeEvent.GetChanges().Select(JsonChanges.ToPatchOp).ToList();
        var result = JsonPatch.Apply(data, ops, inPlace: true);
        return result as JsonObject ?? throw new JsonPatchConflictException("A store event replaced the store with a non-object value.");
    }
}

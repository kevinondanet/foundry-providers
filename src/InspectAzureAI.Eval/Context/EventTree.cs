namespace InspectAzureAI.Eval.Context;

/// <summary>Port of <c>event/_tree.py</c> <c>EventTreeNode</c>: a span or a single event.</summary>
public abstract class EventTreeNode
{
    private protected EventTreeNode()
    {
    }
}

/// <summary>Port of <c>EventTreeSpan</c>: a span with its begin / end events and the nodes recorded inside it.</summary>
public sealed class EventTreeSpan : EventTreeNode
{
    public EventTreeSpan(SpanBeginEvent begin)
    {
        ArgumentNullException.ThrowIfNull(begin);
        Begin = begin;
    }

    public string Id => Begin.Id;

    public string? ParentId => Begin.ParentId;

    public string Type => Begin.Type;

    public string Name => Begin.Name;

    public SpanBeginEvent Begin { get; }

    /// <summary>The end event, or null for a span that never ended.</summary>
    public SpanEndEvent? End { get; internal set; }

    public List<EventTreeNode> Children { get; } = [];
}

/// <summary>An ordinary event in the tree.</summary>
public sealed class EventTreeItem : EventTreeNode
{
    public EventTreeItem(TranscriptEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        Event = @event;
    }

    public TranscriptEvent Event { get; }
}

/// <summary>Port of <c>event_tree</c> / <c>event_sequence</c> / <c>event_tree_walk</c>.</summary>
public static class EventTree
{
    /// <summary>
    /// Port of <c>event_tree</c>: a forest of root-level nodes. Spans are created up front so events nest correctly
    /// however they interleave; a single pass keeps every span's children in transcript order. An end event with no
    /// matching begin is dropped, as Python does (with a warning).
    /// </summary>
    public static IReadOnlyList<EventTreeNode> Build(IEnumerable<TranscriptEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var list = events.ToList();
        var nodes = new Dictionary<string, EventTreeSpan>(StringComparer.Ordinal);
        foreach (var e in list.OfType<SpanBeginEvent>())
        {
            nodes.TryAdd(e.Id, new EventTreeSpan(e));
        }

        var roots = new List<EventTreeNode>();
        List<EventTreeNode> Bucket(string? spanId) => spanId is not null && nodes.TryGetValue(spanId, out var span) ? span.Children : roots;

        foreach (var e in list)
        {
            switch (e)
            {
                case SpanBeginEvent begin:
                    Bucket(begin.ParentId).Add(nodes[begin.Id]);
                    break;
                case SpanEndEvent end:
                    if (nodes.TryGetValue(end.Id, out var span))
                    {
                        span.End = end;
                    }

                    break;
                default:
                    Bucket(e.SpanId).Add(new EventTreeItem(e));
                    break;
            }
        }

        return roots;
    }

    /// <summary>Port of <c>event_sequence</c>: the tree flattened back into transcript order.</summary>
    public static IEnumerable<TranscriptEvent> Sequence(IEnumerable<EventTreeNode> tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        foreach (var node in tree)
        {
            if (node is EventTreeSpan span)
            {
                yield return span.Begin;
                foreach (var child in Sequence(span.Children))
                {
                    yield return child;
                }

                if (span.End is { } end)
                {
                    yield return end;
                }
            }
            else
            {
                yield return ((EventTreeItem)node).Event;
            }
        }
    }

    /// <summary>Port of <c>event_tree_walk</c> without a filter: every node in tree order.</summary>
    public static IEnumerable<EventTreeNode> Walk(IEnumerable<EventTreeNode> tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        foreach (var node in tree)
        {
            yield return node;
            if (node is EventTreeSpan span)
            {
                foreach (var child in Walk(span.Children))
                {
                    yield return child;
                }
            }
        }
    }
}

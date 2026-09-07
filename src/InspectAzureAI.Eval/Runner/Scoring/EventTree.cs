using InspectAzureAI.Eval.Context;

namespace InspectAzureAI.Eval.Runner.Scoring;

/// <summary>A node of an <see cref="EventTree"/>: a span or a single event.</summary>
internal abstract class EventTreeNode;

/// <summary>A leaf of the tree: one non-span event.</summary>
internal sealed class EventTreeLeaf(TranscriptEvent @event) : EventTreeNode
{
    public TranscriptEvent Event { get; } = @event;
}

/// <summary>Port of <c>event/_tree.py</c> <c>EventTreeSpan</c>: a span with its begin event, end event (if any) and children in transcript order.</summary>
internal sealed class EventTreeSpan(SpanBeginEvent begin) : EventTreeNode
{
    public string Id => Begin.Id;

    /// <summary>The parent span id; mutable because re-scoring re-parents spans (<c>_get_updated_events</c>).</summary>
    public string? ParentId { get; set; } = begin.ParentId;

    public string Type => Begin.Type;

    public string Name => Begin.Name;

    public SpanBeginEvent Begin { get; } = begin;

    public SpanEndEvent? End { get; set; }

    public List<EventTreeNode> Children { get; } = [];
}

/// <summary>Port of <c>event/_tree.py</c>: a flat event list as a forest of spans, and back.</summary>
internal static class EventTree
{
    /// <summary>
    /// Port of <c>event_tree</c>: one node per <see cref="SpanBeginEvent"/> (created up front so events can be attached whatever
    /// their position), then a single pass placing every event under its span — or at the root when it names no span or an
    /// unknown one. A <see cref="SpanEndEvent"/> without a begin is dropped (Python logs a warning).
    /// </summary>
    public static List<EventTreeNode> Build(IEnumerable<TranscriptEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var list = events as IReadOnlyList<TranscriptEvent> ?? events.ToList();
        var nodes = new Dictionary<string, EventTreeSpan>(StringComparer.Ordinal);
        foreach (var e in list)
        {
            if (e is SpanBeginEvent begin)
            {
                nodes[begin.Id] = new EventTreeSpan(begin);
            }
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
                    Bucket(e.SpanId).Add(new EventTreeLeaf(e));
                    break;
            }
        }

        return roots;
    }

    /// <summary>Port of <c>event_sequence</c>: the forest flattened back into transcript order; a re-parented span's begin event carries its new parent id.</summary>
    public static IEnumerable<TranscriptEvent> Sequence(IEnumerable<EventTreeNode> tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        foreach (var node in tree)
        {
            if (node is EventTreeSpan span)
            {
                yield return string.Equals(span.Begin.ParentId, span.ParentId, StringComparison.Ordinal) ? span.Begin : span.Begin with { ParentId = span.ParentId };
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
                yield return ((EventTreeLeaf)node).Event;
            }
        }
    }

    /// <summary>Port of <c>walk_node_spans</c>: every span of the forest, depth first in tree order.</summary>
    public static IEnumerable<EventTreeSpan> WalkSpans(IEnumerable<EventTreeNode> tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        foreach (var node in tree)
        {
            if (node is EventTreeSpan span)
            {
                yield return span;
                foreach (var nested in WalkSpans(span.Children))
                {
                    yield return nested;
                }
            }
        }
    }
}

using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Scorers;

namespace InspectAzureAI.Eval.Runner.Scoring;

/// <summary>Port of <c>_eval/score.py</c> <c>_get_updated_scores</c> / <c>_get_updated_events</c> and <c>log/_score.py</c> <c>_find_scorers_span</c>.</summary>
internal static class ScoreMerging
{
    /// <summary>
    /// Port of <c>_get_updated_scores</c>: on overwrite only the new scores remain; on append they follow the existing ones,
    /// a name already present getting a <c>-1</c>, <c>-2</c>, ... suffix.
    /// </summary>
    public static IReadOnlyDictionary<string, Score> UpdatedScores(EvalSample sample, IReadOnlyDictionary<string, SampleScore> scores, ScoreAction action)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(scores);
        var updated = new OrderedDictionary<string, Score>(StringComparer.Ordinal);
        if (action == ScoreAction.Overwrite)
        {
            foreach (var (key, score) in scores)
            {
                updated[key] = score.Score;
            }

            return updated;
        }

        foreach (var (key, score) in sample.Scores ?? new Dictionary<string, Score>(StringComparer.Ordinal))
        {
            updated[key] = score;
        }

        foreach (var (key, score) in scores)
        {
            var newKey = key;
            var count = 0;
            while (updated.ContainsKey(newKey))
            {
                count++;
                newKey = $"{key}-{count}";
            }

            updated[newKey] = score.Score;
        }

        return updated;
    }

    /// <summary>
    /// Port of <c>_get_updated_events</c>: with no scorers span in the sample the new events are appended; on append the new
    /// scorer spans are re-parented into the last scorers span; on overwrite that span (and everything in it) is replaced by
    /// the new one in place. <paramref name="newEvents"/> must form exactly one root span (the scorers span).
    /// </summary>
    public static IReadOnlyList<TranscriptEvent> UpdatedEvents(EvalSample sample, IReadOnlyList<TranscriptEvent> newEvents, ScoreAction action)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(newEvents);
        var tree = EventTree.Build(sample.Events);
        var finalScorers = FindScorersSpan(tree);
        if (finalScorers is null)
        {
            return [.. sample.Events, .. newEvents];
        }

        if (EventTree.Build(newEvents) is not [EventTreeSpan newScorers])
        {
            throw new InvalidOperationException("The events of a scoring pass must form exactly one scorers span.");
        }

        if (action == ScoreAction.Append)
        {
            foreach (var child in newScorers.Children)
            {
                if (child is EventTreeSpan span)
                {
                    span.ParentId = finalScorers.Id;
                }
            }

            finalScorers.Children.AddRange(newScorers.Children);
        }
        else
        {
            var siblings = finalScorers.ParentId is null
                ? tree
                : (EventTree.WalkSpans(tree).FirstOrDefault(span => string.Equals(span.Id, finalScorers.ParentId, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"The scorers span's parent span '{finalScorers.ParentId}' is not in the sample events.")).Children;
            siblings[siblings.IndexOf(finalScorers)] = newScorers;
            newScorers.ParentId = finalScorers.ParentId;
        }

        return EventTree.Sequence(tree).ToList();
    }

    /// <summary>
    /// Port of <c>_find_scorers_span</c>: the last span named <c>scorers</c>. Python requires type <c>scorers</c> (its
    /// <c>span()</c> defaults the type to the name); this port's runner writes its scorers span with the default type
    /// <c>span</c>, which is accepted too so the port's own logs merge the same way.
    /// </summary>
    internal static EventTreeSpan? FindScorersSpan(IEnumerable<EventTreeNode> tree) =>
        EventTree.WalkSpans(tree).LastOrDefault(span => span.Name == ScoreLogs.ScorersSpanName && span.Type is ScoreLogs.ScorersSpanName or "span");
}

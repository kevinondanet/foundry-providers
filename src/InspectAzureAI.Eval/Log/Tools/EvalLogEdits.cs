using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Runner.Scoring;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;

namespace InspectAzureAI.Eval.Log.Tools;

using ScoringTree = InspectAzureAI.Eval.Runner.Scoring.EventTree;

/// <summary>
/// Port of the log editing API: <c>log/_score.py</c> <c>edit_score</c>, and <c>log/_edit.py</c>
/// <c>invalidate_samples</c> / <c>uninvalidate_samples</c> (with <c>edit_eval_log</c> re-exported from
/// <see cref="EvalLogEditing"/> so every edit has one entry point). Python edits its log in place; the records here
/// are immutable, so every method returns the edited log. Nothing is persisted: write the result with
/// <see cref="EvalLogFiles.WriteEvalLog"/>.
/// </summary>
public static class EvalLogEdits
{
    /// <summary>The legacy metadata key an explicit <c>reason</c> edit supersedes (see <see cref="EditScore"/>).</summary>
    public const string LegacyUnscoredReasonKey = "unscored_reason";

    /// <summary>Port of <c>edit_eval_log</c> (see <see cref="EvalLogEditing.EditEvalLog"/>).</summary>
    public static EvalLog EditEvalLog(EvalLog log, IEnumerable<LogEdit> edits, ProvenanceData provenance) =>
        EvalLogEditing.EditEvalLog(log, edits, provenance);

    /// <summary>Port of <c>EvalLog.recompute_tags_and_metadata</c> (see <see cref="EvalLogEditing.RecomputeTagsAndMetadata"/>).</summary>
    public static EvalLog RecomputeTagsAndMetadata(EvalLog log) => EvalLogEditing.RecomputeTagsAndMetadata(log);

    /// <summary>
    /// Port of <c>edit_score</c>: edits or adds a score of one sample and records a <see cref="ScoreEditEvent"/>.
    /// The sample is found by <paramref name="sampleId"/> (an int or a string, compared as Python compares ids) and
    /// <paramref name="epoch"/>, which is required when the id occurs in more than one epoch. A new score needs a
    /// value; an existing score first gets its pre-edit state prepended to <see cref="Score.History"/>, then the set
    /// fields of <paramref name="edit"/> applied — a metadata dict replaces <see cref="Score.Metadata"/> rather than
    /// merging into it — and the edit appended. An explicit reason (set or cleared) drops the legacy
    /// <see cref="LegacyUnscoredReasonKey"/> key from the resulting metadata so a re-read cannot resurrect the old
    /// reason. The event is stamped with the last scorers span of the sample and inserted just before that span's
    /// end (or appended when there is no such span). With <paramref name="recomputeMetrics"/> the log's results and
    /// reductions are recomputed from its samples: Python re-creates the scorers from the log header, which this port
    /// does through the built-in metric table (a header metric it cannot re-create is a
    /// <see cref="NotSupportedException"/>); pass <paramref name="scorers"/> to use explicit scorers instead.
    /// </summary>
    /// <exception cref="ArgumentException">The log has no samples, the sample is not found or ambiguous, or a new score has no value (Python's <c>ValueError</c>).</exception>
    public static EvalLog EditScore(
        EvalLog log,
        object sampleId,
        string scoreName,
        ScoreEdit edit,
        bool recomputeMetrics = true,
        int? epoch = null,
        IReadOnlyList<ScorerDef>? scorers = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(sampleId);
        ArgumentException.ThrowIfNullOrEmpty(scoreName);
        ArgumentNullException.ThrowIfNull(edit);
        if (log.Samples is null)
        {
            throw new ArgumentException("Log contains no samples", nameof(log));
        }

        var samples = log.Samples.ToList();
        var index = FindSample(samples, sampleId, epoch);
        var sample = samples[index];
        var scores = new OrderedDictionary<string, Score>(StringComparer.Ordinal);
        foreach (var (name, score) in sample.Scores ?? new Dictionary<string, Score>(StringComparer.Ordinal))
        {
            scores[name] = score;
        }

        if (!scores.TryGetValue(scoreName, out var existing))
        {
            if (!edit.Value.IsSet)
            {
                throw new ArgumentException(
                    $"Cannot add new score '{scoreName}' without providing a value. The 'value' field is required when creating a new score.",
                    nameof(edit));
            }

            var metadata = edit.Metadata.IsSet ? edit.Metadata.Value : null;
            if (edit.Reason.IsSet)
            {
                metadata = DropLegacyUnscoredReason(metadata);
            }

            scores[scoreName] = new Score(RequireValue(edit))
            {
                Answer = edit.Answer.IsSet ? edit.Answer.Value : null,
                Explanation = edit.Explanation.IsSet ? edit.Explanation.Value : null,
                Reason = edit.Reason.IsSet ? edit.Reason.Value : null,
                Metadata = metadata,
                History = [edit],
            };
        }
        else
        {
            var history = existing.History.ToList();
            if (history.Count == 0)
            {
                history.Add(new ScoreEdit
                {
                    Value = Edited<ScoreValue>.Set(existing.Value),
                    Answer = Edited<string>.Set(existing.Answer),
                    Explanation = Edited<string>.Set(existing.Explanation),
                    Reason = Edited<string>.Set(existing.Reason),
                    Metadata = Edited<IReadOnlyDictionary<string, object?>>.Set(existing.Metadata ?? new Dictionary<string, object?>(StringComparer.Ordinal)),
                });
            }

            var updated = existing with
            {
                Value = edit.Value.IsSet ? RequireValue(edit) : existing.Value,
                Answer = edit.Answer.IsSet ? edit.Answer.Value : existing.Answer,
                Explanation = edit.Explanation.IsSet ? edit.Explanation.Value : existing.Explanation,
                Reason = edit.Reason.IsSet ? edit.Reason.Value : existing.Reason,
                Metadata = edit.Metadata.IsSet ? edit.Metadata.Value : existing.Metadata,
            };
            if (edit.Reason.IsSet)
            {
                updated = updated with { Metadata = DropLegacyUnscoredReason(updated.Metadata) };
            }

            history.Add(edit);
            scores[scoreName] = updated with { History = history };
        }

        var events = sample.Events.ToList();
        var scorersSpan = ScoreMerging.FindScorersSpan(ScoringTree.Build(events));
        var scoreEditEvent = new ScoreEditEvent(scoreName, edit) { SpanId = scorersSpan?.Id };
        var endIndex = events.Count;
        if (scorersSpan?.End is { } end)
        {
            var found = events.LastIndexOf(end);
            if (found >= 0)
            {
                endIndex = found;
            }
        }

        events.Insert(endIndex, scoreEditEvent);
        samples[index] = sample with { Scores = scores, Events = events };
        var edited = log with { Samples = samples };
        return recomputeMetrics ? ScoreLogs.RecomputeMetrics(edited, scorers ?? HeaderScorers.FromLog(edited)) : edited;
    }

    /// <summary>
    /// Port of <c>invalidate_samples</c>: marks the samples with the given uuids invalidated (with
    /// <paramref name="provenance"/>) and sets <see cref="EvalLog.Invalidated"/>. Samples already invalidated keep
    /// their original provenance. Logs with invalidated samples are retried by eval sets.
    /// </summary>
    /// <exception cref="ArgumentException">A uuid is not in the log (Python's <c>ValueError</c>).</exception>
    public static EvalLog InvalidateSamples(EvalLog log, IEnumerable<string> sampleUuids, ProvenanceData provenance)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(sampleUuids);
        ArgumentNullException.ThrowIfNull(provenance);
        var selected = PrepareSamples(log, sampleUuids.ToList());
        return selected.Count == 0 ? log : UpdateSampleInvalidation(log, selected, provenance) with { Invalidated = true };
    }

    /// <summary>Port of <c>invalidate_samples(log, "all", provenance)</c>: every sample of the log.</summary>
    public static EvalLog InvalidateAllSamples(EvalLog log, ProvenanceData provenance)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(provenance);
        var selected = AllSamples(log);
        return selected.Count == 0 ? log : UpdateSampleInvalidation(log, selected, provenance) with { Invalidated = true };
    }

    /// <summary>
    /// Port of <c>uninvalidate_samples</c>: clears the invalidation of the given samples and sets
    /// <see cref="EvalLog.Invalidated"/> to whether any invalidated sample remains.
    /// </summary>
    /// <exception cref="ArgumentException">A uuid is not in the log (Python's <c>ValueError</c>).</exception>
    public static EvalLog UninvalidateSamples(EvalLog log, IEnumerable<string> sampleUuids)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(sampleUuids);
        var selected = PrepareSamples(log, sampleUuids.ToList());
        return selected.Count == 0 ? log : WithInvalidatedFlag(UpdateSampleInvalidation(log, selected, null));
    }

    /// <summary>Port of <c>uninvalidate_samples(log, "all")</c>: every sample of the log.</summary>
    public static EvalLog UninvalidateAllSamples(EvalLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var selected = AllSamples(log);
        return selected.Count == 0 ? log : WithInvalidatedFlag(UpdateSampleInvalidation(log, selected, null));
    }

    /// <summary>Python's <c>sample.id == sample_id</c>: strings compare ordinally, numbers by value, and a string never equals a number.</summary>
    internal static bool IdEquals(object a, object b)
    {
        if (a is string textA)
        {
            return b is string textB && string.Equals(textA, textB, StringComparison.Ordinal);
        }

        return b is not string && string.Equals(EvalLogFormat.IdText(a), EvalLogFormat.IdText(b), StringComparison.Ordinal);
    }

    /// <summary>Port of <c>_drop_legacy_unscored_reason</c>: a copy without the legacy key (the input may be aliased by history entries and events).</summary>
    internal static IReadOnlyDictionary<string, object?>? DropLegacyUnscoredReason(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null || !metadata.ContainsKey(LegacyUnscoredReasonKey))
        {
            return metadata;
        }

        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            if (!string.Equals(key, LegacyUnscoredReasonKey, StringComparison.Ordinal))
            {
                copy[key] = value;
            }
        }

        return copy;
    }

    private static ScoreValue RequireValue(ScoreEdit edit) =>
        edit.Value.Value ?? throw new ArgumentException("A score value cannot be null.", nameof(edit));

    private static int FindSample(List<EvalSample> samples, object sampleId, int? epoch)
    {
        if (epoch is { } wanted)
        {
            var index = samples.FindIndex(sample => IdEquals(sample.Id, sampleId) && sample.Epoch == wanted);
            return index >= 0 ? index : throw new ArgumentException($"Sample with id {EvalLogFormat.IdText(sampleId)} and epoch {wanted} not found", nameof(sampleId));
        }

        var matches = new List<int>();
        for (var i = 0; i < samples.Count; i++)
        {
            if (IdEquals(samples[i].Id, sampleId))
            {
                matches.Add(i);
            }
        }

        return matches.Count switch
        {
            0 => throw new ArgumentException($"Sample with id {EvalLogFormat.IdText(sampleId)} not found", nameof(sampleId)),
            1 => matches[0],
            _ => throw new ArgumentException($"Multiple samples found with id {EvalLogFormat.IdText(sampleId)}. You must specify the epoch parameter.", nameof(epoch)),
        };
    }

    /// <summary>Port of <c>_prepare_samples</c> for an explicit uuid list: the indexes of the named samples, in the order given.</summary>
    private static List<int> PrepareSamples(EvalLog log, List<string> sampleUuids)
    {
        var samples = log.Samples ?? [];
        var byUuid = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < samples.Count; i++)
        {
            if (samples[i].Uuid is { } uuid)
            {
                byUuid.TryAdd(uuid, i);
            }
        }

        var invalid = sampleUuids.Where(uuid => !byUuid.ContainsKey(uuid)).ToList();
        if (invalid.Count > 0)
        {
            throw new ArgumentException($"Samples [{string.Join(", ", invalid.Select(uuid => $"'{uuid}'"))}] not found in log", nameof(sampleUuids));
        }

        return sampleUuids.Select(uuid => byUuid[uuid]).ToList();
    }

    private static List<int> AllSamples(EvalLog log) => Enumerable.Range(0, log.Samples?.Count ?? 0).ToList();

    /// <summary>Port of <c>_update_sample_invalidation</c>: sets (or, with a null provenance, clears) the invalidation of the selected samples that are not already in that state.</summary>
    private static EvalLog UpdateSampleInvalidation(EvalLog log, List<int> selected, ProvenanceData? provenance)
    {
        var samples = (log.Samples ?? []).ToList();
        foreach (var index in selected)
        {
            var sample = samples[index];
            if (provenance is null ? sample.Invalidation is null : sample.Invalidation is not null)
            {
                continue;
            }

            samples[index] = sample with { Invalidation = provenance };
        }

        return log with { Samples = samples };
    }

    private static EvalLog WithInvalidatedFlag(EvalLog log) =>
        log with { Invalidated = (log.Samples ?? []).Any(sample => sample.Invalidation is not null) };
}

/// <summary>
/// Port of <c>_eval/score.py</c> <c>resolve_scorers_info</c>: the scorers recorded in a log header, re-created for
/// metrics recomputation. Python re-instantiates each metric through its registry; this port re-creates the
/// built-in metrics by name (<see cref="LogHeader.MetricFromLog"/>) and cannot score with them (only their metrics
/// are used). Also the score-only results computation the recovery path shares with <see cref="ScoreLogs"/>.
/// </summary>
internal static class HeaderScorers
{
    /// <summary>The header's scorers as <see cref="ScorerDef"/>s (empty when the header records none).</summary>
    /// <exception cref="NotSupportedException">A metric group, or a metric this port cannot re-create.</exception>
    public static IReadOnlyList<ScorerDef> FromLog(EvalLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return (log.Eval.Scorers ?? []).Select(scorer => new ScorerDef(scorer.Name, MissingScorer, MetricsOf(scorer))).ToList();
    }

    /// <summary>
    /// Port of <c>eval_results</c> over collected sample scores (the core of <c>recompute_metrics</c> without the
    /// samples in memory): a score name no header scorer accounts for gets Python's <c>ScorerInfo.from_name</c>
    /// fallback — <paramref name="metrics"/>, else accuracy and stderr.
    /// </summary>
    public static ComputedResults ComputeResults(
        int totalSamples,
        IReadOnlyList<IReadOnlyDictionary<string, SampleScore>> scores,
        IReadOnlyList<ScorerDef> headerScorers,
        IReadOnlyList<ScoreReducer>? reducers,
        IReadOnlyList<MetricDef>? metrics,
        int completedSamples,
        HeadlineMetric? headlineMetric)
    {
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentNullException.ThrowIfNull(headerScorers);
        var resolved = headerScorers.ToList();
        var names = EvalResultsBuilder.UniqueScorerNames(resolved).ToList();
        var known = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var sampleScores in scores)
        {
            foreach (var name in sampleScores.Keys)
            {
                if (known.Add(name))
                {
                    resolved.Add(new ScorerDef(name, MissingScorer, metrics ?? [Metrics.Accuracy(), Metrics.Stderr()]));
                    names.Add(name);
                }
            }
        }

        return EvalResultsBuilder.ComputeResults(totalSamples, scores, resolved, names, reducers, metrics, null, null, completedSamples, headlineMetric);
    }

    private static IReadOnlyList<MetricDef> MetricsOf(EvalScorer scorer) => scorer.Metrics switch
    {
        null => [],
        JsonArray { Count: 0 } => [],
        JsonArray items => items.Select(item => MetricOf(scorer.Name, item)).ToList(),
        _ => throw new NotSupportedException($"The scorer '{scorer.Name}' in the log header declares metric groups (a dict of metric lists), which this port cannot re-create; pass the scorers explicitly."),
    };

    private static MetricDef MetricOf(string scorerName, JsonNode? item) =>
        item is JsonObject definition && definition["name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var name)
            ? LogHeader.MetricFromLog(name, definition["options"] as JsonObject)
            : throw new NotSupportedException($"The scorer '{scorerName}' in the log header declares a group of metrics, which this port cannot re-create; pass the scorers explicitly.");

    private static Task<Score> MissingScorer(TaskState state, Target target, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A scorer re-created from a log header only carries metrics and cannot score.");
}

using System.Runtime.ExceptionServices;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;

namespace InspectAzureAI.Eval.Runner.Scoring;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>_eval/score.py</c> (<c>score_async</c>, <c>_run_score_task</c>, <c>task_state_from_sample</c>), the
/// non-interactive parts of <c>_cli/score.py</c> and <c>log/_metric.py</c> <c>recompute_metrics</c>: scoring the samples
/// of an existing log with new scorers, appending or overwriting their scores and score events, and recomputing the
/// results and epoch reductions with the same metric and reducer semantics as the runner (<c>EvalResultsBuilder</c>).
/// </summary>
public static class ScoreLogs
{
    /// <summary>Port of <c>SCORERS_SPAN_NAME</c>: the span enclosing a sample's scorer spans.</summary>
    public const string ScorersSpanName = "scorers";

    /// <summary>Port of <c>SCORER_SPAN_TYPE</c>: the type of each scorer's span.</summary>
    public const string ScorerSpanType = "scorer";

    /// <summary>
    /// Port of <c>score_async</c>: scores every sample of <paramref name="log"/> with <paramref name="scorers"/> and returns a
    /// new log (the input is not mutated) whose samples carry the updated scores and score events, whose results and
    /// reductions are recomputed, whose header lists the scorers applied, and whose headline is re-resolved.
    /// <paramref name="action"/> defaults to <see cref="ScoreAction.Append"/>; <paramref name="epochsReducer"/> defaults
    /// to the reducers recorded in the log header (and is recorded there when given). On append the new scorers use
    /// their own metrics; on overwrite the task-level metrics recorded in the header, if any, replace them (Python's
    /// <c>metrics_from_log_header</c>); <see cref="ScoreLogOptions.Metrics"/> replaces both.
    /// </summary>
    /// <exception cref="ArgumentException">The log has no samples, or no scorer was given.</exception>
    /// <exception cref="InvalidOperationException">A scorer wrote to <c>TaskState.Scores</c> itself (Python's <c>RuntimeError</c>).</exception>
    public static async Task<EvalLog> ScoreAsync(
        EvalLog log,
        IReadOnlyList<ScorerDef> scorers,
        ScoreAction? action = null,
        IReadOnlyList<ScoreReducer>? epochsReducer = null,
        ScoreLogOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(scorers);
        if (log.Samples is not { Count: > 0 } samples)
        {
            throw new ArgumentException("There are no samples to score in the log.", nameof(log));
        }

        if (scorers.Count == 0)
        {
            throw new ArgumentException("At least one scorer is required to score a log.", nameof(scorers));
        }

        options ??= new ScoreLogOptions();
        if (options.MaxSamples is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxSamples must be at least 1.");
        }

        var resolvedAction = action ?? ScoreAction.Append;
        var model = options.Model ?? new Model(new UnavailableModelApi(log.Eval.Model));
        using var roles = ModelRoles.Begin(ModelRoles.Resolve(options.ModelRoles));

        var scored = await ScoreSamplesAsync(log, samples, scorers, model, resolvedAction, options.MaxSamples ?? samples.Count, cancellationToken).ConfigureAwait(false);
        var scorerNames = scored[0].ScorerNames;
        var scores = scored.Select(s => s.Scores).ToList();
        var completedSamples = scored.Count(s => s.Sample.Error is null);

        var logMetrics = options.Metrics ?? (resolvedAction != ScoreAction.Append ? LogHeader.MetricsFromLogHeader(log) : null);
        var resolvedScorers = logMetrics is null ? scorers : scorers.Select(scorer => scorer with { Metrics = logMetrics }).ToList();

        var config = log.Eval.Config;
        IReadOnlyList<ScoreReducer>? reducers;
        if (epochsReducer is not null)
        {
            config = config with { EpochsReducer = LogHeader.ReducerLogNames(epochsReducer) };
            reducers = epochsReducer;
        }
        else
        {
            reducers = LogHeader.ReducersFromLogHeader(log);
        }

        // the headline is resolved below against the merged scores: these cover only this pass's scorers
        var computed = EvalResultsBuilder.ComputeResults(
            samples.Count,
            scores,
            resolvedScorers,
            scorerNames,
            reducers,
            logMetrics,
            log.Results?.EarlyStopping,
            log.Results?.Metadata,
            completedSamples,
            headlineMetric: null);
        var applied = LogHeader.ToEvalScorers(resolvedScorers);

        EvalResults results;
        IReadOnlyList<EvalSampleReductions>? reductions;
        IReadOnlyList<EvalScorer> evalScorers;
        if (resolvedAction == ScoreAction.Overwrite || log.Results is null)
        {
            results = computed.Results;
            reductions = computed.Reductions;
            evalScorers = applied;
        }
        else
        {
            results = log.Results with { Scores = [.. log.Results.Scores, .. computed.Results.Scores] };
            reductions = computed.Reductions is null ? log.Reductions : [.. log.Reductions ?? [], .. computed.Reductions];
            evalScorers = [.. log.Eval.Scorers ?? [], .. applied];
        }

        var headline = HeadlineMetrics.Resolve(results, log.Eval.HeadlineMetric);
        results = results with { Headline = headline is null ? null : HeadlineMetrics.Ref(headline) };

        return log with
        {
            Eval = log.Eval with { Scorers = evalScorers, Config = config },
            Results = results,
            Reductions = reductions,
            Samples = scored.Select(s => s.Sample).ToList(),
        };
    }

    /// <summary>
    /// Port of the <c>inspect score</c> command: reads the JSON log at <paramref name="path"/>, resolves the action with
    /// <see cref="ResolveAction"/>, scores it, and writes the result back to <see cref="ScoreLogOptions.OutputPath"/> or over
    /// the input in the same (JSON) format. The zip <c>.eval</c> format is not supported by this port and is refused.
    /// </summary>
    public static async Task<EvalLog> ScoreAsync(
        string path,
        IReadOnlyList<ScorerDef> scorers,
        ScoreAction? action = null,
        IReadOnlyList<ScoreReducer>? epochsReducer = null,
        ScoreLogOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (string.Equals(Path.GetExtension(path), ".eval", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"'{path}' is a zip-format (.eval) log, which this port cannot read or write; convert it to JSON first (inspect log convert).");
        }

        var log = await EvalLogWriter.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        var scored = await ScoreAsync(log, scorers, ResolveAction(log, action), epochsReducer, options, cancellationToken).ConfigureAwait(false);
        var output = options?.OutputPath ?? path;
        scored = scored with { Location = output };
        await EvalLogWriter.WriteAsync(scored, output, cancellationToken).ConfigureAwait(false);
        return scored;
    }

    /// <summary>
    /// Port of <c>_cli/score.py</c> <c>resolve_action</c> without its prompt: an explicit action wins; otherwise a log that already
    /// has result scores is appended to (the prompt's default) and one without is overwritten.
    /// </summary>
    public static ScoreAction ResolveAction(EvalLog log, ScoreAction? action)
    {
        ArgumentNullException.ThrowIfNull(log);
        return action ?? (log.Results is { Scores.Count: > 0 } ? ScoreAction.Append : ScoreAction.Overwrite);
    }

    /// <summary>
    /// Port of <c>eval_results</c> over samples already scored (the core of <c>recompute_metrics</c>): the sample scores are
    /// grouped by scorer name, each <see cref="ScorerDef"/> contributes the metrics for its (uniquely named) scores, and a
    /// score name no scorer accounts for gets Python's default metrics (accuracy and stderr) or <paramref name="metrics"/>.
    /// <paramref name="metrics"/> replaces every scorer's metrics; <paramref name="reducers"/> null means the implicit mean
    /// view, an empty list disables epoch reduction. Shared with the runner, so a recomputation over a run's samples equals
    /// the run's own results.
    /// </summary>
    public static ComputedResults ComputeResults(
        IReadOnlyList<EvalSample> samples,
        IReadOnlyList<ScorerDef> scorers,
        IReadOnlyList<ScoreReducer>? reducers = null,
        IReadOnlyList<MetricDef>? metrics = null,
        HeadlineMetric? headlineMetric = null,
        EarlyStoppingSummary? earlyStopping = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(scorers);
        var scores = new List<IReadOnlyDictionary<string, SampleScore>>();
        foreach (var sample in samples)
        {
            if (sample.Scores is { Count: > 0 } sampleScores)
            {
                var entry = new OrderedDictionary<string, SampleScore>(StringComparer.Ordinal);
                foreach (var (name, score) in sampleScores)
                {
                    entry[name] = new SampleScore(score, sample.Id, sample.Metadata);
                }

                scores.Add(entry);
            }
        }

        var resolvedScorers = scorers.ToList();
        var names = EvalResultsBuilder.UniqueScorerNames(resolvedScorers).ToList();
        var known = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var sampleScores in scores)
        {
            foreach (var name in sampleScores.Keys)
            {
                if (known.Add(name))
                {
                    // Python: ScorerInfo.from_name, which falls back to accuracy + stderr when the scorer cannot be loaded
                    resolvedScorers.Add(new ScorerDef(name, MissingScorer, metrics ?? [Metrics.Accuracy(), Metrics.Stderr()]));
                    names.Add(name);
                }
            }
        }

        return EvalResultsBuilder.ComputeResults(
            samples.Count,
            scores,
            resolvedScorers,
            names,
            reducers,
            metrics,
            earlyStopping,
            metadata,
            samples.Count(sample => sample.Error is null),
            headlineMetric);
    }

    /// <summary>
    /// Port of <c>log/_metric.py</c> <c>recompute_metrics</c>: the log with its results and reductions recomputed from its
    /// samples' scores, using the reducers and task-level metrics recorded in the header (or <paramref name="metrics"/>) and
    /// the metrics of <paramref name="scorers"/> (Python re-creates these from the header's scorer entries).
    /// </summary>
    public static EvalLog RecomputeMetrics(EvalLog log, IReadOnlyList<ScorerDef> scorers, IReadOnlyList<MetricDef>? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(scorers);
        if (log.Samples is null)
        {
            throw new ArgumentException("Log contains no samples", nameof(log));
        }

        var computed = ComputeResults(
            log.Samples,
            scorers,
            LogHeader.ReducersFromLogHeader(log),
            metrics ?? LogHeader.MetricsFromLogHeader(log),
            log.Eval.HeadlineMetric,
            log.Results?.EarlyStopping,
            log.Results?.Metadata);
        return log with { Results = computed.Results, Reductions = computed.Reductions };
    }

    /// <summary>Port of <c>unique_scorer_name</c>: <paramref name="baseName"/>, or the first <c>name1</c>, <c>name2</c>, ... not already used.</summary>
    internal static string UniqueScorerName(string baseName, IEnumerable<string> alreadyUsed)
    {
        var used = alreadyUsed as ICollection<string> ?? alreadyUsed.ToList();
        var name = baseName;
        var count = 1;
        while (used.Contains(name))
        {
            name = $"{baseName}{count}";
            count++;
        }

        return name;
    }

    private static Task<Score> MissingScorer(TaskState state, Target target, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A scorer re-created from a score name only carries metrics and cannot score.");

    /// <summary>
    /// Port of the <c>tg_collect</c> fan-out of <c>score_async</c>: every sample is scored concurrently (bounded by
    /// <paramref name="maxSamples"/>); the first failure cancels the rest and is rethrown, and caller cancellation propagates.
    /// </summary>
    private static async Task<ScoredSample[]> ScoreSamplesAsync(EvalLog log, IReadOnlyList<EvalSample> samples, IReadOnlyList<ScorerDef> scorers, Model model, ScoreAction action, int maxSamples, CancellationToken cancellationToken)
    {
        var scored = new ScoredSample[samples.Count];
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var semaphore = new SemaphoreSlim(maxSamples);
        Exception? failure = null;
        var failureSync = new object();

        await Task.WhenAll(Enumerable.Range(0, samples.Count).Select(ScoreOneAsync)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return scored;

        async Task ScoreOneAsync(int index)
        {
            try
            {
                await semaphore.WaitAsync(abort.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                abort.Token.ThrowIfCancellationRequested();
                scored[index] = await ScoreSampleAsync(log, samples[index], scorers, model, action, abort.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (abort.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                lock (failureSync)
                {
                    failure ??= ex;
                }

                await abort.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        }
    }

    /// <summary>
    /// Port of <c>_run_score_task</c> and <c>task_state_from_sample</c>: the sample's attachments are resolved for the
    /// scorers (the sample written back keeps its references), a completed <see cref="TaskState"/> is rebuilt from it (with
    /// its existing scores on append), the transcript is seeded with its events, and each scorer runs in its own span under
    /// a <c>scorers</c> span, recording a <see cref="ScoreEvent"/> carrying the scorer name and the sample's usage.
    /// </summary>
    private static async Task<ScoredSample> ScoreSampleAsync(EvalLog log, EvalSample sample, IReadOnlyList<ScorerDef> scorers, Model model, ScoreAction action, CancellationToken cancellationToken)
    {
        var resolved = sample.Attachments.Count > 0 ? LogAttachments.ResolveSampleAttachments(sample, ResolveAttachments.Core) : sample;
        var store = new Store(resolved.Store);
        var state = new TaskState(
            log.Eval.Model,
            resolved.Id,
            resolved.Epoch,
            resolved.Input,
            resolved.Messages,
            resolved.Target,
            resolved.Choices,
            resolved.Output,
            metadata: resolved.Metadata.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            store: store,
            sampleUuid: resolved.Uuid)
        {
            Completed = true,
            Scores = action == ScoreAction.Append && resolved.Scores is { } existing
                ? new Dictionary<string, Score>(existing, StringComparer.Ordinal)
                : new Dictionary<string, Score>(StringComparer.Ordinal),
        };
        var transcript = new Transcript(resolved.Events);
        using var scope = SampleContext.Begin(new SampleContext { ActiveModel = model, Store = store, Transcript = transcript, SampleState = state });

        var existingNames = state.Scores!.Keys.ToList();
        var results = new OrderedDictionary<string, SampleScore>(StringComparer.Ordinal);
        var scorerNames = new List<string>(scorers.Count);
        var modelUsage = resolved.ModelUsage.Count > 0 ? resolved.ModelUsage : null;
        var roleUsage = resolved.RoleUsage.Count > 0 ? resolved.RoleUsage : null;
        using (transcript.Span(ScorersSpanName, ScorersSpanName))
        {
            foreach (var scorer in scorers)
            {
                var scorerName = UniqueScorerName(scorer.Name, existingNames.Concat(results.Keys).ToList());
                scorerNames.Add(scorerName);
                using var span = transcript.Span(scorerName, ScorerSpanType);
                var score = await scorer.Score(state, state.Target, cancellationToken).ConfigureAwait(false);
                var stateScores = state.Scores ??= new Dictionary<string, Score>(StringComparer.Ordinal);
                if (stateScores.ContainsKey(scorerName))
                {
                    throw new InvalidOperationException($"Scorer {scorerName} has modified state.scores");
                }

                stateScores[scorerName] = score;
                transcript.Add(new ScoreEvent(score, state.Target) { Scorer = scorerName, ModelUsage = modelUsage, RoleUsage = roleUsage });
                results[scorerName] = new SampleScore(score, state.SampleId, state.Metadata, scorer.Name);
            }
        }

        var newEvents = transcript.Events.Skip(resolved.Events.Count).ToList();
        var updated = sample with
        {
            Scores = ScoreMerging.UpdatedScores(sample, results, action),
            Events = ScoreMerging.UpdatedEvents(sample, newEvents, action),
        };
        return new ScoredSample(updated, results, scorerNames);
    }

    /// <summary>One sample after a scoring pass: the updated sample, the scores this pass produced (keyed by unique scorer name) and those names in scorer order.</summary>
    private sealed record ScoredSample(EvalSample Sample, IReadOnlyDictionary<string, SampleScore> Scores, IReadOnlyList<string> ScorerNames);
}

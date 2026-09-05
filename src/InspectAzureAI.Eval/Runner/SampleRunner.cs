using System.Diagnostics;
using System.Globalization;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;

namespace InspectAzureAI.Eval.Runner;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The outcome of one (sample, epoch) attempt: the logged sample, its scorer scores keyed by unique scorer name,
/// the exception (if any) and whether it ended by cancellation. <see cref="Retry"/> is set when the attempt
/// errored with retries remaining: nothing is logged and the run re-enters with a fresh attempt.
/// </summary>
internal sealed record SampleResult(EvalSample Sample, IReadOnlyDictionary<string, SampleScore> Scores, Exception? Exception, bool Cancelled)
{
    public EvalRetryError? Retry { get; init; }
}

/// <summary>
/// Port of <c>_eval/task/run.py</c> <c>SampleAttempt</c>: the retry state one sample run carries across its
/// attempts. The budget is invariant and the remaining retries derive from the errors accrued, so budget and
/// history cannot drift apart; the sample uuid minted by the first attempt is reused by the retries.
/// </summary>
internal sealed record SampleAttempt(int RetryLimit, IReadOnlyList<EvalRetryError> Errors, string? SampleUuid)
{
    public static SampleAttempt First(int retryLimit) => new(retryLimit, [], null);

    /// <summary>1-based attempt number.</summary>
    public int Number => Errors.Count + 1;

    public bool IsFirst => Errors.Count == 0;

    public int RetriesRemaining => RetryLimit - Errors.Count;

    /// <summary>The next attempt's state after an error-retry: the error appended, the uuid carried.</summary>
    public SampleAttempt Advance(EvalRetryError error, string sampleUuid) => new(RetryLimit, [.. Errors, error], sampleUuid);
}

/// <summary>
/// Port of <c>_eval/task/run.py</c> <c>_task_run_sample_attempt</c> for one (sample, epoch) attempt: sandbox init
/// (files, setup), task state and ambient <see cref="SampleContext"/>, setup + solver inside the sample-level
/// scoped limits (token, message, turn, time, working — a limit ends the solver but the sample is still scored),
/// scoring, the <see cref="EvalSample"/> record, sandbox cleanup. An attempt that errors with retries remaining
/// hands back a <see cref="SampleResult.Retry"/> for <c>Eval</c>'s attempt loop.
/// </summary>
internal sealed class SampleRunner(
    EvalTask task,
    Model model,
    IReadOnlyList<string> scorerNames,
    int? messageLimit,
    int? tokenLimit,
    TimeSpan? timeLimit,
    bool cleanup,
    double? costLimit = null,
    int? turnLimit = null,
    TimeSpan? workingLimit = null)
{
    public Task<SampleResult> RunAsync(Sample sample, SandboxSpec? sandbox, int epoch, CancellationToken cancellationToken) =>
        RunAsync(sample, sandbox, epoch, SampleAttempt.First(0), cancellationToken);

    public async Task<SampleResult> RunAsync(Sample sample, SandboxSpec? sandbox, int epoch, SampleAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(attempt);
        SampleLimits.ResetSnapshot();
        var startedAt = DateTimeOffset.UtcNow;
        var store = new Store();
        var transcript = new Transcript();
        // usage, waiting-time and cost accounting: message/token/time enforcement is the scoped stack entered around
        // the solvers, while the cost limit is still enforced by this flat class
        var limits = new Limits { CostLimit = costLimit, StartedAt = startedAt };
        var state = new TaskState(
            model.Name,
            sample.Id!,
            epoch,
            sample.Input,
            sample.Input.ToMessages(),
            sample.Target,
            sample.Choices,
            messageLimit: messageLimit,
            tokenLimit: tokenLimit,
            metadata: sample.Metadata?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            store: store,
            sampleUuid: attempt.SampleUuid);
        var tokenNode = new TokenLimit(tokenLimit);
        var messageNode = new MessageLimit(messageLimit);
        var turnNode = new TurnLimit(turnLimit);
        var timeNode = new TimeLimit(timeLimit);
        var workingNode = new WorkingLimit(workingLimit);
        state.AttachLimits(messageNode, tokenNode);
        var scores = new OrderedDictionary<string, SampleScore>(StringComparer.Ordinal);
        EvalError? error = null;
        Exception? exception = null;
        EvalSampleLimit? limit = null;
        EvalRetryError? retry = null;
        var cancelled = false;
        SandboxEnvironments? sandboxes = null;
        // Python's start_time is taken once the init span closes, so sandbox setup is outside total_time
        long? workStarted = null;

        try
        {
            if (sandbox is not null)
            {
                using var initSpan = transcript.Span("init", "init");
                sandboxes = await SandboxSetup.InitAsync(task.Name, sandbox, sample, cancellationToken).ConfigureAwait(false);
            }

            var context = new SampleContext
            {
                ActiveModel = model,
                Store = store,
                Transcript = transcript,
                Limits = limits,
                Sandboxes = sandboxes,
                SampleState = state,
                Scorer = task.Scorers.Count > 0
                    ? scored => ScoreIntermediateAsync(scored, sample, state, transcript, cancellationToken)
                    : null,
            };
            using var scope = SampleContext.Begin(context);
            using var modelAccumulators = SampleModelAccumulators.Begin();
            var generate = GenerateLoop.Create(model);
            workStarted = Stopwatch.GetTimestamp();

            LimitExceededException? workingError = null;
            using (Limit.Apply(tokenNode, messageNode, turnNode, timeNode, workingNode))
            {
                using var solverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeNode.Token);
                using var monitorStop = new CancellationTokenSource();
                var monitor = MonitorWorkingLimitAsync(
                    workingNode,
                    exceeded =>
                    {
                        workingError = exceeded;
                        solverCts.Cancel();
                    },
                    monitorStop.Token);
                try
                {
                    using var solversSpan = transcript.Span("solvers");
                    if (task.Setup is { } setup)
                    {
                        state = await RunSolverAsync("setup", setup, state, generate, transcript, solverCts.Token).ConfigureAwait(false);
                    }

                    state = await RunSolverAsync("solver", task.Solver, state, generate, transcript, solverCts.Token).ConfigureAwait(false);
                }
                catch (LimitExceededException ex)
                {
                    limit = SampleLimit(ex);
                }
                catch (Approval.TerminateSampleException ex)
                {
                    // Python's `except TerminateSampleError`: an approver ended the sample; it is still scored
                    transcript.Add(new SampleLimitEvent("operator", ex.Reason, 1));
                    limit = new EvalSampleLimit("operator", 1, ex.Reason);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeNode.Exceeded)
                {
                    limit = TimeLimitExceeded(timeNode, transcript);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && workingError is { } exceeded)
                {
                    // the monitor records no event of its own, so this is its sole recorder (as in Python)
                    transcript.Add(new SampleLimitEvent("working", exceeded.Message, exceeded.Limit));
                    limit = SampleLimit(exceeded);
                }
                finally
                {
                    await monitorStop.CancelAsync().ConfigureAwait(false);
                    await monitor.ConfigureAwait(false);
                    // Python snapshots the sample limits while their scopes are still open, for the scorers
                    SampleLimits.RecordSnapshot(state.Messages.Count);
                }
            }

            state.Completed = true;
            limits.Suspend();

            // Python gives scoring half the original time limit: it must still run after a timed-out solver,
            // but a hung container should not cost the full limit a second time.
            using var scoringCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeLimit is { } scoringLimit)
            {
                scoringCts.CancelAfter(scoringLimit / 2);
            }

            try
            {
                using var scorersSpan = transcript.Span("scorers");
                for (var i = 0; i < task.Scorers.Count; i++)
                {
                    var name = scorerNames[i];
                    var score = await RunScorerAsync(name, task.Scorers[i], state, sample.Target, transcript, scoringCts.Token).ConfigureAwait(false);
                    state.Scores ??= new Dictionary<string, Score>(StringComparer.Ordinal);
                    state.Scores[name] = score;
                    scores[name] = new SampleScore(score, sample.Id, sample.Metadata, task.Scorers[i].Name);
                }
            }
            catch (OperationCanceledException) when (scoringCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Timed out while scoring the sample.");
            }
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
            exception = ex;
            error = EvalError.FromException(ex);
            transcript.Add(new ErrorEvent(error.Message, error.Traceback));
        }
        catch (Exception ex)
        {
            exception = ex;
            error = EvalError.FromException(ex);
            if (attempt.RetriesRemaining > 0)
            {
                // Python: with retries left the error is neither counted nor logged; the run re-enters
                retry = RetryError(error, transcript.Events);
            }
            else
            {
                transcript.Add(new ErrorEvent(error.Message, error.Traceback));
            }
        }
        finally
        {
            if (sandboxes?.Cleanup is { } cleanupSandboxes)
            {
                try
                {
                    await cleanupSandboxes(cleanup).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    transcript.Add(new ErrorEvent($"Sandbox cleanup failed: {ex.Message}", ex.ToString()));
                }
            }
        }

        var elapsed = workStarted is { } started ? Stopwatch.GetElapsedTime(started) : (TimeSpan?)null;
        var evalSample = new EvalSample
        {
            Id = sample.Id!,
            Epoch = epoch,
            Input = sample.Input,
            Choices = sample.Choices,
            Target = sample.Target,
            Sandbox = sandbox,
            Files = sample.Files?.Keys.ToArray(),
            Setup = sample.Setup,
            Messages = state.Messages.ToArray(),
            Output = state.Output,
            Scores = new OrderedDictionary<string, Score>(scores.Select(pair => KeyValuePair.Create(pair.Key, pair.Value.Score)), StringComparer.Ordinal),
            Metadata = new Dictionary<string, object?>(state.Metadata, StringComparer.Ordinal),
            Store = store.ToDictionary(),
            Events = transcript.Events,
            ModelUsage = limits.UsageByModel,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            TotalTime = elapsed is { } total ? Math.Round(total.TotalSeconds, 3) : null,
            WorkingTime = elapsed is { } working ? Math.Round((working - limits.WaitingTime).TotalSeconds, 3) : null,
            Uuid = state.Uuid,
            Error = error,
            ErrorRetries = attempt.Errors,
            Limit = limit,
        };
        return new SampleResult(evalSample, scores, exception, cancelled) { Retry = retry };
    }

    /// <summary>Port of <c>Plan.__call__</c>'s per-solver step: a solver span (plus the legacy <see cref="StepEvent"/> pair) around the call.</summary>
    private static async Task<TaskState> RunSolverAsync(string name, Solver solver, TaskState state, Generate generate, Transcript transcript, CancellationToken cancellationToken)
    {
        transcript.Add(new StepEvent(name, "solver", "begin"));
        try
        {
            using var span = transcript.Span(name, "solver");
            return await solver(state, generate, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            transcript.Add(new StepEvent(name, "solver", "end"));
        }
    }

    private static async Task<Score> RunScorerAsync(string name, ScorerDef scorer, TaskState state, Target target, Transcript transcript, CancellationToken cancellationToken)
    {
        transcript.Add(new StepEvent(name, "scorer", "begin"));
        try
        {
            using var span = transcript.Span(name, "scorer");
            var score = await scorer.Score(state, target, cancellationToken).ConfigureAwait(false);
            transcript.Add(new ScoreEvent(score, target));
            return score;
        }
        finally
        {
            transcript.Add(new StepEvent(name, "scorer", "end"));
        }
    }

    /// <summary>
    /// Port of <c>monitor_working_limit()</c>: a background check that ends the solver when the sample's working
    /// time is exceeded. Python polls every second; waiting time can only push the deadline later, so this sleeps
    /// until the limit could first be exceeded and re-checks.
    /// </summary>
    private static async Task MonitorWorkingLimitAsync(WorkingLimit node, Action<LimitExceededException> exceeded, CancellationToken stop)
    {
        if (node.Limit is null)
        {
            return;
        }

        try
        {
            while (true)
            {
                var remaining = TimeSpan.FromSeconds(Math.Clamp(node.Remaining ?? 0, 0, TimeSpan.FromHours(1).TotalSeconds));
                await Task.Delay(remaining + TimeSpan.FromMilliseconds(10), stop).ConfigureAwait(false);
                if (node.Check() is { } error)
                {
                    exceeded(error);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Port of <c>score(state)</c>: the task scorers over the sample's state. A state an agent built itself
    /// contributes only its messages and output — Python copies <c>sample_state()</c> and swaps those in, so
    /// the sample's target, metadata and identity always come from the runner.
    /// </summary>
    private async Task<IReadOnlyList<Score>> ScoreIntermediateAsync(TaskState scored, Sample sample, TaskState sampleState, Transcript transcript, CancellationToken cancellationToken)
    {
        var state = ReferenceEquals(scored, sampleState) ? scored : sampleState.WithMessages(scored.Messages, scored.Output);
        var result = new List<Score>(task.Scorers.Count);
        foreach (var scorer in task.Scorers)
        {
            result.Add(await scorer.Score(state, sample.Target, cancellationToken).ConfigureAwait(false));
        }

        foreach (var score in result)
        {
            transcript.Add(new ScoreEvent(score, sample.Target, Intermediate: true));
        }

        return result;
    }

    /// <summary>Port of <c>_eval_retry_error</c>: the error plus the events from the attempt's last <see cref="ModelEvent"/> onward (all of them when there is none).</summary>
    private static EvalRetryError RetryError(EvalError error, IReadOnlyList<TranscriptEvent> events)
    {
        var start = 0;
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i] is ModelEvent)
            {
                start = i;
                break;
            }
        }

        return new EvalRetryError(error.Message, error.Traceback, error.TracebackAnsi) { Events = events.Skip(start).ToArray() };
    }

    /// <summary>Port of the <c>except LimitExceededError</c> branch: the limit applied, or the configured value for errors built without one, or -1.</summary>
    private EvalSampleLimit SampleLimit(LimitExceededException ex)
    {
        double? configured = ex.Limit ?? ex.Type switch
        {
            "message" => messageLimit,
            "token" => tokenLimit,
            "turn" => turnLimit,
            "time" => timeLimit?.TotalSeconds,
            "cost" => costLimit,
            "working" => workingLimit?.TotalSeconds,
            _ => null,
        };
        var value = configured
            ?? (double.TryParse(ex.LimitStr, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1);
        return new EvalSampleLimit(ex.Type, value, ex.Message);
    }

    /// <summary>Port of <c>_TimeLimit.__exit__</c> for the sample's deadline: the event is recorded here since the exception never surfaces through a scope exit.</summary>
    private static EvalSampleLimit TimeLimitExceeded(TimeLimit timeNode, Transcript transcript)
    {
        var error = timeNode.ExceededError()!;
        transcript.Add(new SampleLimitEvent("time", error.Message, error.Limit));
        return new EvalSampleLimit("time", error.Limit!.Value, error.Message);
    }
}

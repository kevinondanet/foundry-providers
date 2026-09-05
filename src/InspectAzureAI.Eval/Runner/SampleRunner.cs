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

/// <summary>The outcome of one (sample, epoch) run: the logged sample, its scorer scores keyed by unique scorer name, the exception (if any) and whether it ended by cancellation.</summary>
internal sealed record SampleResult(EvalSample Sample, IReadOnlyDictionary<string, SampleScore> Scores, Exception? Exception, bool Cancelled);

/// <summary>
/// Port of <c>_eval/task/run.py</c> <c>task_run_sample</c> for one (sample, epoch): sandbox init (files, setup),
/// task state and ambient <see cref="SampleContext"/>, setup + solver under the sample limits (a limit ends the
/// solver but the sample is still scored), scoring, the <see cref="EvalSample"/> record, sandbox cleanup.
/// </summary>
internal sealed class SampleRunner(
    EvalTask task,
    Model model,
    IReadOnlyList<string> scorerNames,
    int? messageLimit,
    int? tokenLimit,
    TimeSpan? timeLimit,
    bool cleanup,
    double? costLimit = null)
{
    public async Task<SampleResult> RunAsync(Sample sample, SandboxSpec? sandbox, int epoch, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var store = new Store();
        var transcript = new Transcript();
        var limits = new Limits { MessageLimit = messageLimit, TokenLimit = tokenLimit, TimeLimit = timeLimit, CostLimit = costLimit, StartedAt = startedAt };
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
            store: store);
        var scores = new OrderedDictionary<string, SampleScore>(StringComparer.Ordinal);
        EvalError? error = null;
        Exception? exception = null;
        EvalSampleLimit? limit = null;
        var cancelled = false;
        SandboxEnvironments? sandboxes = null;

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

            using var solverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeLimit is { } solverLimit)
            {
                solverCts.CancelAfter(solverLimit);
            }

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
            catch (OperationCanceledException) when (solverCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                limit = TimeLimitExceeded();
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
            transcript.Add(new ErrorEvent(error.Message, error.Traceback));
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

        var totalTime = Math.Round(stopwatch.Elapsed.TotalSeconds, 3);
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
            TotalTime = totalTime,
            WorkingTime = totalTime,
            Uuid = state.Uuid,
            Error = error,
            Limit = limit,
        };
        return new SampleResult(evalSample, scores, exception, cancelled);
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

    private EvalSampleLimit SampleLimit(LimitExceededException ex)
    {
        double? configured = ex.Type switch
        {
            "message" => messageLimit,
            "token" => tokenLimit,
            "time" => timeLimit?.TotalSeconds,
            "cost" => costLimit,
            _ => null,
        };
        var value = configured
            ?? (double.TryParse(ex.LimitStr, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1);
        return new EvalSampleLimit(ex.Type, value, ex.Message);
    }

    private EvalSampleLimit TimeLimitExceeded()
    {
        var seconds = timeLimit!.Value.TotalSeconds;
        return new EvalSampleLimit("time", seconds, $"Time limit exceeded. limit: {LimitExceededException.FormatLimit(seconds)} seconds");
    }
}

using System.Globalization;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Concurrency;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Hooks;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Model.Cost;
using InspectAzureAI.Eval.Runner.EvalSet;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Runner;

using Concurrency = InspectAzureAI.Eval.Concurrency.Concurrency;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>_eval/eval.py</c> <c>eval()</c> and <c>_eval/task/run.py</c> <c>task_run</c> for one task: resolves
/// the samples (ids, <c>sample_id</c> / <c>limit</c>), initialises the sandbox providers once, runs every
/// (sample, epoch) under <see cref="EvalOptions.MaxSamples"/> concurrency, reduces epoch scores, computes the
/// metrics and writes the log (<c>.eval</c> by default, or JSON per <see cref="EvalOptions.LogFormat"/>) to <see cref="EvalOptions.LogDir"/> incrementally as samples complete.
/// </summary>
public static class Eval
{
    /// <summary>
    /// Runs <paramref name="task"/>. With fail-on-error the first failing sample aborts the run (status
    /// <c>error</c>, the eval-level error set); otherwise failed samples are recorded and the run succeeds.
    /// Cancellation still cleans up every sandbox, writes a <c>cancelled</c> log and then propagates.
    /// Lifecycle hooks (<see cref="HookRegistry"/> plus <see cref="EvalOptions.Hooks"/>) are notified of the run's
    /// start and end — the end carries the exception when the run throws, as Python's <c>eval()</c> does.
    /// </summary>
    public static Task<EvalLog> RunAsync(EvalTask task, EvalOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(options);
        return RunAsync(task, options, new HookRun(options), cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="task"/> as one task of an eval-set pass: the run id and the run start/end emissions are
    /// the <paramref name="group"/>'s (Python's <c>eval_set</c> calls <c>eval()</c> once per pass), task start/end this task's.
    /// </summary>
    internal static Task<EvalLog> RunAsync(EvalTask task, EvalOptions options, HookRunGroup group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(group);
        return RunAsync(task, options, new HookRun(options, group), cancellationToken);
    }

    private static async Task<EvalLog> RunAsync(EvalTask task, EvalOptions options, HookRun hooks, CancellationToken cancellationToken)
    {
        try
        {
            HookStartup.InitHooks(options.Reporter is { } reporter ? reporter.Message : null);
            var log = await RunCoreAsync(task, options, hooks, cancellationToken).ConfigureAwait(false);
            await hooks.EndAsync(null).ConfigureAwait(false);
            return log;
        }
        catch (Exception ex)
        {
            await hooks.EndAsync(ex).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="task"/>. With fail-on-error the first failing sample aborts the run (status
    /// <c>error</c>, the eval-level error set); otherwise failed samples are recorded and the run succeeds.
    /// Cancellation still cleans up every sandbox, writes a <c>cancelled</c> log and then propagates.
    /// </summary>
    private static async Task<EvalLog> RunCoreAsync(EvalTask task, EvalOptions options, HookRun hooks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxSamples is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxSamples must be at least 1.");
        }

        if (options.Epochs is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Epochs must be at least 1.");
        }

        var reporter = options.Reporter;
        var sourceModel = ResolveModel(task, options);
        var model = EvalModel(task, sourceModel, options);
        var resolvedRoles = ModelRoles.Merge(ModelRoles.Resolve(task.ModelRoles), ModelRoles.Resolve(options.ModelRoles));
        using var modelRoles = ModelRoles.Begin(resolvedRoles);
        // Python: the eval-level policy replaces the task's (run.py), and init_tool_approval installs it (or none) for every sample
        var approval = (options.Approval ?? task.Approval)?.Resolve();
        using var approvalScope = ToolApproval.Init(approval);
        var samples = ResolveSamples(task, options, reporter);
        var epochs = options.Epochs ?? task.Epochs?.Count ?? 1;
        var failOnError = options.FailOnError ?? task.FailOnError;
        var continueOnFail = options.ContinueOnFail ?? task.ContinueOnFail;
        var retryOnError = options.RetryOnError ?? task.RetryOnError;
        var messageLimit = options.MessageLimit ?? task.MessageLimit;
        var tokenLimit = options.TokenLimit ?? task.TokenLimit;
        var timeLimit = options.TimeLimit ?? task.TimeLimit;
        var costLimit = options.CostLimit ?? task.CostLimit;
        if (options.ModelCostConfig is { } modelCostConfig)
        {
            ModelCostConfig.Apply(modelCostConfig);
        }

        ResolveModelCosts(model, costLimit);
        var turnLimit = options.TurnLimit ?? task.TurnLimit;
        var workingLimit = options.WorkingLimit ?? task.WorkingLimit;
        if (retryOnError is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RetryOnError must not be negative.");
        }
        var scorerNames = EvalResultsBuilder.UniqueScorerNames(task.Scorers);
        foreach (var reducer in task.Epochs?.Reducers ?? [])
        {
            Reducers.Validate(epochs, reducer);
        }

        var startedAt = DateTimeOffset.UtcNow;

        var spec = new EvalSpec
        {
            EvalSetId = hooks.EvalSetId,
            RunId = hooks.RunId,
            TaskId = options.TaskId ?? ShortUuid.Generate(),
            Created = startedAt,
            Task = task.Name,
            TaskVersion = task.Version,
            TaskArgs = task.TaskArgs ?? new Dictionary<string, object?>(StringComparer.Ordinal),
            TaskArgsPassed = task.TaskArgs,
            Metadata = EvalMetadata(options.Metadata, task.Metadata),
            Dataset = new EvalDataset
            {
                Name = task.Dataset.Name,
                Location = task.Dataset.Location,
                // Python records len(task.dataset), the whole dataset; the selection is sample_ids (eval-set sample reuse compares against this)
                Samples = task.Dataset.Count,
                SampleIds = samples.Select(sample => sample.Id!).ToArray(),
                Shuffled = task.Dataset.Shuffled,
            },
            Sandbox = task.Sandbox,
            Model = ModelIdentity.ForLog(model.Api),
            ModelArgs = model.Api.ModelArgsForLog,
            ModelBaseUrl = model.Api.BaseUrl,
            ModelGenerateConfig = sourceModel.Config,
            ModelRoles = ModelRolesConfig.ToConfig(resolvedRoles),
            Config = new EvalConfig
            {
                Limit = options.Limit,
                SampleId = options.SampleIds,
                Epochs = epochs,
                EpochsReducer = EvalResultsBuilder.EpochsReducerNames(task.Epochs?.Reducers),
                FailOnError = failOnError,
                ContinueOnFail = continueOnFail,
                RetryOnError = retryOnError,
                MessageLimit = messageLimit,
                TokenLimit = tokenLimit,
                TurnLimit = turnLimit,
                TimeLimit = (int?)timeLimit?.TotalSeconds,
                CostLimit = costLimit,
                WorkingLimit = (int?)workingLimit?.TotalSeconds,
                MaxSamples = options.MaxSamples,
                SandboxCleanup = options.Cleanup,
                Approval = approval is { Count: > 0 } ? ApprovalPolicies.ToConfig(approval).ToJson() : null,
            },
        };

        // one plan instance for the task-start hook and the log, as Python's TaskLogger holds it
        var plan = ResolvePlan(task, model.Config);
        await hooks.StartAsync(spec, plan, cancellationToken).ConfigureAwait(false);
        var sandboxSpecs = samples.Select(sample => SandboxSetup.ResolveSpec(task.Sandbox, sample)).ToList();
        var providerSpecs = sandboxSpecs.OfType<SandboxSpec>().Distinct().ToList();
        var runner = new SampleRunner(task, model, scorerNames, messageLimit, tokenLimit, timeLimit, options.Cleanup, costLimit, turnLimit: turnLimit, workingLimit: workingLimit, hooks: hooks);
        var totalSamples = samples.Count * epochs;
        var logFormat = options.LogFormat ?? LogFormats.FromEnvironment() ?? LogFormats.Default;
        var recorder = LogRecorders.CreateForFormat(logFormat, options.LogDir);
        await using var recorderScope = recorder.ConfigureAwait(false);
        var logLocation = await recorder.LogInitAsync(spec, LogFileNaming.LogFilePath(options.LogDir, spec, logFormat), cancellationToken: cancellationToken).ConfigureAwait(false);
        await recorder.LogStartAsync(spec, plan, cancellationToken).ConfigureAwait(false);
        var flushBuffer = recorder.DefaultLogBuffer(totalSamples, highThroughput: false);
        var pendingFlush = 0;
        var results = new SampleResult?[totalSamples];
        // Evaluation totals include discarded retry attempts; sample records retain only their own usage.
        var executedUsage = new Limits();
        var earlyStops = new EarlyStop?[totalSamples];
        // Python: continue_on_fail never aborts mid-run; the fail_on_error policy is applied again at the end
        var errorHandler = new SampleErrorHandler(continueOnFail == true ? FailOnError.Never : failOnError, totalSamples);
        var earlyStopping = task.EarlyStopping;
        var stoppingManager = "";
        Exception? failure = null;
        var failureSync = new object();

        // one linked source: the caller's cancellation and a fail-on-error abort both stop the samples in flight
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var semaphore = SampleScheduler.CreateSampleSemaphore(options.MaxSamples, model.Config, model.AdaptiveConnections ?? AdaptiveConnections.FromConfigValue(model.Config.AdaptiveConnections), model.Api);
        try
        {
            if (earlyStopping is not null)
            {
                stoppingManager = await earlyStopping.StartTaskAsync(spec, samples, epochs, cancellationToken).ConfigureAwait(false);
            }

            foreach (var providerSpec in providerSpecs)
            {
                await SandboxRegistry.Get(providerSpec.Type).TaskInitAsync(task.Name, providerSpec.Config, cancellationToken).ConfigureAwait(false);
            }

            var runs = new List<Task>(results.Length);
            for (var i = 0; i < samples.Count; i++)
            {
                for (var epoch = 1; epoch <= epochs; epoch++)
                {
                    runs.Add(RunSampleAsync(i * epochs + epoch - 1, samples[i], sandboxSpecs[i], epoch));
                }
            }

            await Task.WhenAll(runs).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (failureSync)
            {
                failure ??= ex;
            }
        }
        finally
        {
            // the runner never registers its limiter under a task id, so it owns it (see docs/ports/concurrency.md)
            (semaphore as IDisposable)?.Dispose();
            foreach (var providerSpec in providerSpecs)
            {
                try
                {
                    await SandboxRegistry.Get(providerSpec.Type).TaskCleanupAsync(task.Name, providerSpec.Config, options.Cleanup, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    reporter?.Message($"Sandbox task cleanup failed for '{providerSpec.Type}': {ex.Message}");
                }
            }
        }

        if (options.SampleSource is { } sampleSource
            && (cancellationToken.IsCancellationRequested || failure is not null || SampleErrorHandler.ShouldEvalFail(errorHandler.ErrorCount, totalSamples, failOnError)))
        {
            foreach (var index in CarryForwardUnloggedSamples(sampleSource, samples, epochs, results))
            {
                // Python's carry-forward completes the sample on the logger too, so the file carries the error history
                await LogSampleAsync(results[index]!.Sample).ConfigureAwait(false);
            }
        }

        var completed = results.OfType<SampleResult>().ToList();
        var evalSamples = completed.Select(result => result.Sample).ToList();
        var status = EvalStatus.Success;
        EvalError? error = null;
        if (cancellationToken.IsCancellationRequested)
        {
            status = EvalStatus.Cancelled;
        }
        else if (failure is not null)
        {
            status = EvalStatus.Error;
            error = EvalError.FromException(failure);
        }
        else if (SampleErrorHandler.ShouldEvalFail(errorHandler.ErrorCount, totalSamples, failOnError))
        {
            // Python's end-of-run check (continue_on_fail, or a threshold reached by the last samples): the log is
            // marked failed with no eval-level error
            status = EvalStatus.Error;
        }

        EarlyStoppingSummary? stoppingSummary = null;
        if (earlyStopping is not null && status != EvalStatus.Cancelled && failure is null)
        {
            try
            {
                var stoppingMetadata = await earlyStopping.CompleteTaskAsync(cancellationToken).ConfigureAwait(false);
                stoppingSummary = new EarlyStoppingSummary(stoppingManager, earlyStops.OfType<EarlyStop>().ToArray()) { Metadata = stoppingMetadata };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                status = EvalStatus.Error;
                error = EvalError.FromException(ex);
            }
        }

        var computed = EvalResultsBuilder.ComputeResults(
            totalSamples,
            completed.Select(result => result.Scores).ToList(),
            task.Scorers,
            scorerNames,
            task.Epochs?.Reducers,
            task.Metrics,
            earlyStopping: stoppingSummary,
            completedSamples: evalSamples.Count(sample => sample.Error is null),
            metricsByKeyOverride: task.MetricsByKey);
        // Python's EvalLog validator recomputes tags/metadata on construction, so the returned log carries eval.metadata too
        var log = EvalLogEditing.RecomputeTagsAndMetadata(new EvalLog
        {
            Status = status,
            Eval = spec,
            Plan = plan,
            Results = computed.Results,
            Stats = new EvalStats
            {
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                ModelUsage = CumulativeUsage(options.InitialModelUsage, executedUsage.UsageByModel),
                ConnectionLimitHistory = Concurrency.AdaptiveControllers().SelectMany(controller => controller.History).ToArray(),
            },
            Error = error,
            Samples = evalSamples,
            Reductions = computed.Reductions,
            Location = logLocation,
        });
        await recorder.LogFinishAsync(spec, status, log.Stats, log.Results, log.Reductions, error, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        await hooks.TaskEndAsync(log).ConfigureAwait(false);
        reporter?.Message($"Log written to {log.Location}");

        if (status == EvalStatus.Cancelled)
        {
            throw new OperationCanceledException("The eval was cancelled.", cancellationToken);
        }

        return log;

        // Port of TaskLogger.complete_sample: the condensed sample is recorded and the log flushed at the buffer cadence
        // (never cancelled: a stopped run still keeps the samples that completed, as Python does)
        async Task LogSampleAsync(EvalSample sample)
        {
            await recorder.LogSampleAsync(spec, LogAttachments.CondenseSample(sample), cancellationToken: CancellationToken.None).ConfigureAwait(false);
            if (Interlocked.Increment(ref pendingFlush) >= flushBuffer)
            {
                Interlocked.Exchange(ref pendingFlush, 0);
                await recorder.FlushAsync(spec, CancellationToken.None).ConfigureAwait(false);
            }
        }

        // Port of task_run_sample: one run as a loop of error-retry attempts (design/sample-lifecycle.md)
        async Task RunSampleAsync(int index, Sample sample, SandboxSpec? sandbox, int epoch)
        {
            var attempt = SampleAttempt.First(retryOnError ?? 0);
            // the prior attempt's record, consulted before the semaphore as in Python: a clean one is reused as
            // logged, an errored one seeds this run's error_retries (task/run.py run_sample)
            switch (options.SampleSource?.Lookup(sample.Id!, epoch))
            {
                case PreviousSample.Reusable reusable:
                    var reusedResult = ReusedSampleResult(reusable.Sample);
                    results[index] = reusedResult;
                    // Python re-logs a reused sample into this attempt's log (the reuse sweep), so the file is complete on its own
                    await LogSampleAsync(reusable.Sample).ConfigureAwait(false);
                    reporter?.SampleCompleted(reusable.Sample);
                    if (earlyStopping is not null)
                    {
                        await earlyStopping.CompleteSampleAsync(sample.Id!, epoch, reusedResult.Scores, abort.Token).ConfigureAwait(false);
                    }

                    return;
                case PreviousSample.Errored errored:
                    attempt = SampleAttempt.First(retryOnError ?? 0, errored.ErrorRetries);
                    break;
            }

            while (true)
            {
                ConcurrencyLease lease;
                try
                {
                    lease = await semaphore.AcquireAsync(abort.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // never started (or a retry abandoned while queued): Python logs nothing for it
                    return;
                }

                try
                {
                    if (abort.IsCancellationRequested)
                    {
                        return;
                    }

                    // the early stopping check is the first thing of every attempt: a halted run is completed without being logged
                    if (earlyStopping is not null && await earlyStopping.ScheduleSampleAsync(sample.Id!, epoch, abort.Token).ConfigureAwait(false) is { } stop)
                    {
                        earlyStops[index] = stop;
                        return;
                    }

                    if (attempt.IsFirst)
                    {
                        reporter?.SampleStarted(sample.Id!, epoch);
                    }

                    reporter?.Stats(EvalRunStats.Capture(semaphore));
                    var result = await runner.RunAsync(sample, sandbox, epoch, attempt, abort.Token).ConfigureAwait(false);
                    foreach (var (name, usage) in result.Sample.ModelUsage)
                    {
                        executedUsage.RecordUsage(usage, name);
                    }

                    if (result.Retry is { } retry)
                    {
                        // re-enter with the error recorded and the uuid carried; releasing the semaphore first sends
                        // the retry to the back of the queue
                        attempt = attempt.Advance(retry, result.Sample.Uuid!);
                        continue;
                    }

                    results[index] = result;
                    await LogSampleAsync(result.Sample).ConfigureAwait(false);
                    reporter?.SampleCompleted(result.Sample);
                    reporter?.Stats(EvalRunStats.Capture(semaphore));
                    var raised = false;
                    if (result.Exception is { } ex && !result.Cancelled && errorHandler.RecordError())
                    {
                        lock (failureSync)
                        {
                            failure ??= ex;
                        }

                        raised = true;
                        await abort.CancelAsync().ConfigureAwait(false);
                    }

                    // Python reports scores to the early stopping hook for completed samples and errored ones that
                    // still scored, never for a sample whose error fails the eval
                    if (earlyStopping is not null && !raised && !result.Cancelled && (result.Exception is null || result.Scores.Count > 0))
                    {
                        await earlyStopping.CompleteSampleAsync(sample.Id!, epoch, result.Scores, abort.Token).ConfigureAwait(false);
                    }

                    return;
                }
                finally
                {
                    lease.Release();
                }
            }
        }
    }

    /// <summary>Port of <c>task.config.merge(eval config)</c>: the model's own (eval-level) config layers over the task's.</summary>
    /// <summary>
    /// Port of <c>resolve_plan</c> + <c>plan_to_eval_plan</c>: the plan the log records for <paramref name="task"/> under
    /// <paramref name="config"/>. Solver delegates carry no registry name, so the steps are named as the transcript
    /// spans are (<c>setup</c> when the task has one, then <c>solver</c>). The eval-set task identifier hashes this
    /// same plan, so a task and the log it produced agree. Deviation: Python records <c>task.config.merge(kwargs)</c>
    /// (the task's config plus the eval-level generate kwargs, which this port has no separate surface for); the
    /// runner records the effective config of <see cref="EvalModel"/> — the model's config with the task's layered
    /// over it — so the plan shows what generation actually used.
    /// </summary>
    internal static EvalPlan ResolvePlan(EvalTask task, GenerateConfig config) => new()
    {
        Steps = task.Setup is null ? [new EvalPlanStep("solver")] : [new EvalPlanStep("setup"), new EvalPlanStep("solver")],
        Config = config,
    };

    /// <summary>
    /// Port of <c>ResolvedTask.model = task.model or model</c> (<c>_eval/loader.py</c>): the task's own
    /// <see cref="EvalTask.Model"/> wins, then the eval-level <see cref="EvalOptions.Model"/>; neither is an error
    /// (Python's <c>get_model</c> raises <c>ValueError("No model specified ...")</c>).
    /// </summary>
    private static Model ResolveModel(EvalTask task, EvalOptions options) =>
        task.Model
        ?? options.Model
        ?? throw new ArgumentException("No model specified: set EvalOptions.Model or the task's EvalTask.Model.");

    /// <summary>
    /// The model the samples generate with: <paramref name="source"/>'s api and retry policy under
    /// <c>model.config.merge(task.config)</c> — Python's <c>Model.generate</c> layers the task's generate config over
    /// the model's own (<c>_model.py</c> <c>base_config.merge(config)</c>), so a task value wins over the model's.
    /// </summary>
    private static Model EvalModel(EvalTask task, Model source, EvalOptions options)
    {
        var merged = new Model(source.Api, source.Config.Merge(task.Config), source.Retry) { AdaptiveConnections = options.AdaptiveConnections ?? source.AdaptiveConnections };
        return source.EventSink is { } sink ? merged.WithEventSink(sink) : merged;
    }

    /// <summary>
    /// Port of the id assignment in <c>_eval/run.py</c> (1-based when missing, then unique) and
    /// <c>slice_dataset</c> (the <c>sample_id</c> filter of <see cref="SampleIdFilter"/> — glob patterns, task-scoped
    /// ids, a warning per unmatched pattern and an error when nothing matches — else the <c>limit</c> prefix; a zero
    /// limit selects everything).
    /// </summary>
    private static List<Sample> ResolveSamples(EvalTask task, EvalOptions options, IEvalReporter? reporter)
    {
        var dataset = task.Dataset;
        var samples = new List<Sample>(dataset.Count);
        for (var i = 0; i < dataset.Count; i++)
        {
            var sample = dataset[i];
            samples.Add(sample.Id is null ? sample with { Id = i + 1 } : sample);
        }

        // Python keys on the value, so the int 1 and the string "1" are distinct samples (only the sample_id
        // filter below compares text, as its values come from the command line).
        var duplicates = samples.GroupBy(sample => SampleIdKey(sample.Id), StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => IdText(group.First().Id)).ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException($"The dataset contains duplicate sample ids: {string.Join(", ", duplicates)}.");
        }

        if (options.SampleIds is { } ids)
        {
            var scoped = SampleIdFilter.ResolveForTask(task.Name, ids);
            return SampleIdFilter.Filter(samples, dataset.Name, scoped, reporter is null ? null : reporter.Message);
        }

        if (options.Limit is > 0 and var limit)
        {
            return samples.Take(limit).ToList();
        }

        return samples;
    }

    private static string IdText(object? id) => Convert.ToString(id, CultureInfo.InvariantCulture) ?? "";

    /// <summary>A grouping key that tells a string id from a numeric one with the same text.</summary>
    internal static string SampleIdKey(object? id) => (id is string ? "s:" : "n:") + IdText(id);

    /// <summary>Port of <c>resolve_model_costs</c>: a cost limit requires cost data for the eval model and its configured fallbacks, otherwise a <see cref="PrerequisiteError"/> before any sample runs.</summary>
    private static void ResolveModelCosts(Model model, double? costLimit)
    {
        if (costLimit is null)
        {
            return;
        }

        var missing = ServingModels(model.Api).Distinct(StringComparer.Ordinal)
            .Where(name => ModelInfoLookup.GetModelInfo(name)?.Cost is null).ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        throw new PrerequisiteError(
            $"cost_limit requires cost data for all models. Missing cost data for: {string.Join(", ", missing)}. "
            + $"Use ModelInfoLookup.SetModelCost() or {ModelCostConfig.EnvironmentVariable} to configure pricing.");

        static IEnumerable<string> ServingModels(IModelApi api) => api is FallbackModelApi fallback
            ? ServingModels(fallback.Primary).Concat(fallback.Fallbacks.SelectMany(ServingModels))
            : [api.ModelName];
    }

    /// <summary>Port of <c>init_model_usage(initial_model_usage)</c>: the previous attempt's totals (which already cover the samples reused from it) plus the usage of this attempt's own runs.</summary>
    private static Dictionary<string, ModelUsage> CumulativeUsage(IReadOnlyDictionary<string, ModelUsage>? initial, IReadOnlyDictionary<string, ModelUsage> executed)
    {
        var usage = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
        foreach (var (name, prior) in initial ?? new Dictionary<string, ModelUsage>(StringComparer.Ordinal))
        {
            usage[name] = prior;
        }

        foreach (var (name, added) in executed)
        {
            usage[name] = usage.TryGetValue(name, out var existing) ? existing + added : added;
        }

        return usage;
    }

    /// <summary>A prior attempt's record taken as this run's result: the sample as logged, with its scores as <see cref="SampleScore"/>s (Python's <c>scores_as_logged</c>); an errored record contributes no scores.</summary>
    private static SampleResult ReusedSampleResult(EvalSample sample)
    {
        var scores = new Dictionary<string, SampleScore>(StringComparer.Ordinal);
        if (sample.Error is null && sample.Scores is { } logged)
        {
            foreach (var (name, score) in logged)
            {
                scores[name] = new SampleScore(score, sample.Id, sample.Metadata, name);
            }
        }

        return new SampleResult(sample, scores, null, false);
    }

    /// <summary>
    /// Port of <c>carry_forward_unlogged_samples</c>: on a non-success finish, a planned sample that errored in the
    /// previous attempt but never ran in this one (a sibling's failure stopped the run first) is re-logged from the
    /// previous record, so the next attempt's sample source still sees its error history. Returns the result
    /// indices it filled, for the caller to re-log.
    /// </summary>
    private static List<int> CarryForwardUnloggedSamples(EvalSampleSource source, IReadOnlyList<Sample> samples, int epochs, SampleResult?[] results)
    {
        var carried = new List<int>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < samples.Count; i++)
        {
            positions[SampleIdKey(samples[i].Id)] = i;
        }

        foreach (var (id, epoch) in source.ErrorHistoryIds().OrderBy(candidate => IdText(candidate.Id), StringComparer.Ordinal).ThenBy(candidate => candidate.Epoch))
        {
            if (epoch < 1 || epoch > epochs || !positions.TryGetValue(SampleIdKey(id), out var position))
            {
                continue;
            }

            var index = position * epochs + epoch - 1;
            if (results[index] is null && source.Lookup(id, epoch) is PreviousSample.Errored previous)
            {
                results[index] = ReusedSampleResult(previous.Sample);
                carried.Add(index);
            }
        }

        return carried;
    }

    /// <summary>
    /// Port of <c>_eval/run.py</c>'s <c>metadata=((metadata or {}) | (task.metadata or {})) or None</c>: the eval-level
    /// metadata with the task's own merged over it, or null when both are empty (Python writes <c>null</c>, not <c>{}</c>).
    /// </summary>
    private static IReadOnlyDictionary<string, object?>? EvalMetadata(IReadOnlyDictionary<string, object?>? evalMetadata, IReadOnlyDictionary<string, object?>? taskMetadata)
    {
        if (evalMetadata is not { Count: > 0 } && taskMetadata is not { Count: > 0 })
        {
            return null;
        }

        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in evalMetadata ?? new Dictionary<string, object?>())
        {
            merged[key] = value;
        }

        foreach (var (key, value) in taskMetadata ?? new Dictionary<string, object?>())
        {
            merged[key] = value;
        }

        return merged;
    }
}

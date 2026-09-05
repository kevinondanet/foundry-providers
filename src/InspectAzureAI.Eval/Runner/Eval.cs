using System.Globalization;
using System.Security.Cryptography;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Concurrency;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Model.Cost;
using InspectAzureAI.Eval.Sandbox;
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
/// metrics and writes the JSON log to <see cref="EvalOptions.LogDir"/>.
/// </summary>
public static class Eval
{
    /// <summary>
    /// Runs <paramref name="task"/>. With fail-on-error the first failing sample aborts the run (status
    /// <c>error</c>, the eval-level error set); otherwise failed samples are recorded and the run succeeds.
    /// Cancellation still cleans up every sandbox, writes a <c>cancelled</c> log and then propagates.
    /// </summary>
    public static async Task<EvalLog> RunAsync(EvalTask task, EvalOptions options, CancellationToken cancellationToken = default)
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
        var model = EvalModel(task, options);
        using var modelRoles = ModelRoles.Begin(ModelRoles.Merge(ModelRoles.Resolve(task.ModelRoles), ModelRoles.Resolve(options.ModelRoles)));
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
        var startedAt = DateTimeOffset.UtcNow;

        var spec = new EvalSpec
        {
            RunId = ShortUuid.Generate(),
            TaskId = ShortUuid.Generate(),
            Created = startedAt,
            Task = task.Name,
            TaskVersion = task.Version,
            Dataset = new EvalDataset
            {
                Name = task.Dataset.Name,
                Location = task.Dataset.Location,
                Samples = samples.Count,
                SampleIds = samples.Select(sample => sample.Id!).ToArray(),
                Shuffled = task.Dataset.Shuffled,
            },
            Sandbox = task.Sandbox,
            Model = model.Name,
            Config = new EvalConfig
            {
                Limit = options.Limit,
                SampleId = options.SampleIds,
                Epochs = epochs,
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

        var sandboxSpecs = samples.Select(sample => SandboxSetup.ResolveSpec(task.Sandbox, sample)).ToList();
        var providerSpecs = sandboxSpecs.OfType<SandboxSpec>().Distinct().ToList();
        var runner = new SampleRunner(task, model, scorerNames, messageLimit, tokenLimit, timeLimit, options.Cleanup, costLimit, turnLimit: turnLimit, workingLimit: workingLimit);
        var totalSamples = samples.Count * epochs;
        var results = new SampleResult?[totalSamples];
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

        var log = new EvalLog
        {
            Status = status,
            Eval = spec,
            Results = new EvalResults
            {
                TotalSamples = totalSamples,
                CompletedSamples = evalSamples.Count(sample => sample.Error is null),
                EarlyStopping = stoppingSummary,
                Scores = EvalResultsBuilder.BuildScores(task.Scorers, scorerNames, completed.Select(result => result.Scores).ToList(), task.Epochs?.Reducers, task.Metrics),
            },
            Stats = new EvalStats
            {
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                ModelUsage = AggregateUsage(evalSamples),
                ConnectionLimitHistory = Concurrency.AdaptiveControllers().SelectMany(controller => controller.History).ToArray(),
            },
            Error = error,
            Samples = evalSamples,
            Location = LogPath(options.LogDir, task.Name, startedAt),
        };
        EvalLogWriter.Write(log, log.Location!);
        reporter?.Message($"Log written to {log.Location}");

        if (status == EvalStatus.Cancelled)
        {
            throw new OperationCanceledException("The eval was cancelled.", cancellationToken);
        }

        return log;

        // Port of task_run_sample: one run as a loop of error-retry attempts (design/sample-lifecycle.md)
        async Task RunSampleAsync(int index, Sample sample, SandboxSpec? sandbox, int epoch)
        {
            var attempt = SampleAttempt.First(retryOnError ?? 0);
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
                    if (result.Retry is { } retry)
                    {
                        // re-enter with the error recorded and the uuid carried; releasing the semaphore first sends
                        // the retry to the back of the queue
                        attempt = attempt.Advance(retry, result.Sample.Uuid!);
                        continue;
                    }

                    results[index] = result;
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
    private static Model EvalModel(EvalTask task, EvalOptions options)
    {
        var source = options.Model;
        var merged = new Model(source.Api, task.Config.Merge(source.Config), source.Retry) { AdaptiveConnections = options.AdaptiveConnections ?? source.AdaptiveConnections };
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

    /// <summary>Port of <c>resolve_model_costs</c>: a cost limit requires cost data for the eval model, otherwise a <see cref="PrerequisiteError"/> before any sample runs.</summary>
    private static void ResolveModelCosts(Model model, double? costLimit)
    {
        if (costLimit is null || ModelInfoLookup.GetModelInfo(model)?.Cost is not null)
        {
            return;
        }

        throw new PrerequisiteError(
            $"cost_limit requires cost data for all models. Missing cost data for: {model.Name}. "
            + $"Use ModelInfoLookup.SetModelCost() or {ModelCostConfig.EnvironmentVariable} to configure pricing.");
    }

    private static Dictionary<string, ModelUsage> AggregateUsage(IEnumerable<EvalSample> samples)
    {
        var usage = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
        foreach (var sample in samples)
        {
            foreach (var (name, sampleUsage) in sample.ModelUsage)
            {
                usage[name] = usage.TryGetValue(name, out var existing) ? existing + sampleUsage : sampleUsage;
            }
        }

        return usage;
    }

    /// <summary>Port of the log file naming of <c>_eval/eval.py</c>: <c>&lt;local time&gt;_&lt;task&gt;_&lt;id&gt;.json</c> with a filename-safe task name.</summary>
    private static string LogPath(string logDir, string taskName, DateTimeOffset created)
    {
        var stamp = created.ToLocalTime().ToString("yyyy-MM-dd'T'HH-mm-ss", CultureInfo.InvariantCulture);
        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
        var name = new string(taskName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray());
        return Path.Combine(logDir, $"{stamp}_{name}_{suffix}.json");
    }
}

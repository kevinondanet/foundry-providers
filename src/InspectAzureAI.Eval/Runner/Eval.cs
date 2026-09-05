using System.Globalization;
using System.Security.Cryptography;
using InspectAzureAI.Eval.Concurrency;
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
        var samples = ResolveSamples(task.Dataset, options);
        var epochs = options.Epochs ?? task.Epochs?.Count ?? 1;
        var failOnError = options.FailOnError ?? task.FailOnError;
        var messageLimit = options.MessageLimit ?? task.MessageLimit;
        var tokenLimit = options.TokenLimit ?? task.TokenLimit;
        var timeLimit = options.TimeLimit ?? task.TimeLimit;
        var costLimit = options.CostLimit ?? task.CostLimit;
        if (options.ModelCostConfig is { } modelCostConfig)
        {
            ModelCostConfig.Apply(modelCostConfig);
        }

        ResolveModelCosts(model, costLimit);
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
                MessageLimit = messageLimit,
                TokenLimit = tokenLimit,
                TimeLimit = (int?)timeLimit?.TotalSeconds,
                CostLimit = costLimit,
                MaxSamples = options.MaxSamples,
                SandboxCleanup = options.Cleanup,
            },
        };

        var sandboxSpecs = samples.Select(sample => SandboxSetup.ResolveSpec(task.Sandbox, sample)).ToList();
        var providerSpecs = sandboxSpecs.OfType<SandboxSpec>().Distinct().ToList();
        var runner = new SampleRunner(task, model, scorerNames, messageLimit, tokenLimit, timeLimit, options.Cleanup, costLimit);
        var results = new SampleResult?[samples.Count * epochs];
        Exception? failure = null;
        var failureSync = new object();

        // one linked source: the caller's cancellation and a fail-on-error abort both stop the samples in flight
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var semaphore = SampleScheduler.CreateSampleSemaphore(options.MaxSamples, model.Config, model.AdaptiveConnections ?? AdaptiveConnections.FromConfigValue(model.Config.AdaptiveConnections), model.Api);
        try
        {
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

        var log = new EvalLog
        {
            Status = status,
            Eval = spec,
            Results = new EvalResults
            {
                TotalSamples = samples.Count * epochs,
                CompletedSamples = evalSamples.Count(sample => sample.Error is null),
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

        async Task RunSampleAsync(int index, Sample sample, SandboxSpec? sandbox, int epoch)
        {
            ConcurrencyLease lease;
            try
            {
                lease = await semaphore.AcquireAsync(abort.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // never started: Python logs nothing for samples still queued when the run stops
                return;
            }

            try
            {
                if (abort.IsCancellationRequested)
                {
                    return;
                }

                reporter?.SampleStarted(sample.Id!, epoch);
                reporter?.Stats(EvalRunStats.Capture(semaphore));
                var result = await runner.RunAsync(sample, sandbox, epoch, abort.Token).ConfigureAwait(false);
                results[index] = result;
                reporter?.SampleCompleted(result.Sample);
                reporter?.Stats(EvalRunStats.Capture(semaphore));
                if (result.Exception is { } ex && !result.Cancelled && failOnError)
                {
                    lock (failureSync)
                    {
                        failure ??= ex;
                    }

                    await abort.CancelAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                lease.Release();
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
    /// <c>slice_dataset</c> (<c>sample_id</c> filter, else the <c>limit</c> prefix; a zero limit selects everything).
    /// </summary>
    private static List<Sample> ResolveSamples(IDataset dataset, EvalOptions options)
    {
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

        if (options.SampleIds is { Count: > 0 } ids)
        {
            var wanted = ids.Select(IdText).ToHashSet(StringComparer.Ordinal);
            return samples.Where(sample => wanted.Contains(IdText(sample.Id))).ToList();
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

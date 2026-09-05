using InspectAzureAI.Eval.Model;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Concurrency;

/// <summary>
/// The shape shared by <see cref="ResizableLimiter"/> and <see cref="DynamicSampleLimiter"/> — the two sample
/// semaphores <c>create_sample_semaphore</c> can return — so the runner and a limits view read both uniformly.
/// </summary>
public interface ISampleLimiter
{
    /// <summary>Current capacity.</summary>
    int Limit { get; }

    /// <summary>Holders currently inside the limiter.</summary>
    int InUse { get; }

    ValueTask<ConcurrencyLease> AcquireAsync(CancellationToken cancellationToken = default);
}

/// <summary>Port of <c>model_concurrency_key</c> / <c>_connection_pool_key</c> in <c>model/_model.py</c>.</summary>
public static class ModelConcurrency
{
    /// <summary>
    /// Provider-namespaced connection-pool key: the api's <c>ConnectionKey()</c> prefixed with its type, so two
    /// providers serving the same model never share a pool even when their keys coincide.
    /// </summary>
    public static string ConnectionPoolKey(IModelApi api)
    {
        ArgumentNullException.ThrowIfNull(api);
        return $"{api.GetType().Name}:{ModelApiHooks.ConnectionKey(api)}";
    }

    /// <summary>
    /// The registry key of a model's generate-concurrency context — the single definition used by the model's
    /// connection pool and by <see cref="SampleScheduler.CreateSampleSemaphore"/>, so the two sides can't drift.
    /// </summary>
    public static string Key(IModelApi api) => $"Model{ConnectionPoolKey(api)}";
}

/// <summary>Port of <c>_eval/task/run.py</c> <c>create_sample_semaphore</c> and the constants it derives from.</summary>
public static class SampleScheduler
{
    /// <summary>Python <c>DEFAULT_MAX_CONNECTIONS</c>.</summary>
    public const int DefaultMaxConnections = 10;

    /// <summary>Python <c>DEFAULT_MAX_CONNECTIONS_BATCH</c> (a sentinel ceiling for batch mode).</summary>
    public const int DefaultMaxConnectionsBatch = 10000;

    /// <summary>
    /// Creates (or, for a registered <paramref name="taskId"/>, reuses) the task's sample-concurrency semaphore:
    /// an explicit <paramref name="maxSamples"/> is a user setpoint (a <see cref="ResizableLimiter"/>); otherwise
    /// the adaptive path follows the model's controller (a <see cref="DynamicSampleLimiter"/> scoped to the
    /// model's connection-pool key), and the static path defaults from <c>max_connections</c> — the config value,
    /// else the batch sentinel, else the api's <c>MaxConnections()</c>, else <see cref="DefaultMaxConnections"/> —
    /// so the connection pool always saturates. <paramref name="generateConfig"/> must be the model-composed
    /// config (the same composition the generate path resolves).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxSamples"/> is below 1.</exception>
    public static ISampleLimiter CreateSampleSemaphore(
        int? maxSamples,
        GenerateConfig generateConfig,
        AdaptiveConnections? adaptiveConnections = null,
        IModelApi? modelApi = null,
        string? taskId = null,
        bool batch = false)
    {
        ArgumentNullException.ThrowIfNull(generateConfig);
        if (taskId is not null && Concurrency.TaskSampleSemaphore(taskId) is { } existing)
        {
            return existing;
        }

        ISampleLimiter semaphore;
        if (maxSamples is { } explicitMax)
        {
            if (explicitMax < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxSamples), explicitMax, "max_samples must be >= 1");
            }

            semaphore = new ResizableLimiter(explicitMax);
        }
        else if (Concurrency.AdaptiveActive(adaptiveConnections, generateConfig.MaxConnections, batch))
        {
            semaphore = new DynamicSampleLimiter(
                (adaptiveConnections ?? AdaptiveConnections.Default).Resolve(),
                modelApi is not null ? ModelConcurrency.Key(modelApi) : "<no-model>");
        }
        else
        {
            var derived = generateConfig.MaxConnections
                ?? (batch ? DefaultMaxConnectionsBatch : modelApi?.MaxConnections() ?? DefaultMaxConnections);
            semaphore = new ResizableLimiter(derived);
        }

        if (taskId is not null)
        {
            Concurrency.RegisterTaskSampleSemaphore(taskId, semaphore);
        }

        return semaphore;
    }
}

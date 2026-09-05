using InspectAzureAI.Eval.Concurrency;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Runner.EvalSet;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of the arguments of <c>_eval/eval.py</c> <c>eval_retry()</c> this runner honours. Everything else a retry
/// needs — limits, epochs, sample selection, error policy, the model and its config, the model roles — is read
/// back from the log being retried; the members here are the retry-time overrides (null keeps the log's value).
/// Python resolves the task through its registry; this port has none, so the tasks are supplied through
/// <see cref="Tasks"/> (matched by name, then by task args) or <see cref="ResolveTask"/>.
/// </summary>
public sealed record EvalRetryOptions
{
    /// <summary>The tasks the logs were produced by, matched by <c>task</c> name and, when several share a name, by <see cref="EvalTask.TaskArgs"/>.</summary>
    public IReadOnlyList<EvalTask>? Tasks { get; init; }

    /// <summary>Resolves the task of a log directly (takes precedence over <see cref="Tasks"/>).</summary>
    public Func<EvalLog, EvalTask>? ResolveTask { get; init; }

    /// <summary>
    /// Port of the <c>get_model(model, config, base_url, **model_args)</c> call: creates the log's model from its
    /// spec (default: <c>FoundryModels.Create(spec.Model, modelArgs: spec.ModelArgs)</c>). The recorded generate
    /// config, with the overrides below applied, is layered onto the returned model.
    /// </summary>
    public Func<EvalSpec, Model>? ResolveModel { get; init; }

    /// <summary>Creates the models of the recorded roles by name (default <c>FoundryModels.Create(name)</c>); see <see cref="ModelRolesConfig.FromConfig"/>.</summary>
    public Func<string, Model>? ResolveRoleModel { get; init; }

    /// <summary>Port of <c>log_dir</c>; null writes the retried log next to the original (Python defaults to <c>./logs</c>).</summary>
    public string? LogDir { get; init; }

    public int? MaxSamples { get; init; }

    /// <summary>Port of <c>sandbox_cleanup</c>.</summary>
    public bool? SandboxCleanup { get; init; }

    public FailOnError? FailOnError { get; init; }

    public bool? ContinueOnFail { get; init; }

    public int? RetryOnError { get; init; }

    /// <summary>Port of <c>max_retries</c>: overrides the recorded generate config's value.</summary>
    public int? MaxRetries { get; init; }

    /// <summary>Port of <c>timeout</c> (seconds).</summary>
    public int? Timeout { get; init; }

    /// <summary>Port of <c>attempt_timeout</c> (seconds).</summary>
    public int? AttemptTimeout { get; init; }

    /// <summary>Port of <c>stream_idle_timeout</c> (seconds).</summary>
    public int? StreamIdleTimeout { get; init; }

    public int? MaxConnections { get; init; }

    public AdaptiveConnections? AdaptiveConnections { get; init; }

    public IEvalReporter? Reporter { get; init; }
}

/// <summary>
/// Port of <c>_eval/eval.py</c> <c>eval_retry()</c> / <c>eval_retry_async()</c>: re-runs previously logged tasks,
/// one after another, each as a <c>PreviousTask</c> — the log's completed samples are reused, its errored samples
/// re-run with their error history carried into <c>error_retries</c>, its model usage rolled forward, and its
/// <c>task_id</c> kept so the new log supersedes it in an eval set. As in Python the retried log carries no
/// <c>eval_set_id</c>.
/// </summary>
public static class EvalRetry
{
    /// <summary>Retries the logs at <paramref name="logPaths"/> (JSON logs, read whole).</summary>
    public static async Task<IReadOnlyList<EvalLog>> RunAsync(IReadOnlyList<string> logPaths, EvalRetryOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logPaths);
        ArgumentNullException.ThrowIfNull(options);
        var logs = new List<EvalLog>(logPaths.Count);
        foreach (var path in logPaths)
        {
            logs.Add(await EvalLogWriter.ReadAsync(path, cancellationToken).ConfigureAwait(false));
        }

        return await RunAsync(logs, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Retries <paramref name="logs"/> in turn and returns one new log per input, in order.</summary>
    /// <exception cref="PrerequisiteError">A log's task is not among the supplied tasks.</exception>
    public static async Task<IReadOnlyList<EvalLog>> RunAsync(IReadOnlyList<EvalLog> logs, EvalRetryOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logs);
        ArgumentNullException.ThrowIfNull(options);
        if (options.ResolveTask is null && options.Tasks is null)
        {
            throw new ArgumentException("EvalRetryOptions needs Tasks or ResolveTask to find the task of a log.", nameof(options));
        }

        var results = new List<EvalLog>(logs.Count);
        foreach (var log in logs)
        {
            var task = ResolveTask(log, options);
            results.Add(await Eval.RunAsync(task, RetryOptions(log, task, options), cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>Port of the per-log parameter collection of <c>eval_retry_async</c>: the log's own settings with the retry-time overrides applied.</summary>
    internal static EvalOptions RetryOptions(EvalLog log, EvalTask task, EvalRetryOptions options)
    {
        var spec = log.Eval;
        var config = spec.ModelGenerateConfig with
        {
            MaxRetries = options.MaxRetries ?? spec.ModelGenerateConfig.MaxRetries,
            Timeout = options.Timeout ?? spec.ModelGenerateConfig.Timeout,
            AttemptTimeout = options.AttemptTimeout ?? spec.ModelGenerateConfig.AttemptTimeout,
            StreamIdleTimeout = options.StreamIdleTimeout ?? spec.ModelGenerateConfig.StreamIdleTimeout,
            MaxConnections = options.MaxConnections ?? spec.ModelGenerateConfig.MaxConnections,
        };
        var resolveModel = options.ResolveModel ?? (recorded => FoundryModels.Create(recorded.Model, modelArgs: recorded.ModelArgs));
        var model = resolveModel(spec).WithConfig(config);
        var reporter = options.Reporter;
        return new EvalOptions
        {
            Model = model,
            ModelRoles = ModelRolesConfig.FromConfig(spec.ModelRoles, options.ResolveRoleModel),
            Limit = spec.Config.Limit,
            SampleIds = spec.Config.SampleId,
            Epochs = spec.Config.Epochs,
            MaxSamples = options.MaxSamples ?? spec.Config.MaxSamples,
            AdaptiveConnections = options.AdaptiveConnections,
            FailOnError = options.FailOnError ?? spec.Config.FailOnError,
            ContinueOnFail = options.ContinueOnFail ?? spec.Config.ContinueOnFail,
            RetryOnError = options.RetryOnError ?? spec.Config.RetryOnError,
            LogDir = options.LogDir ?? LogDirOf(log),
            Cleanup = options.SandboxCleanup ?? spec.Config.SandboxCleanup ?? true,
            MessageLimit = spec.Config.MessageLimit,
            TokenLimit = spec.Config.TokenLimit,
            TurnLimit = spec.Config.TurnLimit,
            TimeLimit = spec.Config.TimeLimit is { } time ? TimeSpan.FromSeconds(time) : null,
            WorkingLimit = spec.Config.WorkingLimit is { } working ? TimeSpan.FromSeconds(working) : null,
            CostLimit = spec.Config.CostLimit,
            Reporter = reporter,
            TaskId = spec.TaskId,
            SampleSource = EvalSampleSource.FromLog(log, task.Dataset, reporter is null ? null : reporter.Message),
            InitialModelUsage = log.Stats.ModelUsage is { Count: > 0 } usage ? usage : null,
        };
    }

    /// <summary>The log's task: <see cref="EvalRetryOptions.ResolveTask"/>, else the supplied task with its name and (when the name is shared) its args hash.</summary>
    private static EvalTask ResolveTask(EvalLog log, EvalRetryOptions options)
    {
        if (options.ResolveTask is { } resolve)
        {
            return resolve(log);
        }

        var name = log.Eval.Task;
        var named = options.Tasks!.Where(task => task.Name == name).ToList();
        if (named.Count == 0)
        {
            throw new PrerequisiteError($"Task '{name}' not found.");
        }

        if (named.Count == 1)
        {
            return named[0];
        }

        var argsHash = TaskIdentifier.TaskArgsHash(log.Eval.TaskArgsPassed ?? log.Eval.TaskArgs);
        return named.FirstOrDefault(task => TaskIdentifier.TaskArgsHash(task.TaskArgs ?? new Dictionary<string, object?>(StringComparer.Ordinal)) == argsHash)
            ?? throw new PrerequisiteError($"Task '{name}' not found with the task args of log '{log.Location}'.");
    }

    private static string LogDirOf(EvalLog log) =>
        log.Location is { Length: > 0 } location && Path.GetDirectoryName(location) is { Length: > 0 } directory ? directory : "logs";
}

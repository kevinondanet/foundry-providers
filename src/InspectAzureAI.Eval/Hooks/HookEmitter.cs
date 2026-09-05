using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Hooks;

/// <summary>
/// Port of the <c>emit_*</c> functions and <c>_emit_to_all</c> of <c>hooks/_hooks.py</c>: builds each payload and
/// delivers it to every enabled hook in turn. The runner and <c>Model</c> call these; they are public so another
/// driver (an eval set, a custom runner) can emit the same events. Without an explicit <c>hooks</c> list, the
/// run's hooks apply inside a sample (registry plus <c>EvalOptions.Hooks</c>) and the registry alone outside one.
/// </summary>
public static class HookEmitter
{
    /// <summary>Port of <c>get_all_hooks()</c> as seen from the current async flow: the run's hooks inside a sample, else <see cref="HookRegistry.All"/>.</summary>
    public static IReadOnlyList<Hooks> ActiveHooks => HookContext.Current?.Hooks ?? HookRegistry.All;

    /// <summary>Port of <c>has_api_key_override()</c> over <see cref="ActiveHooks"/> (the retry loop's auth-failure rule).</summary>
    public static bool HasApiKeyOverride => ActiveHooks.Any(HookRegistry.OverridesApiKey);

    /// <summary>Port of <c>emit_eval_set_start</c>: the static entry point an eval set driver calls before its first run.</summary>
    public static Task EmitEvalSetStartAsync(string evalSetId, string logDir, IReadOnlyList<Hooks>? hooks = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evalSetId);
        ArgumentNullException.ThrowIfNull(logDir);
        var data = new EvalSetStart(evalSetId, logDir);
        return EmitToAllAsync(hooks ?? ActiveHooks, (hook, ct) => hook.OnEvalSetStartAsync(data, ct), cancellationToken);
    }

    /// <summary>Port of <c>emit_eval_set_end</c>: the static entry point an eval set driver calls after its last run.</summary>
    public static Task EmitEvalSetEndAsync(string evalSetId, string logDir, IReadOnlyList<Hooks>? hooks = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evalSetId);
        ArgumentNullException.ThrowIfNull(logDir);
        var data = new EvalSetEnd(evalSetId, logDir);
        return EmitToAllAsync(hooks ?? ActiveHooks, (hook, ct) => hook.OnEvalSetEndAsync(data, ct), cancellationToken);
    }

    /// <summary>Port of <c>emit_run_start</c>.</summary>
    public static Task EmitRunStartAsync(string? evalSetId, string runId, IReadOnlyList<string> taskNames, IReadOnlyList<Hooks>? hooks = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(taskNames);
        var data = new RunStart(evalSetId, runId, taskNames);
        return EmitToAllAsync(hooks ?? ActiveHooks, (hook, ct) => hook.OnRunStartAsync(data, ct), cancellationToken);
    }

    /// <summary>Port of <c>emit_run_end</c>.</summary>
    public static Task EmitRunEndAsync(string? evalSetId, string runId, IReadOnlyList<EvalLog> logs, Exception? exception = null, IReadOnlyList<Hooks>? hooks = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(logs);
        var data = new RunEnd(evalSetId, runId, exception, logs);
        return EmitToAllAsync(hooks ?? ActiveHooks, (hook, ct) => hook.OnRunEndAsync(data, ct), cancellationToken);
    }

    /// <summary>Port of <c>emit_task_start</c>: the ids come from <paramref name="spec"/> (Python's <c>logger.eval</c>).</summary>
    public static Task EmitTaskStartAsync(EvalSpec spec, EvalPlan plan, IReadOnlyList<Hooks>? hooks = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(plan);
        var data = new TaskStart(spec.EvalSetId, spec.RunId, spec.EvalId, spec, plan);
        return EmitToAllAsync(hooks ?? ActiveHooks, (hook, ct) => hook.OnTaskStartAsync(data, ct), cancellationToken);
    }

    /// <summary>Port of <c>emit_task_end</c>.</summary>
    public static Task EmitTaskEndAsync(EvalLog log, IReadOnlyList<Hooks>? hooks = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        var data = new TaskEnd(log.Eval.EvalSetId, log.Eval.RunId, log.Eval.EvalId, log);
        return EmitToAllAsync(hooks ?? ActiveHooks, (hook, ct) => hook.OnTaskEndAsync(data, ct), cancellationToken);
    }

    /// <summary>Port of <c>emit_sample_init</c>.</summary>
    public static Task EmitSampleInitAsync(string? evalSetId, string runId, string evalId, string sampleId, EvalSampleSummary summary, IReadOnlyList<Hooks>? hooks = null, CancellationToken cancellationToken = default)
    {
        var data = new SampleInit(evalSetId, runId, evalId, sampleId, summary);
        return EmitToAllAsync(hooks ?? ActiveHooks, (hook, ct) => hook.OnSampleInitAsync(data, ct), cancellationToken);
    }

    /// <summary>Port of <c>emit_sample_start</c>.</summary>
    public static Task EmitSampleStartAsync(string? evalSetId, string runId, string evalId, string sampleId, EvalSampleSummary summary, IReadOnlyList<Hooks>? hooks = null, CancellationToken cancellationToken = default)
    {
        var data = new SampleStart(evalSetId, runId, evalId, sampleId, summary);
        return EmitToAllAsync(hooks ?? ActiveHooks, (hook, ct) => hook.OnSampleStartAsync(data, ct), cancellationToken);
    }

    /// <summary>
    /// Delivers one transcript event to the hooks now. The runner queues events instead (Python's
    /// <c>emit_sample_event</c> + background emitter) and calls this from the emitter; a pending event is skipped.
    /// </summary>
    public static Task EmitSampleEventAsync(string? evalSetId, string runId, string evalId, string sampleId, TranscriptEvent e, IReadOnlyList<Hooks>? hooks = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Pending == true)
        {
            return Task.CompletedTask;
        }

        return EmitSampleEventAsync(new SampleEvent(evalSetId, runId, evalId, sampleId, e), hooks, cancellationToken);
    }

    internal static Task EmitSampleEventAsync(SampleEvent data, IReadOnlyList<Hooks>? hooks, CancellationToken cancellationToken) =>
        EmitToAllAsync(hooks ?? ActiveHooks, (hook, ct) => hook.OnSampleEventAsync(data, ct), cancellationToken);

    /// <summary>Port of <c>emit_sample_end</c>.</summary>
    public static Task EmitSampleEndAsync(string? evalSetId, string runId, string evalId, string sampleId, EvalSample sample, IReadOnlyList<Hooks>? hooks = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var data = new SampleEnd(evalSetId, runId, evalId, sampleId, sample);
        return EmitToAllAsync(hooks ?? ActiveHooks, (hook, ct) => hook.OnSampleEndAsync(data, ct), cancellationToken);
    }

    /// <summary>Port of <c>emit_sample_attempt_start</c>.</summary>
    public static Task EmitSampleAttemptStartAsync(string? evalSetId, string runId, string evalId, string sampleId, EvalSampleSummary summary, int attempt, IReadOnlyList<Hooks>? hooks = null, CancellationToken cancellationToken = default)
    {
        var data = new SampleAttemptStart(evalSetId, runId, evalId, sampleId, summary, attempt);
        return EmitToAllAsync(hooks ?? ActiveHooks, (hook, ct) => hook.OnSampleAttemptStartAsync(data, ct), cancellationToken);
    }

    /// <summary>Port of <c>emit_sample_attempt_end</c>.</summary>
    public static Task EmitSampleAttemptEndAsync(string? evalSetId, string runId, string evalId, string sampleId, EvalSampleSummary summary, int attempt, EvalError? error, bool willRetry, IReadOnlyList<Hooks>? hooks = null, CancellationToken cancellationToken = default)
    {
        var data = new SampleAttemptEnd(evalSetId, runId, evalId, sampleId, summary, attempt, error, willRetry);
        return EmitToAllAsync(hooks ?? ActiveHooks, (hook, ct) => hook.OnSampleAttemptEndAsync(data, ct), cancellationToken);
    }

    /// <summary>Port of <c>emit_sample_scoring</c>.</summary>
    public static Task EmitSampleScoringAsync(string? evalSetId, string runId, string evalId, string sampleId, IReadOnlyList<Hooks>? hooks = null, CancellationToken cancellationToken = default)
    {
        var data = new SampleScoring(evalSetId, runId, evalId, sampleId);
        return EmitToAllAsync(hooks ?? ActiveHooks, (hook, ct) => hook.OnSampleScoringAsync(data, ct), cancellationToken);
    }

    /// <summary>Port of <c>emit_before_model_generate</c>: the eval ids come from the ambient sample (null outside one).</summary>
    public static Task EmitBeforeModelGenerateAsync(
        string modelName,
        IReadOnlyList<ChatMessage> input,
        IReadOnlyList<ToolInfo> tools,
        ToolChoice toolChoice,
        GenerateConfig config,
        CacheMode? cache,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(toolChoice);
        ArgumentNullException.ThrowIfNull(config);
        var active = HookContext.Current;
        var data = new BeforeModelGenerate(modelName, input, tools, toolChoice, config, cache)
        {
            EvalSetId = active?.EvalSetId,
            RunId = active?.RunId,
            EvalId = active?.EvalId,
            SampleId = active?.SampleId,
            TaskName = active?.TaskName,
        };
        return EmitToAllAsync(ActiveHooks, (hook, ct) => hook.OnBeforeModelGenerateAsync(data, ct), cancellationToken);
    }

    /// <summary>Port of <c>emit_model_usage</c>: the eval ids come from the ambient sample, <paramref name="retries"/> from the model event (Python reads it from the active event).</summary>
    public static Task EmitModelUsageAsync(string modelName, ModelUsage usage, double callDuration, int retries = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        ArgumentNullException.ThrowIfNull(usage);
        var active = HookContext.Current;
        var data = new ModelUsageData(modelName, usage, callDuration)
        {
            EvalSetId = active?.EvalSetId,
            RunId = active?.RunId,
            EvalId = active?.EvalId,
            TaskName = active?.TaskName,
            Retries = retries,
        };
        return EmitToAllAsync(ActiveHooks, (hook, ct) => hook.OnModelUsageAsync(data, ct), cancellationToken);
    }

    /// <summary>Port of <c>emit_model_cache_usage</c>.</summary>
    public static Task EmitModelCacheUsageAsync(string modelName, ModelUsage usage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        ArgumentNullException.ThrowIfNull(usage);
        var data = new ModelCacheUsageData(modelName, usage);
        return EmitToAllAsync(ActiveHooks, (hook, ct) => hook.OnModelCacheUsageAsync(data, ct), cancellationToken);
    }

    /// <summary>Port of <c>emit_model_retry</c>: the eval ids come from the ambient sample; <paramref name="error"/> is the cause's type and status (<see cref="RetryErrorInfo.Of"/>).</summary>
    public static Task EmitModelRetryAsync(string modelName, int attempt, double waitTime, RetryErrorInfo error = default, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        var active = HookContext.Current;
        var data = new ModelRetry(modelName, attempt, waitTime)
        {
            EvalSetId = active?.EvalSetId,
            RunId = active?.RunId,
            EvalId = active?.EvalId,
            SampleId = active?.SampleId,
            TaskName = active?.TaskName,
            ExceptionType = error.ExceptionType,
            StatusCode = error.StatusCode,
        };
        return EmitToAllAsync(ActiveHooks, (hook, ct) => hook.OnModelRetryAsync(data, ct), cancellationToken);
    }

    /// <summary>
    /// Port of <c>_emit_to_all</c>: calls <paramref name="callback"/> for each enabled hook in order. A hook that
    /// throws is logged (<c>Exception calling hook '&lt;type&gt;': &lt;message&gt;</c>) and the remaining hooks still run;
    /// <see cref="LimitExceededException"/> propagates so limits can be enforced via hooks, and so does an
    /// <see cref="OperationCanceledException"/> once <paramref name="cancellationToken"/> is cancelled (Python's
    /// <c>CancelledError</c> is likewise not swallowed); a hook's own timeout or cancellation with the run still
    /// live is a hook failure like any other.
    /// </summary>
    public static async Task EmitToAllAsync(IReadOnlyList<Hooks> hooks, Func<Hooks, CancellationToken, Task> callback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(callback);
        foreach (var hook in hooks)
        {
            if (!hook.Enabled)
            {
                continue;
            }

            try
            {
                await callback(hook, cancellationToken).ConfigureAwait(false);
            }
            catch (LimitExceededException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ProviderLogger.Warning($"Exception calling hook '{hook.GetType().Name}': {ex.Message}");
            }
        }
    }
}

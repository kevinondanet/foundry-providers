namespace InspectAzureAI.Eval.Hooks;

/// <summary>
/// Port of <c>hooks/_hooks.py</c> <c>Hooks</c>: the base class for lifecycle hook subscribers. Every callback is a
/// no-op; override the ones you need. Register an instance for the process with
/// <see cref="HookRegistry.Register"/> (Python's <c>@hooks</c> decorator) or for one run with
/// <c>EvalOptions.Hooks</c>.
/// <para>
/// Whenever a hook is called it is wrapped in a try/catch: a hook failure is logged as a warning
/// (<c>ProviderLogger</c>) and does not affect the eval. Two exceptions propagate instead:
/// <c>LimitExceededException</c>, so limits can be enforced via hooks, and
/// <see cref="OperationCanceledException"/>.
/// </para>
/// <para>
/// <b>Hook lifecycle.</b> One instance serves every eval set, run, task, sample and epoch for as long as it is
/// registered; there is no teardown event, so do per-run cleanup in <see cref="OnRunEndAsync"/> or
/// <see cref="OnEvalSetEndAsync"/>. Because the instance is shared, state stored on it by one sample is visible
/// to all the others: key per-sample state by <c>data.SampleId</c> and remove it in <see cref="OnSampleEndAsync"/>.
/// Samples run concurrently, so a call for one sample can begin while a call for another is in flight, and the
/// sample event callbacks run on a background task alongside the sample's own lifecycle callbacks. Within a
/// single sample, <see cref="OnSampleEventAsync"/> calls are serialized.
/// </para>
/// <para>
/// <b>Ownership of hook event data.</b> The events passed via <see cref="OnSampleEventAsync"/> and the
/// <c>EvalSample</c> passed via <see cref="OnSampleEndAsync"/> are owned by the framework, which serializes them
/// after the hook returns: read them, retain references if you like, but never mutate them in place.
/// </para>
/// </summary>
public abstract class Hooks
{
    /// <summary>
    /// Port of <c>enabled()</c>: whether the hook should be enabled (default true). Override to e.g. check an
    /// environment variable. Read before every callback, so keep it cheap.
    /// </summary>
    public virtual bool Enabled => true;

    /// <summary>
    /// Port of <c>on_eval_set_start</c>. An eval set is an invocation of an eval set for a log directory; the
    /// <c>EvalSetId</c> is stable across multiple invocations for the same directory.
    /// </summary>
    public virtual Task OnEvalSetStartAsync(EvalSetStart data, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Port of <c>on_eval_set_end</c>.</summary>
    public virtual Task OnEvalSetEndAsync(EvalSetEnd data, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Port of <c>on_run_start</c>. A run is a single invocation of <c>Eval.RunAsync</c>, which may contain many
    /// samples and epochs.
    /// </summary>
    public virtual Task OnRunStartAsync(RunStart data, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Port of <c>on_run_end</c>. Fires even when the run failed before <see cref="OnRunStartAsync"/> (the exception is in the data).</summary>
    public virtual Task OnRunEndAsync(RunEnd data, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Port of <c>on_task_start</c>.</summary>
    public virtual Task OnTaskStartAsync(TaskStart data, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Port of <c>on_task_end</c>: fires once the task's log has been written, whatever its status.</summary>
    public virtual Task OnTaskEndAsync(TaskEnd data, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Port of <c>on_sample_init</c>: called when a sample has been scheduled and is about to begin
    /// initialization, before sandbox environments are created (so it can gate sandbox provisioning). Not called
    /// again when the sample errors and retries; called once per epoch.
    /// </summary>
    public virtual Task OnSampleInitAsync(SampleInit data, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Port of <c>on_sample_start</c>: called when a sample is about to start (sandboxes ready, solvers about to
    /// run). Not called again when the sample errors and retries; called once per epoch.
    /// </summary>
    public virtual Task OnSampleStartAsync(SampleStart data, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Port of <c>on_sample_event</c>: called for each transcript event recorded between sample start and the end
    /// of scoring, from a background emitter that is drained before <see cref="OnSampleEndAsync"/>. Pending
    /// events are not delivered.
    /// </summary>
    public virtual Task OnSampleEventAsync(SampleEvent data, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Port of <c>on_sample_end</c>: called when a sample has either completed successfully, or has errored with no
    /// retries remaining (or was cancelled). Called once per epoch.
    /// </summary>
    public virtual Task OnSampleEndAsync(SampleEnd data, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Port of <c>on_before_model_generate</c>: called before every provider attempt of a generate call, ahead of
    /// the cache lookup — so it fires again on each HTTP retry. The payload references the lists the call uses (as
    /// Python's does); treat it as read-only — in-place mutation is unsupported and its effect on the call and the
    /// cache key is undefined.
    /// </summary>
    public virtual Task OnBeforeModelGenerateAsync(BeforeModelGenerate data, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Port of <c>on_model_retry</c>: called before a model call is retried after a transient failure — once per
    /// retry (not for the initial attempt), before the backoff sleep. <c>data.WaitTime</c> is the upcoming
    /// backoff, useful for surfacing time spent in rate limiting.
    /// </summary>
    public virtual Task OnModelRetryAsync(ModelRetry data, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Port of <c>on_sample_attempt_start</c>: fired at the beginning of every attempt, including the first and every error retry.</summary>
    public virtual Task OnSampleAttemptStartAsync(SampleAttemptStart data, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Port of <c>on_sample_attempt_end</c>: fired at the end of every attempt that started, including the last.</summary>
    public virtual Task OnSampleAttemptEndAsync(SampleAttemptEnd data, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Port of <c>on_model_usage</c>: called when a generate call completes successfully without hitting Inspect's
    /// local cache (provider-side caching still counts). Not called for a local cache hit.
    /// </summary>
    public virtual Task OnModelUsageAsync(ModelUsageData data, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Port of <c>on_model_cache_usage</c>: called when a generate call is served from Inspect's local cache.</summary>
    public virtual Task OnModelCacheUsageAsync(ModelCacheUsageData data, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Port of <c>on_sample_scoring</c>: called after the solvers and before the scorers on every attempt, so hooks
    /// can demarcate the end of solver execution and the start of scoring.
    /// </summary>
    public virtual Task OnSampleScoringAsync(SampleScoring data, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Port of <c>override_api_key</c>: optionally override an API key. Return the value to use in place of the
    /// original, or null to keep it. Consulted through <see cref="HookRegistry.OverrideApiKey"/> and
    /// <see cref="ApiKeyOverrides.Apply"/>; see <c>docs/ports/hooks.md</c> for why the Foundry providers
    /// (Entra ID only) never call it.
    /// </summary>
    public virtual string? OverrideApiKey(ApiKeyOverride data) => null;
}

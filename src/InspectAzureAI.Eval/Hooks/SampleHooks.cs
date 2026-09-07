using System.Threading.Channels;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Hooks;

/// <summary>
/// The sample-level emissions of <c>_eval/task/run.py</c> <c>task_run_sample_attempt</c> for one attempt:
/// <c>emit_sample_init</c> (first attempt, before sandboxes), <c>emit_sample_start</c> (first attempt) and
/// <c>emit_sample_attempt_start</c> (every attempt) just before the solvers, the background sample event emitter
/// (<c>start_sample_event_emitter</c> / <c>emit_sample_event</c> / <c>drain_sample_events</c>),
/// <c>emit_sample_scoring</c>, and <c>emit_sample_attempt_end</c> / <c>emit_sample_end</c>. Also installs the
/// <see cref="HookContext"/> the model-level hooks read. A detached instance (no run) emits nothing.
/// </summary>
internal sealed class SampleHooks
{
    /// <summary>Port of the <c>move_on_after(5)</c> in <c>drain_sample_events</c>.</summary>
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private readonly HookContext? _context;

    private readonly EvalSampleSummary? _summary;

    private readonly int _attempt;

    private readonly bool _isFirstAttempt;

    private Channel<SampleEvent>? _events;

    private Task? _emitter;

    private SampleHooks()
    {
    }

    internal SampleHooks(HookRun run, EvalSpec spec, Sample sample, TaskState state, int attempt, bool isFirstAttempt)
    {
        _context = new HookContext(run.EvalSetId, run.RunId, spec.EvalId, spec.Task, state.Uuid, run.Hooks);
        _summary = MakeSampleSummary(sample, state);
        _attempt = attempt;
        _isFirstAttempt = isFirstAttempt;
    }

    /// <summary>Whether <c>emit_sample_attempt_start</c> fired (Python's <c>attempt_started</c>), which gates the attempt end.</summary>
    public bool AttemptStarted { get; private set; }

    /// <summary>The attempt's emitter, or a detached one that emits nothing when the runner has no <see cref="HookRun"/>.</summary>
    public static SampleHooks Create(HookRun? run, Sample sample, TaskState state, int attempt, bool isFirstAttempt) =>
        run is null ? new SampleHooks() : run.Sample(sample, state, attempt, isFirstAttempt);

    /// <summary>Installs the <see cref="HookContext"/> for the attempt (dispose at its end).</summary>
    public IDisposable Begin() => _context is null ? DetachedScope.Instance : HookContext.Begin(_context);

    /// <summary>Port of the <c>emit_sample_init</c> site: before sandbox creation, on the first attempt only.</summary>
    public Task InitAsync(CancellationToken cancellationToken)
    {
        if (_context is not { } context || !_isFirstAttempt)
        {
            return Task.CompletedTask;
        }

        return HookEmitter.EmitSampleInitAsync(context.EvalSetId, context.RunId, context.EvalId, context.SampleId, _summary!, context.Hooks, cancellationToken);
    }

    /// <summary>
    /// Port of the <c>emit_sample_start</c> (first attempt) and <c>emit_sample_attempt_start</c> sites, then
    /// <c>start_sample_event_emitter</c>: events recorded from here on are queued for the sample event hooks.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_context is not { } context)
        {
            return;
        }

        if (_isFirstAttempt)
        {
            await HookEmitter.EmitSampleStartAsync(context.EvalSetId, context.RunId, context.EvalId, context.SampleId, _summary!, context.Hooks, cancellationToken).ConfigureAwait(false);
        }

        await HookEmitter.EmitSampleAttemptStartAsync(context.EvalSetId, context.RunId, context.EvalId, context.SampleId, _summary!, _attempt, context.Hooks, cancellationToken).ConfigureAwait(false);
        AttemptStarted = true;
        StartEventEmitter(context);
    }

    /// <summary>Port of <c>emit_sample_event</c>: queues a completed event for the background emitter (dropped when it is not running, or the event is pending).</summary>
    public void EventLogger(TranscriptEvent e)
    {
        if (_events is null || _context is not { } context || e.Pending == true)
        {
            return;
        }

        _events.Writer.TryWrite(new SampleEvent(context.EvalSetId, context.RunId, context.EvalId, context.SampleId, e));
    }

    /// <summary>Port of the <c>emit_sample_scoring</c> site: after the solvers on every attempt, shielded from cancellation as in Python.</summary>
    public Task ScoringAsync()
    {
        if (_context is not { } context)
        {
            return Task.CompletedTask;
        }

        return HookEmitter.EmitSampleScoringAsync(context.EvalSetId, context.RunId, context.EvalId, context.SampleId, context.Hooks, CancellationToken.None);
    }

    /// <summary>
    /// Port of <c>drain_sample_events</c>: closes the queue, waits up to <see cref="DrainTimeout"/> for the emitter
    /// (warning on timeout), delivers whatever it did not get to, and stops accepting events. Must run before
    /// <see cref="EndAsync"/> so every queued event reaches the hooks before the sample end.
    /// </summary>
    public async Task DrainAsync()
    {
        if (_events is not { } events)
        {
            return;
        }

        try
        {
            events.Writer.TryComplete();
            if (_emitter is { } emitter)
            {
                try
                {
                    await emitter.WaitAsync(DrainTimeout).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    ProviderLogger.Warning("Timed out waiting for sample event emitter to drain");
                }
            }

            while (events.Reader.TryRead(out var data))
            {
                await DeliverAsync(data).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            ProviderLogger.Warning($"Exception draining sample events: {ex.Message}");
        }
        finally
        {
            _events = null;
            _emitter = null;
        }
    }

    /// <summary>
    /// Port of <c>emit_attempt_end</c> (only when the attempt started) followed, unless the sample will be retried,
    /// by <c>emit_sample_end</c>. Not cancellable: these are completion notifications.
    /// </summary>
    public async Task EndAsync(EvalSample sample, EvalError? error, bool willRetry)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (_context is not { } context)
        {
            return;
        }

        if (AttemptStarted)
        {
            await HookEmitter.EmitSampleAttemptEndAsync(context.EvalSetId, context.RunId, context.EvalId, context.SampleId, _summary!, _attempt, error, willRetry, context.Hooks, CancellationToken.None).ConfigureAwait(false);
        }

        if (!willRetry)
        {
            await HookEmitter.EmitSampleEndAsync(context.EvalSetId, context.RunId, context.EvalId, context.SampleId, sample, context.Hooks, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Port of <c>make_sample_summary</c> (with <c>EvalSampleSummary.thin_data</c> applied, as the pydantic validator does).</summary>
    private static EvalSampleSummary MakeSampleSummary(Sample sample, TaskState state) => new()
    {
        Id = sample.Id ?? state.SampleId,
        Epoch = state.Epoch,
        Uuid = state.Uuid,
        Input = LogThinning.ThinInput(sample.Input),
        Choices = sample.Choices,
        Target = LogThinning.ThinTarget(sample.Target),
        Metadata = LogThinning.ThinMetadata(sample.Metadata ?? new Dictionary<string, object?>(StringComparer.Ordinal)),
    };

    /// <summary>Port of <c>start_sample_event_emitter</c>: an unbounded queue with one consumer task (skipped when no hook could receive anything).</summary>
    private void StartEventEmitter(HookContext context)
    {
        if (context.Hooks.Count == 0)
        {
            return;
        }

        _events = Channel.CreateUnbounded<SampleEvent>(new UnboundedChannelOptions { SingleReader = true });
        _emitter = EmitLoopAsync(_events.Reader);
    }

    private async Task EmitLoopAsync(ChannelReader<SampleEvent> reader)
    {
        await foreach (var data in reader.ReadAllAsync().ConfigureAwait(false))
        {
            await DeliverAsync(data).ConfigureAwait(false);
        }
    }

    /// <summary>Port of the emitter loop body: <c>_emit_to_all</c> with any escaping exception (a hook's limit error) logged rather than raised, since nothing awaits the loop.</summary>
    private async Task DeliverAsync(SampleEvent data)
    {
        try
        {
            await HookEmitter.EmitSampleEventAsync(data, _context!.Hooks, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ProviderLogger.Warning($"Exception in sample event emitter: {ex.Message}");
        }
    }

    private sealed class DetachedScope : IDisposable
    {
        public static readonly DetachedScope Instance = new();

        public void Dispose()
        {
        }
    }
}

using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Provider.Core;

/// <summary>Base stream event (port of the <c>StreamEvent</c> union in <c>src/inspect_ai/model/_stream.py</c>).</summary>
public abstract record StreamEvent
{
    public abstract string Type { get; }
}

/// <summary>Text delta (port of <c>StreamTextEvent</c>).</summary>
public sealed record StreamTextEvent(string Text) : StreamEvent
{
    public override string Type => "text";
}

/// <summary>Reasoning delta (port of <c>StreamReasoningEvent</c>): a fragment of <c>reasoning_content</c> on the model-inference route or of a <c>thinking</c> block on the Anthropic route.</summary>
public sealed record StreamReasoningEvent(string Reasoning) : StreamEvent
{
    public override string Type => "reasoning";
}

/// <summary>
/// Tool call delta (port of <c>StreamToolCallEvent</c>). <see cref="Arguments"/> is the argument
/// fragment for this delta only; consumers append fragments with the same id.
/// </summary>
public sealed record StreamToolCallEvent(string? Id = null, string? Function = null, string Arguments = "") : StreamEvent
{
    public override string Type => "tool_call";
}

/// <summary>Emitted by the framework before a retry attempt (port of <c>StreamRetryEvent</c>).</summary>
public sealed record StreamRetryEvent(int Attempt) : StreamEvent
{
    public override string Type => "retry";
}

/// <summary>Async callback receiving stream events (port of <c>StreamHandler</c>).</summary>
public delegate Task StreamHandler(StreamEvent streamEvent);

/// <summary>
/// Minimal port of <c>ModelStreamObserver</c> and the module-level <c>model_stream_requested()</c> /
/// <c>report_model_stream_*</c> functions in <c>src/inspect_ai/model/_stream.py</c>. The observer is
/// ambient (an <see cref="AsyncLocal{T}"/> installed with <see cref="Install"/>), which is how the
/// provider learns that the caller passed <c>on_stream</c>. Stall scopes (<c>stream_idle_timeout</c>)
/// are out of scope for the sample, so <see cref="ModelStreamRequested"/> is true only when a handler
/// is attached.
/// </summary>
public sealed class ModelStreamObserver
{
    private static readonly AsyncLocal<ModelStreamObserver?> CurrentObserver = new();

    /// <summary>Test hook standing in for monkeypatching <c>report_model_stream_delta</c>.</summary>
    internal static Func<StreamEvent, Task>? DeltaInterceptor { get; set; }

    public ModelStreamObserver(string model, StreamHandler? onStream)
    {
        Model = model;
        OnStream = onStream;
    }

    /// <summary>Model name used in diagnostics.</summary>
    public string Model { get; }

    /// <summary>The caller's handler; detached (set to null) after it throws.</summary>
    public StreamHandler? OnStream { get; private set; }

    /// <summary>Cumulative output tokens reported by the provider for the current stream.</summary>
    public int? TokensCurrent { get; private set; }

    /// <summary>Number of progress heartbeats received (usage chunks and bare heartbeats).</summary>
    public int ProgressReports { get; private set; }

    /// <summary>Whether at least one delta was delivered to the handler.</summary>
    public bool Delivered { get; private set; }

    /// <summary>Whether <c>stream_started</c> was reported.</summary>
    public bool Started { get; private set; }

    /// <summary>The ambient observer for this async context, if any.</summary>
    public static ModelStreamObserver? Current => CurrentObserver.Value;

    /// <summary>Port of the <c>model_stream_observer()</c> context manager.</summary>
    public static IDisposable Install(ModelStreamObserver observer)
    {
        var previous = CurrentObserver.Value;
        CurrentObserver.Value = observer;
        return new Restore(previous);
    }

    /// <summary>Port of <c>model_stream_requested()</c>.</summary>
    public static bool ModelStreamRequested() => CurrentObserver.Value is { OnStream: not null };

    /// <summary>Port of <c>report_model_stream_start()</c>.</summary>
    public static void ReportModelStreamStart() => CurrentObserver.Value?.StreamStarted();

    /// <summary>Port of <c>report_model_stream_progress()</c>.</summary>
    public static void ReportModelStreamProgress(int? outputTokens = null) => CurrentObserver.Value?.ReportProgress(outputTokens);

    /// <summary>Port of <c>report_model_stream_delta()</c>.</summary>
    public static Task ReportModelStreamDeltaAsync(StreamEvent delta)
    {
        if (DeltaInterceptor is not null)
        {
            return DeltaInterceptor(delta);
        }

        return CurrentObserver.Value?.ReportDeltaAsync(delta) ?? Task.CompletedTask;
    }

    /// <summary>Port of <c>ModelStreamObserver.stream_started</c>.</summary>
    public void StreamStarted()
    {
        Started = true;
        TokensCurrent = null;
    }

    /// <summary>Port of <c>ModelStreamObserver.report_progress</c>.</summary>
    public void ReportProgress(int? outputTokens = null)
    {
        if (outputTokens is not null)
        {
            TokensCurrent = outputTokens;
        }

        ProgressReports++;
    }

    /// <summary>
    /// Port of <c>ModelStreamObserver.report_delta</c> / <c>_deliver</c>: delivers the event to the
    /// handler; a handler exception detaches it for the rest of the generate call and logs a warning.
    /// </summary>
    public async Task ReportDeltaAsync(StreamEvent delta)
    {
        if (OnStream is null)
        {
            ProgressReports++;
            return;
        }

        ProgressReports++;
        Delivered = true;
        try
        {
            await OnStream(delta).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            OnStream = null;
            ProviderLogger.Warning(
                $"on_stream handler raised an exception; streaming callbacks are disabled for the remainder of this {Model} generate call: {ex}");
        }
    }

    private sealed class Restore(ModelStreamObserver? previous) : IDisposable
    {
        public void Dispose() => CurrentObserver.Value = previous;
    }
}

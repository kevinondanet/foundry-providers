namespace InspectAzureAI.Eval.Model;

/// <summary>Port of the <c>ModelEventSink</c> hook of <c>model/_model.py</c>: receives every <see cref="ModelEvent"/> (the agent bridge uses it to track state).</summary>
public interface IModelEventSink
{
    /// <summary>Called with the final event, after it was added to the transcript.</summary>
    void OnModelEvent(ModelEvent e);

    /// <summary>
    /// Called by <see cref="Model"/> with the completed event <em>before</em> it is added to the transcript. The sink
    /// may return a modified copy (a span id, tool-call views) and may add events of its own to the transcript first
    /// (span ends, compaction markers), which then precede the model event. The substitute for Python's
    /// <c>ModelEventSink.on_pending</c> (<c>model/_model.py:2793-2810</c>): this port records a model event once, at
    /// completion. The default returns <paramref name="e"/> unchanged. An exception fails the generation.
    /// </summary>
    ModelEvent OnRecording(ModelEvent e) => e;
}

/// <summary>Ambient (AsyncLocal) sink installation, the port of <c>_model_event_sink</c>; disposing the handle restores the previous sink.</summary>
public static class ModelEventSinks
{
    private static readonly AsyncLocal<IModelEventSink?> Ambient = new();

    public static IModelEventSink? Current => Ambient.Value;

    public static IDisposable Install(IModelEventSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var previous = Ambient.Value;
        Ambient.Value = sink;
        return new Scope(previous);
    }

    private sealed class Scope(IModelEventSink? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}

namespace InspectAzureAI.Eval.Model;

/// <summary>Port of the <c>ModelEventSink</c> hook of <c>model/_model.py</c>: receives every <see cref="ModelEvent"/> (the agent bridge uses it to track state).</summary>
public interface IModelEventSink
{
    void OnModelEvent(ModelEvent e);
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

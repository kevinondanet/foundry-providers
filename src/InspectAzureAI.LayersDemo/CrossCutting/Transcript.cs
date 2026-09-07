// ============================================================================
//  CROSS-CUTTING CONCERN 2 of 4: THE TRANSCRIPT
//  Python: inspect_ai/log/_transcript.py
//
//  Each sample owns a transcript: an append-only list of events. Every layer
//  that does work writes to it — the engine records sample init and scores,
//  solvers record steps, the model layer records every generate call with the
//  raw request/response, tools record their calls, the sandbox records execs.
//
//  The transcript is ALSO the only channel by which lower layers communicate
//  upward. The model layer never calls the engine to say "I retried"; it
//  emits an event. The control plane never asks the engine for progress; it
//  taps the event stream. Dependencies point down; information flows up.
//
//  `transcript()` returns the transcript of the sample currently running on
//  this call chain. Python implements that with a contextvar; C# has the
//  same idea in AsyncLocal<T>, which flows through awaits automatically.
// ============================================================================
namespace inspect_ai.log;

/// <summary>Base of every transcript event. `Source` is the layer/package
/// that emitted it, so the demo can show who wrote what.</summary>
public abstract record Event(string Source)
{
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.UtcNow;

    /// <summary>One-line description used when the CLI prints the transcript.</summary>
    public abstract string Summary { get; }
}

public sealed record SampleInitEvent(string Source, string SampleId, string Input) : Event(Source)
{
    public override string Summary => $"sample '{SampleId}' input: \"{Input}\"";
}

/// <summary>Begin/end markers around each solver (Python: StepEvent from `solver_transcript`).</summary>
public sealed record StepEvent(string Source, string Name, string Action) : Event(Source)
{
    public override string Summary => $"{Action} solver '{Name}'";
}

/// <summary>One call to the model, including the exact bytes sent and received.</summary>
public sealed record ModelEvent(
    string Source, string Model, int InputMessages, string StopReason,
    string Request, string Response, int Retries) : Event(Source)
{
    public override string Summary => $"{Model}: {InputMessages} msgs -> stop={StopReason}" + (Retries > 0 ? $" after {Retries} retry(s)" : "");
}

public sealed record ToolEvent(string Source, string Function, string Arguments, string Result) : Event(Source)
{
    public override string Summary => $"{Function}({Arguments}) => {Result.Trim()}";
}

public sealed record SandboxEvent(string Source, string Action, string Detail, string Result) : Event(Source)
{
    public override string Summary => $"{Action} {Detail} => {Result.Trim()}";
}

public sealed record ScoreEvent(string Source, string Value, string Answer, string Explanation) : Event(Source)
{
    public override string Summary => $"score={Value} answer=\"{Answer}\" ({Explanation})";
}

public sealed record InfoEvent(string Source, string Message) : Event(Source)
{
    public override string Summary => Message;
}

public sealed record SampleDoneEvent(string Source, string SampleId, bool Success) : Event(Source)
{
    public override string Summary => $"sample '{SampleId}' {(Success ? "completed" : "failed")}";
}

public sealed class Transcript(string sampleId)
{
    private readonly List<Event> _events = new();   // no lock: single event loop

    public string SampleId { get; } = sampleId;
    public IReadOnlyList<Event> Events => _events;

    /// <summary>Append an event. Also raises the process-wide tap so anything
    /// watching live (the control plane) sees it at once.</summary>
    public void Emit(Event e)
    {
        _events.Add(e);
        EventEmitted?.Invoke(this, e);
    }

    /// <summary>
    /// The live tap. Python's equivalent is the sample-buffer writer that the
    /// `inspect view` server reads from outside the process. Nothing below the
    /// control plane subscribes to this; lower layers only ever call Emit().
    /// </summary>
    public static event Action<Transcript, Event>? EventEmitted;

    // ---- ambient "current sample" (Python: contextvar) ---------------------
    private static readonly AsyncLocal<Transcript?> Current = new();

    /// <summary>The transcript of the sample running on this call chain.</summary>
    public static Transcript transcript()
        => Current.Value ?? throw new InvalidOperationException("transcript() called outside a running sample.");

    /// <summary>Engine-only: make <paramref name="t"/> current for the duration of a sample.</summary>
    internal static IDisposable Begin(Transcript t)
    {
        var previous = Current.Value;
        Current.Value = t;
        return new Restore(() => Current.Value = previous);
    }

    private sealed class Restore(Action undo) : IDisposable { public void Dispose() => undo(); }
}

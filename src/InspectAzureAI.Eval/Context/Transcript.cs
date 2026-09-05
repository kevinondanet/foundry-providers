using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Context;

/// <summary>
/// Port of <c>log/_transcript.py</c> <c>Transcript</c> plus <c>util/_span.py</c> <c>span()</c>: the ordered
/// event list of one sample. Guarded by a lock because tool calls in one stage run as concurrent tasks;
/// the current span id is ambient (AsyncLocal) so each task nests its events under its own span.
/// </summary>
public sealed class Transcript
{
    private readonly object _sync = new();

    private readonly List<TranscriptEvent> _events = [];

    private readonly AsyncLocal<string?> _currentSpanId = new();

    private readonly Stopwatch _working = Stopwatch.StartNew();

    /// <summary>Snapshot of the events recorded so far.</summary>
    public IReadOnlyList<TranscriptEvent> Events
    {
        get
        {
            lock (_sync)
            {
                return _events.ToArray();
            }
        }
    }

    /// <summary>Id of the innermost open span in the current async flow.</summary>
    public string? CurrentSpanId => _currentSpanId.Value;

    /// <summary>
    /// Port of <c>sample_working_time()</c> as this port measures it: seconds since the transcript was created
    /// (waiting time is not subtracted).
    /// </summary>
    public double WorkingTime => _working.Elapsed.TotalSeconds;

    public void Add(TranscriptEvent e) => Record(e);

    /// <summary><see cref="Add"/> returning the event as stored (span id and working start stamped), for a later <see cref="Update"/>.</summary>
    public TEvent Record<TEvent>(TEvent e) where TEvent : TranscriptEvent
    {
        ArgumentNullException.ThrowIfNull(e);
        var stamped = e;
        if (stamped.SpanId is null && _currentSpanId.Value is { } spanId)
        {
            stamped = (TEvent)(stamped with { SpanId = spanId });
        }

        if (stamped.WorkingStart == 0)
        {
            stamped = (TEvent)(stamped with { WorkingStart = WorkingTime });
        }

        lock (_sync)
        {
            _events.Add(stamped);
        }

        return stamped;
    }

    /// <summary>
    /// Port of <c>transcript()._event_updated(event)</c>: replaces the recorded event with the same <see cref="TranscriptEvent.Uuid"/>
    /// in place (a pending event completing, say). An event that was never recorded is an <see cref="InvalidOperationException"/>.
    /// </summary>
    public void Update(TranscriptEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Uuid is null)
        {
            throw new InvalidOperationException("Only an event with a uuid can be updated.");
        }

        lock (_sync)
        {
            var index = _events.FindIndex(existing => existing.Uuid == e.Uuid);
            if (index < 0)
            {
                throw new InvalidOperationException($"Event {e.Uuid} ({e.Event}) is not in the transcript and cannot be updated.");
            }

            _events[index] = e;
        }
    }

    /// <summary>Port of <c>transcript().info(data, source=...)</c>; <paramref name="data"/> is serialized to JSON.</summary>
    public void Info(string source, object? data = null) => Add(new InfoEvent(source, ToJson(data)));

    /// <summary>
    /// Port of <c>span(name, type=...)</c>: emits a begin event now and an end event on dispose, preceded by a
    /// <see cref="StoreEvent"/> when the ambient sample store changed inside the span (<see cref="StoreChanges.Track"/>).
    /// </summary>
    public IDisposable Span(string name, string type = "span")
    {
        var id = ShortUuid.Generate();
        var parentId = _currentSpanId.Value;
        Add(new SpanBeginEvent(id, name, type, parentId));
        _currentSpanId.Value = id;
        return new SpanScope(this, id, parentId, StoreChanges.Track(transcript: this));
    }

    private static JsonNode? ToJson(object? data) => data switch
    {
        null => null,
        JsonNode node => node,
        string text => JsonValue.Create(text),
        _ => JsonSerializer.SerializeToNode(data, data.GetType()),
    };

    private sealed class SpanScope(Transcript transcript, string id, string? parentId, IDisposable storeChanges) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            storeChanges.Dispose();
            transcript.Add(new SpanEndEvent(id));
            transcript._currentSpanId.Value = parentId;
        }
    }
}

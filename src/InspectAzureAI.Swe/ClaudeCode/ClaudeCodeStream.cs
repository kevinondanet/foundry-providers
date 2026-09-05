using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Sandbox;

namespace InspectAzureAI.Swe.ClaudeCode;

/// <summary>
/// Port of the tagged events of inspect_swe <c>_claude_code/_events/stream.py</c>: a parsed stdout line, a line
/// that is not JSON, a stderr chunk, and the process exit (always last).
/// </summary>
public abstract record ClaudeCodeStreamEvent
{
    /// <summary>Port of <c>JsonlEvent</c>; <see cref="Raw"/> is the parsed line.</summary>
    public sealed record Jsonl(JsonNode Raw, string Line) : ClaudeCodeStreamEvent;

    /// <summary>Port of <c>JsonlParseError</c>.</summary>
    public sealed record ParseError(string Line) : ClaudeCodeStreamEvent;

    /// <summary>Port of <c>StderrEvent</c>.</summary>
    public sealed record Stderr(string Data) : ClaudeCodeStreamEvent;

    /// <summary>Port of <c>ExitEvent</c>.</summary>
    public sealed record Exit(int Code) : ClaudeCodeStreamEvent;
}

/// <summary>
/// Port of <c>claude_code_event_stream</c>: line-buffers stdout chunks, strips each line, skips empty ones and
/// parses JSON; a trailing partial line is flushed on completion before the exit event. Feed chunks through
/// <see cref="PushStdout"/> / <see cref="PushStderr"/> and finish with <see cref="Complete"/>, or use
/// <see cref="Parse"/> on a finished <see cref="ExecResult"/> (the sandbox api has no streaming exec).
/// </summary>
public sealed class ClaudeCodeStream
{
    private readonly StringBuilder _buffer = new();

    /// <summary>Events for every complete line in <paramref name="data"/> plus whatever was buffered before it (one linear scan; only the trailing partial line is kept).</summary>
    public IReadOnlyList<ClaudeCodeStreamEvent> PushStdout(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var text = _buffer.Length == 0 ? data : _buffer.Append(data).ToString();
        _buffer.Clear();
        var events = new List<ClaudeCodeStreamEvent>();
        var start = 0;
        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                _buffer.Append(text, start, text.Length - start);
                break;
            }

            var line = text.AsSpan(start, newline - start).Trim();
            if (line.Length > 0)
            {
                events.Add(ParseLine(line.ToString()));
            }

            start = newline + 1;
        }

        return events;
    }

    public ClaudeCodeStreamEvent.Stderr PushStderr(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new ClaudeCodeStreamEvent.Stderr(data);
    }

    /// <summary>Flushes a trailing partial line, then yields the exit event.</summary>
    public IReadOnlyList<ClaudeCodeStreamEvent> Complete(int exitCode)
    {
        var events = new List<ClaudeCodeStreamEvent>();
        var tail = _buffer.ToString().Trim();
        _buffer.Clear();
        if (tail.Length > 0)
        {
            events.Add(ParseLine(tail));
        }

        events.Add(new ClaudeCodeStreamEvent.Exit(exitCode));
        return events;
    }

    /// <summary>Frames a finished process: stdout lines, one stderr event when there is any, then the exit.</summary>
    public static IReadOnlyList<ClaudeCodeStreamEvent> Parse(ExecResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var stream = new ClaudeCodeStream();
        var events = new List<ClaudeCodeStreamEvent>(stream.PushStdout(result.Stdout));
        if (result.Stderr.Length > 0)
        {
            events.Add(stream.PushStderr(result.Stderr));
        }

        events.AddRange(stream.Complete(result.ReturnCode));
        return events;
    }

    private static ClaudeCodeStreamEvent ParseLine(string line)
    {
        try
        {
            // Python yields json.loads(line) whatever it is; a JSON `null` root would crash the consumer there,
            // so it is reported as unparseable here rather than carried as a null node.
            var node = JsonNode.Parse(line);
            return node is null ? new ClaudeCodeStreamEvent.ParseError(line) : new ClaudeCodeStreamEvent.Jsonl(node, line);
        }
        catch (JsonException)
        {
            return new ClaudeCodeStreamEvent.ParseError(line);
        }
    }
}

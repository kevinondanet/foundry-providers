using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Sandbox;

namespace InspectAzureAI.Swe.CopilotCli;

/// <summary>
/// The tagged stream events of the Copilot CLI agent, a copy of <see cref="InspectAzureAI.Swe.ClaudeCode.ClaudeCodeStreamEvent"/>
/// (inspect_swe <c>_claude_code/_events/stream.py</c>): a parsed stdout line, a line that is not JSON, a stderr
/// chunk, and the process exit (always last).
/// </summary>
public abstract record CopilotCliStreamEvent
{
    /// <summary>A parsed <c>--output-format json</c> line; <see cref="Raw"/> is the parsed node.</summary>
    public sealed record Jsonl(JsonNode Raw, string Line) : CopilotCliStreamEvent;

    public sealed record ParseError(string Line) : CopilotCliStreamEvent;

    public sealed record Stderr(string Data) : CopilotCliStreamEvent;

    public sealed record Exit(int Code) : CopilotCliStreamEvent;
}

/// <summary>
/// The line framing of the CLI's JSONL stdout, a copy of <see cref="InspectAzureAI.Swe.ClaudeCode.ClaudeCodeStream"/>
/// (<c>claude_code_event_stream</c>): line-buffers stdout chunks, strips each line, skips empty ones and parses
/// JSON; a trailing partial line is flushed on completion before the exit event.
/// </summary>
public sealed class CopilotCliStream
{
    private readonly StringBuilder _buffer = new();

    /// <summary>Events for every complete line in <paramref name="data"/> plus whatever was buffered before it (one linear scan; only the trailing partial line is kept).</summary>
    public IReadOnlyList<CopilotCliStreamEvent> PushStdout(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var text = _buffer.Length == 0 ? data : _buffer.Append(data).ToString();
        _buffer.Clear();
        var events = new List<CopilotCliStreamEvent>();
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

    public CopilotCliStreamEvent.Stderr PushStderr(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new CopilotCliStreamEvent.Stderr(data);
    }

    /// <summary>Flushes a trailing partial line, then yields the exit event.</summary>
    public IReadOnlyList<CopilotCliStreamEvent> Complete(int exitCode)
    {
        var events = new List<CopilotCliStreamEvent>();
        var tail = _buffer.ToString().Trim();
        _buffer.Clear();
        if (tail.Length > 0)
        {
            events.Add(ParseLine(tail));
        }

        events.Add(new CopilotCliStreamEvent.Exit(exitCode));
        return events;
    }

    /// <summary>Frames a finished process: stdout lines, one stderr event when there is any, then the exit.</summary>
    public static IReadOnlyList<CopilotCliStreamEvent> Parse(ExecResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var stream = new CopilotCliStream();
        var events = new List<CopilotCliStreamEvent>(stream.PushStdout(result.Stdout));
        if (result.Stderr.Length > 0)
        {
            events.Add(stream.PushStderr(result.Stderr));
        }

        events.AddRange(stream.Complete(result.ReturnCode));
        return events;
    }

    private static CopilotCliStreamEvent ParseLine(string line)
    {
        try
        {
            var node = JsonNode.Parse(line);
            return node is null ? new CopilotCliStreamEvent.ParseError(line) : new CopilotCliStreamEvent.Jsonl(node, line);
        }
        catch (JsonException)
        {
            return new CopilotCliStreamEvent.ParseError(line);
        }
    }
}

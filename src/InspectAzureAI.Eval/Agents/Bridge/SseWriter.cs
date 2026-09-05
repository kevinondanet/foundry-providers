using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Agents.Bridge;

/// <summary>One server-sent event: the <c>event:</c> name (null for the data-only OpenAI style) and its JSON <c>data:</c>.</summary>
public sealed record SseEvent(string? Event, JsonNode Data)
{
    /// <summary>The wire form: <c>event: &lt;name&gt;\ndata: &lt;json&gt;\n\n</c>, or just the data line when unnamed.</summary>
    public string Format() => Event is null
        ? $"data: {PythonJson.Dumps(Data)}\n\n"
        : $"event: {Event}\ndata: {PythonJson.Dumps(Data)}\n\n";
}

/// <summary>
/// Port of the SSE framing of <c>inspect_sandbox_tools/_agent_bridge/proxy.py</c> (<c>_sse_bytes</c> /
/// <c>_sse_anthropic</c>): writes events to a response stream, flushing after each one so a client sees every
/// event as soon as it is produced rather than when the response completes.
/// </summary>
public sealed class SseWriter(Stream stream)
{
    /// <summary>The OpenAI stream terminator.</summary>
    public const string Done = "data: [DONE]\n\n";

    private readonly Stream _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    public Task WriteAsync(SseEvent sseEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sseEvent);
        return WriteRawAsync(sseEvent.Format(), cancellationToken);
    }

    public Task WriteDataAsync(JsonNode data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        return WriteAsync(new SseEvent(null, data), cancellationToken);
    }

    public Task WriteDoneAsync(CancellationToken cancellationToken = default) => WriteRawAsync(Done, cancellationToken);

    public async Task WriteRawAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.UTF8.GetBytes(text);
        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

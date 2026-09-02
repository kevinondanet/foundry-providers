using System.Text;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;

namespace InspectAzureAI.Provider.Testing;

/// <summary>
/// An in-memory <see cref="HttpPipelineTransport"/> that answers every request from
/// <see cref="Responder"/> and records what was sent. Plug it into
/// <see cref="AzureAIClientSettings.Transport"/> to exercise the provider offline (the xunit suite
/// and the sample's <c>--fake</c> mode both use it).
/// </summary>
public sealed class CannedTransport : HttpPipelineTransport
{
    /// <summary>Produces the response for a captured request. Defaults to an empty 200 JSON body.</summary>
    public Func<CapturedRequest, Response> Responder { get; set; } = _ => CannedResponse.Json(200, "{}");

    /// <summary>Every request seen so far, oldest first.</summary>
    public List<CapturedRequest> Requests { get; } = [];

    /// <summary>The most recent request, if any.</summary>
    public CapturedRequest? LastRequest => Requests.Count > 0 ? Requests[^1] : null;

    public override Request CreateRequest() => new CannedRequest();

    public override void Process(HttpMessage message) => ProcessAsync(message).AsTask().GetAwaiter().GetResult();

    public override async ValueTask ProcessAsync(HttpMessage message)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in message.Request.Headers)
        {
            headers[header.Name] = header.Value ?? "";
        }

        string? body = null;
        if (message.Request.Content is not null)
        {
            using var buffer = new MemoryStream();
            await message.Request.Content.WriteToAsync(buffer, message.CancellationToken).ConfigureAwait(false);
            body = Encoding.UTF8.GetString(buffer.ToArray());
        }

        var captured = new CapturedRequest(message.Request.Uri.ToUri(), message.Request.Method.Method, headers, body);
        Requests.Add(captured);
        message.Response = Responder(captured);
    }
}

/// <summary>A request captured by <see cref="CannedTransport"/>.</summary>
public sealed record CapturedRequest(Uri Uri, string Method, IReadOnlyDictionary<string, string> Headers, string? Body)
{
    /// <summary>The JSON body parsed as an object (throws when there is no body).</summary>
    public System.Text.Json.Nodes.JsonObject BodyJson =>
        System.Text.Json.Nodes.JsonNode.Parse(Body ?? throw new InvalidOperationException("Request had no body"))!.AsObject();
}

/// <summary>Minimal <see cref="Request"/> for <see cref="CannedTransport"/>.</summary>
internal sealed class CannedRequest : Request
{
    private readonly List<HttpHeader> _headers = [];

    public override string ClientRequestId { get; set; } = Guid.NewGuid().ToString();

    public override void Dispose()
    {
    }

    protected override void AddHeader(string name, string value) => _headers.Add(new HttpHeader(name, value));

    protected override bool ContainsHeader(string name) => _headers.Any(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    protected override IEnumerable<HttpHeader> EnumerateHeaders() => _headers;

    protected override bool RemoveHeader(string name) => _headers.RemoveAll(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;

    protected override bool TryGetHeader(string name, out string value)
    {
        value = _headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value!;
        return value is not null;
    }

    protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
    {
        values = _headers.Where(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).ToList();
        return values.Any();
    }
}

/// <summary>A canned <see cref="Response"/> with a status, body and headers.</summary>
public sealed class CannedResponse : Response
{
    private readonly List<HttpHeader> _headers = [];
    private Stream? _stream;

    public CannedResponse(int status, string body, string contentType, IReadOnlyDictionary<string, string>? headers = null)
    {
        Status = status;
        _stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        _headers.Add(new HttpHeader("Content-Type", contentType));
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                _headers.Add(new HttpHeader(name, value));
            }
        }
    }

    /// <summary>A JSON response.</summary>
    public static CannedResponse Json(int status, string body, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, body, "application/json", headers);

    /// <summary>A server-sent-events response built from JSON chunks, terminated with <c>[DONE]</c>.</summary>
    public static CannedResponse Sse(IEnumerable<string> jsonChunks)
    {
        var body = new StringBuilder();
        foreach (var chunk in jsonChunks)
        {
            body.Append("data: ").Append(chunk).Append("\n\n");
        }

        body.Append("data: [DONE]\n\n");
        return new CannedResponse(200, body.ToString(), "text/event-stream");
    }

    /// <summary>An OpenAI-style error body (<c>{"error": {"message": ...}}</c>).</summary>
    public static CannedResponse Error(int status, string message, IReadOnlyDictionary<string, string>? headers = null) =>
        Json(status, System.Text.Json.JsonSerializer.Serialize(new { error = new { message } }), headers);

    public override int Status { get; }

    public override string ReasonPhrase => Status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        408 => "Request Timeout",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Status",
    };

    public override Stream? ContentStream
    {
        get => _stream;
        set => _stream = value;
    }

    public override string ClientRequestId { get; set; } = "";

    public override void Dispose()
    {
    }

    protected override bool ContainsHeader(string name) => _headers.Any(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    protected override IEnumerable<HttpHeader> EnumerateHeaders() => _headers;

    protected override bool TryGetHeader(string name, out string value)
    {
        value = _headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value!;
        return value is not null;
    }

    protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
    {
        values = _headers.Where(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).ToList();
        return values.Any();
    }
}

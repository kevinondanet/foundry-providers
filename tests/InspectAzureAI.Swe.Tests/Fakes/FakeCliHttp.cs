using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Testing;

namespace InspectAzureAI.Swe.Tests;

// Shared HTTP helpers for the fake CLIs (FakeCodexCli, FakeClaudeCodeCli): they read the bridge address and token
// from the exec env, talk to the real bridge over HttpClient, parse SSE, log what they sent, read config.toml, and
// keep binary downloads offline. Wave-2 fakes add no helper types of their own.

/// <summary>One request a fake CLI made to the bridge.</summary>
/// <param name="Method">HTTP method.</param>
/// <param name="Path">Request path, such as <c>/v1/responses</c>.</param>
/// <param name="Model">The <c>model</c> the request named, when it had one.</param>
/// <param name="Status">The bridge's HTTP status.</param>
/// <param name="Turn">The fake CLI's turn counter when it sent the request.</param>
/// <param name="Authorization">The credential header the request carried (<c>Authorization</c> or <c>x-api-key</c> value).</param>
public sealed record FakeCliRequest(string Method, string Path, string? Model, int Status, int Turn, string? Authorization);

/// <summary>A thread-safe log of <see cref="FakeCliRequest"/>s (bridge handlers and the fake run concurrently).</summary>
public sealed class FakeCliRequestLog
{
    private readonly Lock _gate = new();

    private readonly List<FakeCliRequest> _entries = [];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public void Add(FakeCliRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            _entries.Add(request);
        }
    }

    /// <summary>A copy of the entries so far, in order.</summary>
    public IReadOnlyList<FakeCliRequest> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToList();
        }
    }
}

/// <summary>A minimal Server-Sent Events reader for the bridge's streaming replies.</summary>
public static class SseFrames
{
    /// <summary>The <c>data:</c> payload that ends an OpenAI-style stream.</summary>
    public const string Done = "[DONE]";

    /// <summary>
    /// Reads blank-line delimited frames: <c>event:</c> names the frame, each <c>data:</c> line appends to its data
    /// (joined with <c>\n</c>), <c>:</c> comment lines and other fields are ignored, and a frame without data is not
    /// dispatched. A frame whose data is <see cref="Done"/> is yielded and ends the enumeration. A trailing frame
    /// without its blank line is still yielded at end of stream.
    /// </summary>
    public static async IAsyncEnumerable<(string? Event, string Data)> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        string? eventName = null;
        List<string>? data = null;
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data is not null)
                {
                    var payload = string.Join("\n", data);
                    yield return (eventName, payload);
                    if (payload == Done)
                    {
                        yield break;
                    }
                }

                eventName = null;
                data = null;
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            if (field == "event")
            {
                eventName = value;
            }
            else if (field == "data")
            {
                (data ??= []).Add(value);
            }
        }

        if (data is not null)
        {
            yield return (eventName, string.Join("\n", data));
        }
    }
}

/// <summary>What a fake CLI reads from its launch: env variables and HTTP clients for the bridge.</summary>
public static class FakeCliEnv
{
    /// <summary>The exec env variable <paramref name="name"/>; throws when the launch did not set it.</summary>
    public static string Require(FakeExecCall call, string name)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(name);
        return call.Env is not null && call.Env.TryGetValue(name, out var value)
            ? value
            : throw new InvalidOperationException($"the CLI launch env has no {name} (argv: {string.Join(" ", call.Cmd)})");
    }

    /// <summary>A client for <paramref name="baseUrl"/> (relative paths resolve beneath it) sending <c>Authorization: Bearer token</c>.</summary>
    public static HttpClient BearerClient(string baseUrl, string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var client = Client(baseUrl);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>A client for <paramref name="baseUrl"/> sending <c>x-api-key: key</c> (the Anthropic SDK's header).</summary>
    public static HttpClient ApiKeyClient(string baseUrl, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var client = Client(baseUrl);
        client.DefaultRequestHeaders.Add("x-api-key", key);
        return client;
    }

    private static HttpClient Client(string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };
    }
}

/// <summary>
/// An <see cref="HttpMessageHandler"/> that serves only registered URIs: every request is recorded in
/// <see cref="Requests"/>, and one with no registration is also recorded in <see cref="Unexpected"/> and throws
/// <see cref="InvalidOperationException"/> (<c>unexpected request &lt;method&gt; &lt;uri&gt;</c>). This is the
/// offline guarantee for binary and catalog downloads, whose failures are otherwise swallowed by fallbacks.
/// </summary>
public sealed class StrictHttpHandler : HttpMessageHandler
{
    private readonly Lock _gate = new();

    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    private readonly List<HttpRequestMessage> _requests = [];

    private readonly List<HttpRequestMessage> _unexpected = [];

    public IReadOnlyList<HttpRequestMessage> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToList();
            }
        }
    }

    public IReadOnlyList<HttpRequestMessage> Unexpected
    {
        get
        {
            lock (_gate)
            {
                return _unexpected.ToList();
            }
        }
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Bytes(byte[] body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new ByteArrayContent(body) };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new ByteArrayContent([]) };

    /// <summary>Serves <paramref name="uri"/> (replacing an earlier registration) with a fresh response per request.</summary>
    public StrictHttpHandler Register(Uri uri, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(respond);
        lock (_gate)
        {
            _routes[uri.AbsoluteUri] = respond;
        }

        return this;
    }

    public StrictHttpHandler Register(string uri, Func<HttpRequestMessage, HttpResponseMessage> respond) => Register(new Uri(uri), respond);

    /// <summary>The number of requests whose URI is <paramref name="uri"/>.</summary>
    public int CountOf(string uri)
    {
        var absolute = new Uri(uri).AbsoluteUri;
        return Requests.Count(request => request.RequestUri?.AbsoluteUri == absolute);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Func<HttpRequestMessage, HttpResponseMessage>? respond;
        lock (_gate)
        {
            _requests.Add(request);
            if (!_routes.TryGetValue(request.RequestUri?.AbsoluteUri ?? "", out respond))
            {
                _unexpected.Add(request);
            }
        }

        if (respond is null)
        {
            throw new InvalidOperationException($"unexpected request {request.Method} {request.RequestUri}");
        }

        var response = respond(request);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}

/// <summary>
/// A line-based reader for the TOML subset <c>Toml.Write</c> emits, so fake CLIs can read the config files agents
/// write. Top-level keys go in table <c>""</c>; a <c>[a.b]</c> or <c>[a."b c"]</c> header names a table by its
/// unquoted segments joined with <c>.</c> (<c>a.b c</c>); keys are stored the same way (<c>features.goals</c>). Values
/// are basic and literal strings, booleans, integers, floats, single-line <c>[…]</c> arrays and <c>{ k = v, … }</c>
/// inline tables (as <see cref="JsonObject"/>). Duplicate tables or keys, and anything else, throw <see cref="FormatException"/>.
/// </summary>
public static class MiniToml
{
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonNode?>> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tables = new Dictionary<string, IReadOnlyDictionary<string, JsonNode?>>(StringComparer.Ordinal);
        var current = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        tables[""] = current;
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var cursor = new Cursor(lines[index].TrimEnd('\r'), index + 1);
            cursor.SkipWhitespace();
            if (cursor.AtEndOrComment)
            {
                continue;
            }

            if (cursor.Peek == '[')
            {
                cursor.Advance();
                var name = string.Join(".", cursor.ReadKey());
                cursor.Expect(']');
                cursor.ExpectEndOfLine();
                if (tables.ContainsKey(name))
                {
                    throw cursor.Error($"duplicate table [{name}]");
                }

                current = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
                tables[name] = current;
                continue;
            }

            var key = string.Join(".", cursor.ReadKey());
            cursor.Expect('=');
            var value = cursor.ReadValue();
            cursor.ExpectEndOfLine();
            if (!current.TryAdd(key, value))
            {
                throw cursor.Error($"duplicate key {key}");
            }
        }

        return tables;
    }

    private sealed class Cursor(string line, int lineNumber)
    {
        private int _position;

        public bool AtEnd => _position >= line.Length;

        public bool AtEndOrComment => AtEnd || line[_position] == '#';

        public char Peek => AtEnd ? '\0' : line[_position];

        public void Advance() => _position++;

        public void SkipWhitespace()
        {
            while (!AtEnd && line[_position] is ' ' or '\t')
            {
                _position++;
            }
        }

        public FormatException Error(string message) => new($"MiniToml line {lineNumber}, column {_position + 1}: {message} in: {line}");

        public void Expect(char expected)
        {
            SkipWhitespace();
            if (Peek != expected)
            {
                throw Error($"expected '{expected}'");
            }

            Advance();
        }

        public void ExpectEndOfLine()
        {
            SkipWhitespace();
            if (!AtEndOrComment)
            {
                throw Error("unexpected trailing text");
            }
        }

        /// <summary>A dotted key: bare segments and quoted segments separated by <c>.</c>.</summary>
        public List<string> ReadKey()
        {
            var segments = new List<string>();
            while (true)
            {
                SkipWhitespace();
                segments.Add(Peek switch
                {
                    '"' => ReadBasicString(),
                    '\'' => ReadLiteralString(),
                    _ => ReadBareKey(),
                });
                SkipWhitespace();
                if (Peek != '.')
                {
                    return segments;
                }

                Advance();
            }
        }

        public JsonNode? ReadValue()
        {
            SkipWhitespace();
            switch (Peek)
            {
                case '"':
                    return JsonValue.Create(ReadBasicString());
                case '\'':
                    return JsonValue.Create(ReadLiteralString());
                case '[':
                    return ReadArray();
                case '{':
                    return ReadInlineTable();
            }

            var start = _position;
            while (!AtEnd && line[_position] is not (' ' or '\t' or ',' or ']' or '}' or '#'))
            {
                _position++;
            }

            var token = line[start.._position];
            switch (token)
            {
                case "true":
                    return JsonValue.Create(true);
                case "false":
                    return JsonValue.Create(false);
                case "inf" or "+inf":
                    return JsonValue.Create(double.PositiveInfinity);
                case "-inf":
                    return JsonValue.Create(double.NegativeInfinity);
                case "nan" or "+nan" or "-nan":
                    return JsonValue.Create(double.NaN);
            }

            var digits = token.Replace("_", "", StringComparison.Ordinal);
            if (long.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
            {
                return JsonValue.Create(integer);
            }

            if (digits.Length > 0 && double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return JsonValue.Create(number);
            }

            _position = start;
            throw Error($"unsupported value '{token}'");
        }

        private JsonArray ReadArray()
        {
            Advance();
            var array = new JsonArray();
            while (true)
            {
                SkipWhitespace();
                if (Peek == ']')
                {
                    Advance();
                    return array;
                }

                array.Add(ReadValue());
                SkipWhitespace();
                if (Peek == ',')
                {
                    Advance();
                    continue;
                }

                Expect(']');
                return array;
            }
        }

        private JsonObject ReadInlineTable()
        {
            Advance();
            var table = new JsonObject();
            SkipWhitespace();
            if (Peek == '}')
            {
                Advance();
                return table;
            }

            while (true)
            {
                var key = string.Join(".", ReadKey());
                Expect('=');
                var value = ReadValue();
                if (table.ContainsKey(key))
                {
                    throw Error($"duplicate inline table key {key}");
                }

                table[key] = value;
                SkipWhitespace();
                if (Peek == ',')
                {
                    Advance();
                    continue;
                }

                Expect('}');
                return table;
            }
        }

        private string ReadBareKey()
        {
            var start = _position;
            while (!AtEnd && (char.IsAsciiLetterOrDigit(line[_position]) || line[_position] is '_' or '-'))
            {
                _position++;
            }

            return _position > start ? line[start.._position] : throw Error("expected a key");
        }

        private string ReadLiteralString()
        {
            Advance();
            var end = line.IndexOf('\'', _position);
            if (end < 0)
            {
                throw Error("unterminated literal string");
            }

            var value = line[_position..end];
            _position = end + 1;
            return value;
        }

        private string ReadBasicString()
        {
            Advance();
            var builder = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw Error("unterminated string");
                }

                var c = line[_position++];
                if (c == '"')
                {
                    return builder.ToString();
                }

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                if (AtEnd)
                {
                    throw Error("unterminated escape");
                }

                var escape = line[_position++];
                switch (escape)
                {
                    case 'b': builder.Append('\b'); break;
                    case 't': builder.Append('\t'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'r': builder.Append('\r'); break;
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case 'u': builder.Append(ReadCodePoint(4)); break;
                    case 'U': builder.Append(ReadCodePoint(8)); break;
                    default: throw Error($"unsupported escape \\{escape}");
                }
            }
        }

        private string ReadCodePoint(int length)
        {
            if (_position + length > line.Length
                || !int.TryParse(line.AsSpan(_position, length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var codePoint))
            {
                throw Error("invalid unicode escape");
            }

            _position += length;
            return char.ConvertFromUtf32(codePoint);
        }
    }
}

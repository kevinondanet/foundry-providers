using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Agents.Bridge;

/// <summary>
/// Port of <c>agent/_bridge/sandbox/</c> plus the HTTP front of <c>inspect_sandbox_tools/_agent_bridge/proxy.py</c>:
/// the server a sandboxed agent talks to as if it were the Anthropic Messages API or the OpenAI chat
/// completions API. Unlike Python (a proxy inside the sandbox relaying over file RPC) it runs on the host
/// and is reached through the sandbox's host address, so every request must carry this instance's random
/// token (a container on the same network could otherwise reach the eval's model).
/// </summary>
public sealed class SandboxAgentBridge : IAsyncDisposable
{
    /// <summary>Largest request body accepted (the proxy's limit).</summary>
    public const long MaxBodyBytes = 50L * 1024 * 1024;

    /// <summary>Errors kept in <see cref="Errors"/>: the most recent ones, so a misbehaving client cannot grow host memory without bound.</summary>
    public const int MaxRecordedErrors = 100;

    private readonly AgentBridge _bridge;

    private readonly HttpListener _listener;

    private readonly CancellationTokenSource _shutdown;

    private readonly CancellationTokenSource _limitReached = new();

    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();

    private readonly object _errorsSync = new();

    private readonly List<Exception> _errors = [];

    private readonly byte[] _authTokenBytes;

    private LimitExceededException? _limitError;

    private Task? _acceptLoop;

    private volatile bool _stopping;

    private int _disposed;

    private SandboxAgentBridge(AgentBridge bridge, string hostAddress, int port, HttpListener listener, CancellationToken cancellationToken)
    {
        _bridge = bridge;
        _listener = listener;
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        HostAddress = hostAddress;
        Port = port;
        AuthToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _authTokenBytes = Encoding.UTF8.GetBytes(AuthToken);
    }

    /// <summary>
    /// Starts an HTTP server the sandboxed agent reaches at BaseUrl. port 0 = pick a free port. Binds 127.0.0.1
    /// when sandbox.HostAddress is loopback; otherwise the wildcard prefix. The wildcard is required, not a
    /// convenience: HttpListener matches each request's Host header against its prefixes, and a container reaches
    /// the host as host.docker.internal, so a prefix naming 127.0.0.1 answers those requests with 400 (Claude Code
    /// then reports the model as missing). The per-instance token is what protects the wider binding; every
    /// request must carry it as bearer/x-api-key (401 otherwise). <paramref name="cancellationToken"/>
    /// also cancels in-flight handlers and skips the disposal grace period.
    /// </summary>
    public static Task<SandboxAgentBridge> StartAsync(AgentBridge bridge, ISandboxEnvironment sandbox, int port = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        cancellationToken.ThrowIfCancellationRequested();

        var hostAddress = sandbox.HostAddress;
        var bindHosts = BindHosts(hostAddress);
        var probeAddress = bindHosts.Count == 1 && bindHosts[0] == "127.0.0.1" ? IPAddress.Loopback : IPAddress.Any;
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            var chosen = port == 0 ? FreePort(probeAddress) : port;
            var listener = new HttpListener();
            foreach (var host in bindHosts)
            {
                listener.Prefixes.Add($"http://{host}:{chosen}/");
            }

            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                listener.Close();
                if (port == 0 && attempt < attempts)
                {
                    // another process grabbed the ephemeral port between probing and binding; pick again
                    continue;
                }

                throw new InvalidOperationException(
                    $"The sandbox agent bridge could not listen on {string.Join(", ", bindHosts.Select(h => $"http://{h}:{chosen}/"))}: {ex.Message}. "
                    + "Check that the port is free and that this account may bind the address (Windows needs a URL ACL for non-loopback prefixes).",
                    ex);
            }

            var server = new SandboxAgentBridge(bridge, hostAddress, chosen, listener, cancellationToken);
            server._acceptLoop = Task.Run(server.AcceptLoopAsync);
            return Task.FromResult(server);
        }
    }

    public int Port { get; }

    public string HostAddress { get; }

    public string BaseUrl => $"http://{HostAddress}:{Port}";

    /// <summary>Random per-instance secret, accepted as <c>Authorization: Bearer</c> or <c>x-api-key</c>.</summary>
    public string AuthToken { get; }

    public AgentState State => _bridge.State;

    /// <summary>
    /// The first sample limit hit by a bridged generation. Python's bridge service re-raises
    /// <c>LimitExceededError</c> so the sample ends as a limit rather than as a provider error; here the agent
    /// watches <see cref="LimitReached"/>, tears down its exec and rethrows this.
    /// </summary>
    public LimitExceededException? LimitError
    {
        get
        {
            lock (_errorsSync)
            {
                return _limitError;
            }
        }
    }

    /// <summary>Cancelled when <see cref="LimitError"/> is set.</summary>
    public CancellationToken LimitReached => _limitReached.Token;

    /// <summary>The most recent exceptions raised while serving requests (each was also answered with an error response).</summary>
    public IReadOnlyList<Exception> Errors
    {
        get
        {
            lock (_errorsSync)
            {
                return _errors.ToArray();
            }
        }
    }

    /// <summary>How long <see cref="DisposeAsync"/> lets in-flight handlers finish before cancelling them.</summary>
    internal TimeSpan GracePeriod { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long <see cref="DisposeAsync"/> waits for cancelled handlers before abandoning them.</summary>
    internal TimeSpan AbandonPeriod { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The body limit applied by this instance (tests lower it; the contract is <see cref="MaxBodyBytes"/>).</summary>
    internal long BodyLimit { get; set; } = MaxBodyBytes;

    /// <summary>
    /// Stops accepting connections, lets in-flight handlers finish (cancelling them after the grace period, or at
    /// once when the start token was cancelled), aborts the listener so stalled connections cannot block, then
    /// waits a bounded time before abandoning whatever is still running and releasing the listener.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _stopping = true;
        try
        {
            _listener.Stop();
        }
        catch (ObjectDisposedException)
        {
        }

        var inFlight = Task.WhenAll(_inFlight.Keys.ToArray());
        var grace = _shutdown.IsCancellationRequested ? TimeSpan.Zero : GracePeriod;
        var finished = await Task.WhenAny(inFlight, Task.Delay(grace)).ConfigureAwait(false) == inFlight;
        if (!finished)
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
            // Stop() leaves established connections alive, so a handler blocked writing to a stalled client
            // would never observe the cancellation; Abort() tears the connections down.
            _listener.Abort();
            if (await Task.WhenAny(inFlight, Task.Delay(AbandonPeriod)).ConfigureAwait(false) != inFlight)
            {
                ProviderLogger.Warning($"Abandoned {_inFlight.Count} sandbox agent bridge request handler(s) that did not stop within {AbandonPeriod.TotalSeconds} seconds.");
            }
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_acceptLoop is not null)
        {
            // the accept loop only ends by the listener being stopped
            await Task.WhenAny(_acceptLoop, Task.Delay(AbandonPeriod)).ConfigureAwait(false);
        }

        try
        {
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        _shutdown.Dispose();
        _limitReached.Dispose();
    }

    private static bool IsLoopback(string hostAddress) =>
        hostAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(hostAddress, out var address) && IPAddress.IsLoopback(address));

    private static IReadOnlyList<string> BindHosts(string hostAddress) =>
        IsLoopback(hostAddress) ? ["127.0.0.1"] : ["*"];

    private static int FreePort(IPAddress address)
    {
        using var probe = new TcpListener(address, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_stopping || !_listener.IsListening)
            {
                break;
            }
            catch (HttpListenerException)
            {
                // a failed accept (client reset during the handshake) must not take the server down
                continue;
            }

            var handler = HandleAsync(context);
            _inFlight[handler] = 0;
            _ = handler.ContinueWith(t => _inFlight.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var cancellationToken = _shutdown.Token;
        var path = (request.Url?.AbsolutePath ?? "/").TrimEnd('/');
        var openAiDialect = path.StartsWith("/v1/chat", StringComparison.Ordinal);
        var responseStarted = false;
        try
        {
            if (!Authorized(request))
            {
                await WriteJsonAsync(response, 401, AnthropicBridgeApi.ErrorBody(401, "invalid x-api-key / bearer token"), cancellationToken).ConfigureAwait(false);
                return;
            }

            var route = (request.HttpMethod, path) switch
            {
                ("POST", "/v1/messages") => Route.Messages,
                ("POST", "/v1/messages/count_tokens") => Route.CountTokens,
                ("POST", "/v1/chat/completions") => Route.Completions,
                _ => Route.None,
            };
            if (route == Route.None)
            {
                await WriteJsonAsync(response, 404, AnthropicBridgeApi.ErrorBody(404, $"Not found: {request.HttpMethod} {request.Url?.PathAndQuery}"), cancellationToken).ConfigureAwait(false);
                return;
            }

            var body = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
            if (body is null)
            {
                await WriteJsonAsync(response, 413, ErrorBody(openAiDialect, 413, $"Request body exceeds {BodyLimit} bytes."), cancellationToken).ConfigureAwait(false);
                return;
            }

            JsonObject? json;
            try
            {
                json = JsonNode.Parse(body) as JsonObject;
            }
            catch (JsonException ex)
            {
                RecordError(ex);
                json = null;
                await WriteJsonAsync(response, 400, ErrorBody(openAiDialect, 400, $"Invalid JSON body: {ex.Message}"), cancellationToken).ConfigureAwait(false);
                return;
            }

            if (json is null)
            {
                await WriteJsonAsync(response, 400, ErrorBody(openAiDialect, 400, "Request body must be a JSON object."), cancellationToken).ConfigureAwait(false);
                return;
            }

            switch (route)
            {
                case Route.CountTokens:
                    await WriteJsonAsync(response, 200, AnthropicBridgeApi.CountTokens(json), cancellationToken).ConfigureAwait(false);
                    break;

                case Route.Messages:
                    {
                        var parsed = AnthropicBridgeApi.ParseRequest(json);
                        var output = await _bridge.GenerateAsync(parsed.Model, parsed.Messages, parsed.Tools, parsed.ToolChoice, parsed.Config, cancellationToken).ConfigureAwait(false);
                        var message = AnthropicBridgeApi.ResponseFromOutput(output, parsed.Model);
                        if (!parsed.Stream)
                        {
                            await WriteJsonAsync(response, 200, message, cancellationToken).ConfigureAwait(false);
                            break;
                        }

                        var writer = StartStream(response);
                        responseStarted = true;
                        foreach (var sseEvent in AnthropicBridgeApi.StreamEvents(message))
                        {
                            await writer.WriteAsync(sseEvent, cancellationToken).ConfigureAwait(false);
                        }

                        break;
                    }

                case Route.Completions:
                    {
                        // the codex cli concatenates the arguments of parallel tool calls when streaming (the
                        // proxy disables parallel calls for the same reason)
                        json["parallel_tool_calls"] = false;
                        var parsed = CompletionsBridgeApi.ParseRequest(json);
                        var output = await _bridge.GenerateAsync(parsed.Model, parsed.Messages, parsed.Tools, parsed.ToolChoice, parsed.Config, cancellationToken).ConfigureAwait(false);
                        var completion = CompletionsBridgeApi.ResponseFromOutput(output, _bridge.ResolveModel(parsed.Model).Name);
                        if (!parsed.Stream)
                        {
                            await WriteJsonAsync(response, 200, completion, cancellationToken).ConfigureAwait(false);
                            break;
                        }

                        var includeUsage = json["stream_options"] is JsonObject options && BridgeJson.GetBool(options, "include_usage") == true;
                        var writer = StartStream(response);
                        responseStarted = true;
                        foreach (var chunk in CompletionsBridgeApi.StreamChunks(completion, includeUsage))
                        {
                            await writer.WriteDataAsync(chunk, cancellationToken).ConfigureAwait(false);
                        }

                        await writer.WriteDoneAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    }
            }
        }
        catch (LimitExceededException ex)
        {
            RecordError(ex);
            lock (_errorsSync)
            {
                _limitError ??= ex;
            }

            await _limitReached.CancelAsync().ConfigureAwait(false);
            await AnswerErrorAsync(response, openAiDialect, responseStarted, 500, ex.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Not an error of the bridge: the sample is being torn down. Still answer, or the client would read
            // an empty 200 out of the closed response.
            await AnswerErrorAsync(response, openAiDialect, responseStarted, 500, "The sandbox agent bridge is shutting down.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordError(ex);
            var status = ex is ModelGenerateException or BridgeRequestException ? 400 : 500;
            await AnswerErrorAsync(response, openAiDialect, responseStarted, status, ex.Message).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception)
            {
                // closing a connection the client already dropped
            }
        }
    }

    private static async Task AnswerErrorAsync(HttpListenerResponse response, bool openAiDialect, bool responseStarted, int status, string message)
    {
        try
        {
            if (!responseStarted)
            {
                await WriteJsonAsync(response, status, ErrorBody(openAiDialect, status, message), CancellationToken.None).ConfigureAwait(false);
            }
            else if (!openAiDialect)
            {
                var writer = new SseWriter(response.OutputStream);
                await writer.WriteAsync(new SseEvent("error", AnthropicBridgeApi.ErrorBody(status, message)), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // the client is gone; the error is already recorded
        }
    }

    private bool Authorized(HttpListenerRequest request)
    {
        var presented = request.Headers["x-api-key"];
        var authorization = request.Headers["Authorization"];
        if (presented is null && authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            presented = authorization["Bearer ".Length..].Trim();
        }

        if (presented is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _authTokenBytes);
    }

    private async Task<byte[]?> ReadBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        var limit = BodyLimit;
        if (request.ContentLength64 > limit)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await request.InputStream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int status, JsonNode body, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(PythonJson.Dumps(body));
        response.StatusCode = status;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static SseWriter StartStream(HttpListenerResponse response)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers["Cache-Control"] = "no-cache";
        response.SendChunked = true;
        return new SseWriter(response.OutputStream);
    }

    private static JsonObject ErrorBody(bool openAiDialect, int status, string message) =>
        openAiDialect ? CompletionsBridgeApi.ErrorBody(status, message) : AnthropicBridgeApi.ErrorBody(status, message);

    private void RecordError(Exception ex)
    {
        lock (_errorsSync)
        {
            if (_errors.Count == MaxRecordedErrors)
            {
                _errors.RemoveAt(0);
            }

            _errors.Add(ex);
        }
    }

    private enum Route
    {
        None,
        Messages,
        CountTokens,
        Completions,
    }
}

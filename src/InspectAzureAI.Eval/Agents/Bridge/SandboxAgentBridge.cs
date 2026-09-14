using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Agents.Bridge;

/// <summary>
/// Port of <c>agent/_bridge/sandbox/</c> plus the HTTP front of <c>inspect_sandbox_tools/_agent_bridge/proxy.py</c>:
/// the server a sandboxed agent talks to as if it were the Anthropic Messages API, the OpenAI chat completions API or
/// the OpenAI Responses API, and the streamable-HTTP MCP endpoint (<c>/mcp/{server}</c>) serving bridged host tools.
/// Unlike Python (a proxy inside the sandbox relaying over file RPC) it runs on the host and is reached through the
/// sandbox's host address, so every request must carry this instance's random token (a container on the same network
/// could otherwise reach the eval's model).
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

    private readonly CancellationTokenSource _terminateRequested = new();

    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();

    private readonly object _errorsSync = new();

    private readonly List<Exception> _errors = [];

    private readonly byte[] _authTokenBytes;

    private LimitExceededException? _limitError;

    private Approval.TerminateSampleException? _terminateError;

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
    public static Task<SandboxAgentBridge> StartAsync(AgentBridge bridge, ISandboxEnvironment sandbox, int port = 0, CancellationToken cancellationToken = default) =>
        StartAsync(bridge, sandbox, port, null, cancellationToken);

    /// <summary>
    /// <see cref="StartAsync(AgentBridge, ISandboxEnvironment, int, CancellationToken)"/> that also serves
    /// <paramref name="bridgedTools"/> as MCP servers at <c>/mcp/{name}</c> (port of <c>sandbox_agent_bridge(bridged_tools=...)</c>).
    /// The registry is built (and validated, <see cref="BridgedToolRegistry(IEnumerable{BridgedToolsSpec})"/>) before the
    /// listener starts and attached to <paramref name="bridge"/>, whose generations register execution grants on it;
    /// <see cref="McpServerConfigs"/> are the configs to hand the scaffold.
    /// </summary>
    public static Task<SandboxAgentBridge> StartAsync(
        AgentBridge bridge,
        ISandboxEnvironment sandbox,
        int port,
        IReadOnlyList<BridgedToolsSpec>? bridgedTools,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        cancellationToken.ThrowIfCancellationRequested();

        var registry = new BridgedToolRegistry(bridgedTools ?? []);
        bridge.AttachBridgedTools(registry);

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

    /// <summary>The bridged host tools served at <c>/mcp/{server}</c> (the wrapped bridge's <see cref="AgentBridge.BridgedTools"/>).</summary>
    public BridgedToolRegistry BridgedTools => _bridge.BridgedTools;

    /// <summary>
    /// One MCP config per bridged tools server, for the scaffold: <c>http</c> at <c>{BaseUrl}/mcp/{name}</c> with
    /// <c>Authorization: Bearer {AuthToken}</c> and all tools. Empty when no bridged tools are served.
    /// </summary>
    public IReadOnlyList<McpServerConfigHttp> McpServerConfigs => BridgedTools.McpServerConfigs(BaseUrl, AuthToken);

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

    /// <summary>
    /// Port of <c>SandboxAgentBridge.request_terminate</c>'s signal: the termination a tool call approver requested
    /// from a bridged generation. Handlers turn exceptions into HTTP error responses, so the exception never
    /// reaches the sample runner on its own; the agent watches <see cref="TerminateRequested"/>, tears down its
    /// exec and rethrows this (as Python's <c>_monitor_terminate</c> task does).
    /// </summary>
    public Approval.TerminateSampleException? TerminateError
    {
        get
        {
            lock (_errorsSync)
            {
                return _terminateError;
            }
        }
    }

    /// <summary>Cancelled when <see cref="TerminateError"/> is set.</summary>
    public CancellationToken TerminateRequested => _terminateRequested.Token;

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
        _terminateRequested.Dispose();
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

    /// <summary>
    /// Serves one request. The dialect (which decides every error body's shape) is derived from the path before
    /// anything else, so even the 401 of an unauthenticated request is in the client's own shape; the MCP endpoint is
    /// dispatched right after authentication, and the API routes through the single route switch.
    /// </summary>
    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var cancellationToken = _shutdown.Token;
        var path = RequestPath(request);
        var dialect = DialectOf(path);
        try
        {
            if (!Authorized(request))
            {
                await WriteJsonAsync(context, 401, ErrorBody(dialect, 401, "invalid x-api-key / bearer token"), cancellationToken).ConfigureAwait(false);
                return;
            }

            if (dialect == Dialect.Mcp)
            {
                await HandleMcpAsync(context, path, cancellationToken).ConfigureAwait(false);
                return;
            }

            var route = (request.HttpMethod, path) switch
            {
                ("POST", "/v1/messages") => Route.Messages,
                ("POST", "/v1/messages/count_tokens") => Route.CountTokens,
                ("POST", "/v1/chat/completions") => Route.Completions,
                ("POST", "/v1/responses") => Route.Responses,
                _ => Route.None,
            };
            if (route == Route.None)
            {
                await WriteJsonAsync(context, 404, ErrorBody(dialect, 404, $"Not found: {request.HttpMethod} {request.Url?.PathAndQuery}"), cancellationToken).ConfigureAwait(false);
                return;
            }

            var body = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
            if (body is null)
            {
                await WriteJsonAsync(context, 413, ErrorBody(dialect, 413, $"Request body exceeds {BodyLimit} bytes."), cancellationToken).ConfigureAwait(false);
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
                await WriteJsonAsync(context, 400, ErrorBody(dialect, 400, $"Invalid JSON body: {ex.Message}"), cancellationToken).ConfigureAwait(false);
                return;
            }

            if (json is null)
            {
                await WriteJsonAsync(context, 400, ErrorBody(dialect, 400, "Request body must be a JSON object."), cancellationToken).ConfigureAwait(false);
                return;
            }

            switch (route)
            {
                case Route.CountTokens:
                    await WriteJsonAsync(context, 200, AnthropicBridgeApi.CountTokens(json), cancellationToken).ConfigureAwait(false);
                    break;

                case Route.Messages:
                    {
                        var parsed = AnthropicBridgeApi.ParseRequest(json);
                        var output = await _bridge.GenerateAsync(parsed.Model, parsed.Messages, parsed.Tools, parsed.ToolChoice, parsed.Config, cancellationToken).ConfigureAwait(false);
                        var message = AnthropicBridgeApi.ResponseFromOutput(output, parsed.Model);
                        if (!parsed.Stream)
                        {
                            await WriteJsonAsync(context, 200, message, cancellationToken).ConfigureAwait(false);
                            break;
                        }

                        var writer = StartStream(response);
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
                            await WriteJsonAsync(context, 200, completion, cancellationToken).ConfigureAwait(false);
                            break;
                        }

                        var includeUsage = json["stream_options"] is JsonObject options && BridgeJson.GetBool(options, "include_usage") == true;
                        var writer = StartStream(response);
                        foreach (var chunk in CompletionsBridgeApi.StreamChunks(completion, includeUsage))
                        {
                            await writer.WriteDataAsync(chunk, cancellationToken).ConfigureAwait(false);
                        }

                        await writer.WriteDoneAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    }

                case Route.Responses:
                    await HandleResponsesAsync(context, json, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (LimitExceededException ex)
        {
            await SignalLimitAsync(ex).ConfigureAwait(false);
            await AnswerErrorAsync(context, dialect, SignalledStatus(dialect), ex.Message).ConfigureAwait(false);
        }
        catch (Approval.TerminateSampleException ex)
        {
            // an approver ended the sample from inside a bridged generation: keep the reason for the agent to
            // rethrow, signal it, and still answer so the scaffold gets an error rather than a hung request
            await SignalTerminateAsync(ex).ConfigureAwait(false);
            await AnswerErrorAsync(context, dialect, SignalledStatus(dialect), ex.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Not an error of the bridge: the sample is being torn down. Still answer, or the client would read
            // an empty 200 out of the closed response.
            await AnswerErrorAsync(context, dialect, 500, "The sandbox agent bridge is shutting down.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordError(ex);
            var status = dialect == Dialect.Responses
                ? ResponsesErrorStatus(ex)
                : ex is ModelGenerateException or BridgeRequestException ? 400 : 500;
            var requestError = ex as BridgeRequestException;
            await AnswerErrorAsync(context, dialect, status, ex.Message, requestError?.Param, requestError?.Code).ConfigureAwait(false);
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

    /// <summary>
    /// The <c>POST /v1/responses</c> route (the OpenAI Responses API dialect, port of the proxy's
    /// <c>/v1/responses</c> handler over <c>responses_impl.py</c>): the request is parsed (a
    /// <see cref="BridgeRequestException"/> is a 400 carrying <c>param</c> and <c>code</c>), adapted to the model it
    /// resolves to, generated through the bridge (which registers bridged-tool grants), and answered as a Responses
    /// object whose <c>model</c> is the served API's model name: JSON, or when streaming the synthesized named events
    /// ending with <c>response.completed</c> (no <c>[DONE]</c>). A failure after the stream started is answered with a
    /// best-effort <c>response.failed</c> event (see <see cref="AnswerErrorAsync"/>). <c>parallel_tool_calls</c> is
    /// forwarded as the request sent it, never overridden.
    /// </summary>
    private async Task HandleResponsesAsync(HttpListenerContext context, JsonObject json, CancellationToken cancellationToken)
    {
        var request = ResponsesBridgeApi.ParseRequest(json);
        var resolved = _bridge.ResolveModel(request.Model);
        var parsed = ResponsesBridgeApi.ForServedModel(request, resolved);
        var output = await _bridge.GenerateAsync(parsed.Model, parsed.Messages, parsed.Tools, parsed.ToolChoice, parsed.Config, cancellationToken).ConfigureAwait(false);
        var body = ResponsesBridgeApi.ResponseFromOutput(output, resolved.Api.ModelName, parsed);
        if (!parsed.Stream)
        {
            await WriteJsonAsync(context, 200, body, cancellationToken).ConfigureAwait(false);
            return;
        }

        var events = ResponsesBridgeApi.StreamEvents(body);
        var progress = ResponsesStreams.GetValue(context.Response, _ => new ResponsesStreamProgress(BridgeJson.GetString(body, "id") ?? ""));
        var writer = StartStream(context.Response);
        foreach (var sseEvent in events)
        {
            await writer.WriteAsync(sseEvent, cancellationToken).ConfigureAwait(false);
            progress.SequenceNumber = sseEvent.Data["sequence_number"]?.GetValue<int>() ?? progress.SequenceNumber + 1;
        }
    }

    /// <summary>
    /// The status a sample limit or an approver's termination is answered with: 500, except on the Responses dialect,
    /// where it is 400 (deviation D-R10) because Codex retries a 5xx up to four times. The agent rethrows the recorded
    /// signal either way.
    /// </summary>
    private static int SignalledStatus(Dialect dialect) => dialect == Dialect.Responses ? 400 : 500;

    /// <summary>
    /// The status any other failure on the Responses dialect is answered with, as the proxy's <c>/v1/responses</c>
    /// handler does (<c>proxy.py:697-717</c> over <c>status_code_of</c>): the provider's HTTP error status when the
    /// exception carries one (a <see cref="ModelGenerateException"/> through its inner exception), otherwise 400
    /// (<c>_DEFAULT_ERROR_STATUS</c>). A deterministic failure is therefore never a 5xx that Codex would resend four
    /// more times.
    /// </summary>
    internal static int ResponsesErrorStatus(Exception ex)
    {
        var carrier = ex is ModelGenerateException { InnerException: { } inner } ? inner : ex;
        var status = HttpRetryUtil.StatusCodeOf(carrier)
            ?? (carrier is System.Net.Http.HttpRequestException { StatusCode: { } code } ? (int)code : null);
        return status is >= 400 and < 600 ? status.Value : 400;
    }

    /// <summary>
    /// The bridged-tools MCP endpoint (port of the proxy's <c>/mcp/*</c> routes, after authentication): a non-POST
    /// method is 405 with <c>Allow: POST</c>; a POST body is answered by <see cref="BridgedToolsMcpApi.HandleAsync"/>
    /// as JSON with <c>MCP-Protocol-Version</c>, or a bare 202. A sample limit or an approver's termination raised by a
    /// tool is signalled as for a generation and answered with a <c>-32603</c> error.
    /// </summary>
    private async Task HandleMcpAsync(HttpListenerContext context, string path, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers["MCP-Protocol-Version"] = BridgedToolsMcpApi.ProtocolVersion;
        if (request.HttpMethod != "POST")
        {
            response.Headers["Allow"] = "POST";
            WriteEmpty(context, 405);
            return;
        }

        var bytes = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            await WriteJsonAsync(context, 413, BridgedToolsMcpApi.JsonRpcError(null, -32600, $"Request body exceeds {BodyLimit} bytes."), cancellationToken).ConfigureAwait(false);
            return;
        }

        JsonNode? body;
        try
        {
            body = JsonNode.Parse(bytes);
        }
        catch (JsonException ex)
        {
            RecordError(ex);
            await WriteJsonAsync(context, 400, BridgedToolsMcpApi.JsonRpcError(null, -32700, $"Parse error: {ex.Message}"), cancellationToken).ConfigureAwait(false);
            return;
        }

        var id = body is JsonObject message ? message["id"] : null;
        McpHttpReply reply;
        try
        {
            reply = await BridgedToolsMcpApi.HandleAsync(_bridge.BridgedTools, _bridge.Approval, BridgedToolsMcpApi.ServerFromPath(path), body, cancellationToken).ConfigureAwait(false);
        }
        catch (LimitExceededException ex)
        {
            await SignalLimitAsync(ex).ConfigureAwait(false);
            reply = new McpHttpReply(200, BridgedToolsMcpApi.JsonRpcError(id, -32603, ex.Message));
        }
        catch (Approval.TerminateSampleException ex)
        {
            await SignalTerminateAsync(ex).ConfigureAwait(false);
            reply = new McpHttpReply(200, BridgedToolsMcpApi.JsonRpcError(id, -32603, ex.Message));
        }

        if (reply.Body is null)
        {
            WriteEmpty(context, reply.Status);
            return;
        }

        await WriteJsonAsync(context, reply.Status, reply.Body, cancellationToken).ConfigureAwait(false);
    }

    private async Task SignalLimitAsync(LimitExceededException ex)
    {
        RecordError(ex);
        lock (_errorsSync)
        {
            _limitError ??= ex;
        }

        await _limitReached.CancelAsync().ConfigureAwait(false);
    }

    private async Task SignalTerminateAsync(Approval.TerminateSampleException ex)
    {
        RecordError(ex);
        lock (_errorsSync)
        {
            _terminateError ??= ex;
        }

        await _terminateRequested.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Answers a failed request: a JSON error body in the dialect's shape when nothing was sent yet (the OpenAI
    /// dialects carry <paramref name="param"/> and <paramref name="code"/>); once a stream started, an Anthropic
    /// <c>error</c> event, or on the Responses dialect a <c>response.failed</c> event for the streamed response id with
    /// the next sequence number (chat completions streams get nothing more).
    /// </summary>
    private static async Task AnswerErrorAsync(HttpListenerContext context, Dialect dialect, int status, string message, string? param = null, string? code = null)
    {
        var response = context.Response;
        try
        {
            if (!response.SendChunked)
            {
                await WriteJsonAsync(context, status, ErrorBody(dialect, status, message, param, code), CancellationToken.None).ConfigureAwait(false);
            }
            else if (dialect == Dialect.Anthropic)
            {
                var writer = new SseWriter(response.OutputStream);
                await writer.WriteAsync(new SseEvent("error", AnthropicBridgeApi.ErrorBody(status, message)), CancellationToken.None).ConfigureAwait(false);
            }
            else if (dialect == Dialect.Responses)
            {
                var progress = ResponsesStreams.TryGetValue(response, out var started) ? started : new ResponsesStreamProgress("");
                var writer = new SseWriter(response.OutputStream);
                await writer.WriteAsync(ResponsesBridgeApi.FailedEvent(progress.ResponseId, progress.SequenceNumber + 1, status, message), CancellationToken.None).ConfigureAwait(false);
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

    private static async Task WriteJsonAsync(HttpListenerContext context, int status, JsonNode body, CancellationToken cancellationToken)
    {
        LogReply(context.Request, status);
        var response = context.Response;
        var bytes = Encoding.UTF8.GetBytes(PythonJson.Dumps(body));
        response.StatusCode = status;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static void WriteEmpty(HttpListenerContext context, int status)
    {
        LogReply(context.Request, status);
        context.Response.StatusCode = status;
        context.Response.ContentLength64 = 0;
    }

    /// <summary>Every non-2xx reply is logged with its method and path (the live smoke checks the bridge's health through these lines).</summary>
    private static void LogReply(HttpListenerRequest request, int status)
    {
        if (status is < 200 or >= 300)
        {
            var path = RequestPath(request);
            ProviderLogger.Warning($"agent bridge answered {status} to {request.HttpMethod} {(path.Length == 0 ? "/" : path)}");
        }
    }

    private static SseWriter StartStream(HttpListenerResponse response)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers["Cache-Control"] = "no-cache";
        response.SendChunked = true;
        return new SseWriter(response.OutputStream);
    }

    private static string RequestPath(HttpListenerRequest request) => (request.Url?.AbsolutePath ?? "/").TrimEnd('/');

    /// <summary>The client dialect a path belongs to, which decides the shape of every error body.</summary>
    private static Dialect DialectOf(string path)
    {
        if (path == "/mcp" || path.StartsWith("/mcp/", StringComparison.Ordinal))
        {
            return Dialect.Mcp;
        }

        if (path == "/v1/responses")
        {
            return Dialect.Responses;
        }

        return path.StartsWith("/v1/chat", StringComparison.Ordinal) ? Dialect.ChatCompletions : Dialect.Anthropic;
    }

    private static JsonObject ErrorBody(Dialect dialect, int status, string message, string? param = null, string? code = null) => dialect switch
    {
        Dialect.ChatCompletions or Dialect.Responses => CompletionsBridgeApi.ErrorBody(status, message, param, code),
        Dialect.Mcp => BridgedToolsMcpApi.JsonRpcError(null, -32600, message),
        _ => AnthropicBridgeApi.ErrorBody(status, message),
    };

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

    /// <summary>What a streamed Responses reply has sent so far, so a later failure can name the response and continue its sequence.</summary>
    private static readonly ConditionalWeakTable<HttpListenerResponse, ResponsesStreamProgress> ResponsesStreams = new();

    private sealed class ResponsesStreamProgress(string responseId)
    {
        public string ResponseId { get; } = responseId;

        public int SequenceNumber { get; set; }
    }

    private enum Route
    {
        None,
        Messages,
        CountTokens,
        Completions,
        Responses,
    }

    private enum Dialect
    {
        Anthropic,
        ChatCompletions,
        Responses,
        Mcp,
    }
}

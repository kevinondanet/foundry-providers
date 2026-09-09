using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Sandbox.Docker;
using InspectAzureAI.Eval.Sandbox.Docker.Compose;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Sandbox;

/// <summary>
/// Port of <c>util/_sandbox/service.py</c> <c>SandboxServiceMethod</c>: a method the sandbox can call back
/// into the host process. Deviation: Python spreads the request's <c>params</c> as keyword arguments; here the
/// method receives the <c>params</c> object itself and returns the JSON result the client unwraps.
/// </summary>
public delegate Task<JsonNode?> SandboxServiceMethod(JsonObject parameters, CancellationToken cancellationToken);

/// <summary>
/// Port of <c>util/_sandbox/service.py</c> <c>SandboxService</c> and <c>sandbox_service()</c>: a request/response
/// protocol over files in the sandbox. The host writes a generated Python client module
/// (<c>/var/tmp/sandbox-services/NAME/NAME.py</c>, <see cref="ClientScript"/>) whose <c>call_NAME(method, **params)</c>
/// drops <c>{id, method, params}</c> JSON files into <c>requests/</c> and polls <c>responses/</c> for
/// <c>{id, result, error}</c>; the host polls <c>requests/</c> through sandbox exec, dispatches each request to a
/// <see cref="SandboxServiceMethod"/> (concurrently, tracked so a slow handler never blocks the queue), writes the
/// response and removes the request. Deviations: an explicit <c>pollingInterval</c> is honoured as given rather
/// than floored to the provider default (the default stays provider based: 0.2 s for docker, 2 s otherwise);
/// request payloads are read through <c>cat</c> under the sandbox's ordinary exec output limit instead of a
/// 150 MiB override (an unreadable oversize request is still discarded with an error response); a
/// <see cref="LimitExceededException"/> raised by a handler is answered with an error response and then rethrown
/// from <see cref="RunAsync"/> in place of Python's <c>sample_active().limit_exceeded()</c>.
/// </summary>
public sealed partial class SandboxService
{
    /// <summary>Port of <c>SERVICES_DIR</c>.</summary>
    public const string ServicesDir = "/var/tmp/sandbox-services";

    /// <summary>Port of <c>REQUESTS_DIR</c>.</summary>
    public const string RequestsDir = "requests";

    /// <summary>Port of <c>RESPONSES_DIR</c>.</summary>
    public const string ResponsesDir = "responses";

    /// <summary>Port of <c>SERVICES_DIR_MODE</c>: the sticky world-writable mode of the shared parent.</summary>
    public const string ServicesDirMode = "1777";

    /// <summary>Port of <c>POLLING_INTERVAL</c>: how often the generated client polls for its response.</summary>
    public static readonly TimeSpan ClientPollingInterval = TimeSpan.FromSeconds(0.1);

    /// <summary>Port of <c>NORMAL_EXIT_DRAIN_TIMEOUT</c>: grace period for in-flight handlers once <c>until()</c> holds.</summary>
    public static readonly TimeSpan NormalExitDrainTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Port of <c>SandboxEnvironment.default_polling_interval()</c> (2 seconds).</summary>
    public static readonly TimeSpan DefaultHostPollingInterval = TimeSpan.FromSeconds(2);

    /// <summary>Port of <c>DockerSandboxEnvironment.default_polling_interval()</c> (0.2 seconds).</summary>
    public static readonly TimeSpan DockerPollingInterval = TimeSpan.FromSeconds(0.2);

    private const string IdField = "id";

    private const string MethodField = "method";

    private const string ParamsField = "params";

    private const string ResultField = "result";

    private const string ErrorField = "error";

    private static readonly TimeSpan ExecTimeout = TimeSpan.FromSeconds(600);

    private readonly string _name;

    private readonly ISandboxEnvironment? _sandbox;

    private readonly string? _user;

    private readonly string _serviceDir;

    private readonly string _rootServiceDir;

    private readonly Dictionary<string, SandboxServiceMethod> _methods = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, Task> _inFlight = new(StringComparer.Ordinal);

    private string _requestsDir = "";

    private string _responsesDir = "";

    private string _clientScript = "";

    private LimitExceededException? _limitExceeded;

    /// <summary>
    /// Port of <c>SandboxService(name, sandbox, user, instance)</c>: <paramref name="name"/> must be a 1-128 character
    /// ASCII Python identifier, <paramref name="instance"/> (which nests the service under <c>NAME/INSTANCE/</c>)
    /// a 1-128 character ASCII filename token.
    /// </summary>
    public SandboxService(string name, ISandboxEnvironment? sandbox, string? user = null, string? instance = null)
    {
        if (!IsServiceName(name))
        {
            throw new ArgumentException($"invalid service name: '{name}' (must be a 1-128 character ASCII Python identifier)", nameof(name));
        }

        _name = name;
        _sandbox = sandbox;
        _user = user;
        _rootServiceDir = $"{ServicesDir}/{name}";
        _serviceDir = _rootServiceDir;
        if (instance is not null)
        {
            if (!IsFilenameToken(instance))
            {
                throw new ArgumentException($"invalid instance: '{instance}' (must be a 1-128 character ASCII filename token)", nameof(instance));
            }

            _serviceDir = $"{_rootServiceDir}/{instance}";
        }
    }

    /// <summary>The service name.</summary>
    public string Name => _name;

    /// <summary>The directory the service publishes under (<c>/var/tmp/sandbox-services/NAME[/INSTANCE]</c>).</summary>
    public string ServiceDir => _serviceDir;

    /// <summary>The <c>requests/</c> directory once started.</summary>
    public string RequestsPath => _requestsDir;

    /// <summary>The <c>responses/</c> directory once started.</summary>
    public string ResponsesPath => _responsesDir;

    /// <summary>The generated client module's path once started.</summary>
    public string ClientScriptPath => _clientScript;

    /// <summary>Requests dispatched and not yet answered.</summary>
    public int InFlightCount => _inFlight.Count;

    /// <summary>Port of <c>SandboxService.add_method</c>.</summary>
    public void AddMethod(string name, SandboxServiceMethod method)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(method);
        _methods[name] = method;
    }

    /// <summary>
    /// Port of <c>sandbox_service(name, methods, until, sandbox, user, instance, polling_interval, requires_python)</c>:
    /// starts the service, polls <c>requests/</c> every <paramref name="pollingInterval"/> until
    /// <paramref name="until"/> returns true (it is evaluated before every poll, so a caller can refresh a view
    /// from it), then gives in-flight handlers <see cref="NormalExitDrainTimeout"/> to finish before cancelling them.
    /// </summary>
    public static async Task RunAsync(
        string name,
        IReadOnlyDictionary<string, SandboxServiceMethod> methods,
        Func<bool> until,
        ISandboxEnvironment sandbox,
        string? user = null,
        string? instance = null,
        TimeSpan? pollingInterval = null,
        bool requiresPython = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(methods);
        ArgumentNullException.ThrowIfNull(until);
        ArgumentNullException.ThrowIfNull(sandbox);

        if (requiresPython)
        {
            await ValidateSandboxPythonAsync(name, sandbox, user, cancellationToken).ConfigureAwait(false);
        }

        var interval = pollingInterval ?? DefaultPollingInterval(sandbox);
        var service = new SandboxService(name, sandbox, user, instance);
        foreach (var (methodName, method) in methods)
        {
            service.AddMethod(methodName, method);
        }

        await service.StartAsync(cancellationToken).ConfigureAwait(false);

        using var handlers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            while (!until())
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                await service.SafeDispatchAsync(handlers.Token).ConfigureAwait(false);
                service.ThrowIfLimitExceeded();
            }

            var deadline = DateTime.UtcNow + NormalExitDrainTimeout;
            while (service.InFlightCount > 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            handlers.Cancel();
            await service.AwaitInFlightAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Port of <c>sandbox_service_script(name)</c>: the client module generated for a service of this name.</summary>
    public static string ClientScript(string name) => new SandboxService(name, null).GenerateClient();

    /// <summary>Port of <c>default_polling_interval()</c> as a lookup over the known environments: both docker environments (bare and compose) answer with the docker interval.</summary>
    public static TimeSpan DefaultPollingInterval(ISandboxEnvironment sandbox) =>
        sandbox is DockerSandboxEnvironment or DockerComposeSandboxEnvironment ? DockerPollingInterval : DefaultHostPollingInterval;

    /// <summary>Port of <c>validate_sandbox_python</c>: <see cref="PrerequisiteError"/> when <c>python3</c> is not on the sandbox path.</summary>
    public static async Task ValidateSandboxPythonAsync(string serviceName, ISandboxEnvironment sandbox, string? user = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        var result = await sandbox.ExecAsync(["which", "python3"], user: user, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new PrerequisiteError($"The {serviceName} requires that Python be installed in the sandbox.");
        }
    }

    /// <summary>Port of <c>SandboxService.start()</c>: creates the service, request and response directories and writes the client module.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await EnsureServiceDirAsync(cancellationToken).ConfigureAwait(false);

        if (_requestsDir.Length != 0 || _responsesDir.Length != 0 || _clientScript.Length != 0)
        {
            throw new InvalidOperationException($"Sandbox service '{_name}' has already been started.");
        }

        _requestsDir = await CreateRpcDirAsync(RequestsDir, cancellationToken).ConfigureAwait(false);
        _responsesDir = await CreateRpcDirAsync(ResponsesDir, cancellationToken).ConfigureAwait(false);

        var clientScript = $"{_serviceDir}/{_name}.py";
        await WriteTextFileAsync(clientScript, GenerateClient(), cancellationToken).ConfigureAwait(false);
        _clientScript = clientScript;
    }

    /// <summary>
    /// Port of <c>SandboxService.handle_requests()</c> without a task group: serves every pending request and
    /// waits for the handlers it dispatched (a request already in flight is skipped, not served twice).
    /// </summary>
    public async Task HandleRequestsAsync(CancellationToken cancellationToken = default)
    {
        var dispatched = await DispatchAsync(cancellationToken).ConfigureAwait(false);
        if (dispatched.Count > 0)
        {
            await Task.WhenAll(dispatched).ConfigureAwait(false);
        }

        ThrowIfLimitExceeded();
    }

    private async Task SafeDispatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await DispatchAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ProviderLogger.Warning($"Error waiting for sandbox rpc: {ex.Message}");
        }
    }

    private async Task<List<Task>> DispatchAsync(CancellationToken cancellationToken)
    {
        // NUL-delimited so hostile filenames (e.g. containing newlines) can't forge extra entries in the listing.
        var result = await ExecAsync(["find", _requestsDir, "-maxdepth", "1", "-name", "*.json", "-type", "f", "-print0"], null, cancellationToken).ConfigureAwait(false);
        var dispatched = new List<Task>();
        if (!result.Success)
        {
            ProviderLogger.Warning($"Error listing requests for sandbox service '{_name}': {result.Stderr}");
            return dispatched;
        }

        foreach (var file in result.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // a request stays queued until it is answered, so skip the ones already running rather than serving twice
            var requestId = RemoveSuffix(PosixName(file), ".json");
            if (!_inFlight.TryAdd(requestId, Task.CompletedTask))
            {
                continue;
            }

            var task = HandleRequestTrackedAsync(file, requestId, cancellationToken);
            _inFlight.TryUpdate(requestId, task, Task.CompletedTask);
            dispatched.Add(task);
        }

        return dispatched;
    }

    private async Task HandleRequestTrackedAsync(string requestFile, string requestId, CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            await HandleRequestAsync(requestFile, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ProviderLogger.Warning($"Error handling sandbox service request: {ex.Message}");
        }
        finally
        {
            _inFlight.TryRemove(requestId, out _);
        }
    }

    private async Task HandleRequestAsync(string requestFile, CancellationToken cancellationToken)
    {
        if (PosixParent(requestFile) != _requestsDir)
        {
            ProviderLogger.Warning($"Ignoring sandbox service request outside '{_requestsDir}': '{requestFile}'");
            return;
        }

        var fileName = PosixName(requestFile);
        var requestId = RemoveSuffix(fileName, ".json");
        if (fileName != $"{requestId}.json" || !IsFilenameToken(requestId))
        {
            ProviderLogger.Warning($"Discarding sandbox service request with invalid filename: '{requestFile}'");
            await RemoveRequestFileAsync(requestFile, cancellationToken).ConfigureAwait(false);
            return;
        }

        // read request
        ExecResult read;
        try
        {
            read = await ExecAsync(["cat", "--", requestFile], null, cancellationToken).ConfigureAwait(false);
        }
        catch (OutputLimitExceededException ex)
        {
            // The request is too large to ever read: discard it and deliver an error response to unblock the client.
            await DiscardUnreadableRequestAsync(requestFile, requestId, $"exceeded the {ex.LimitDescription} sandbox exec output limit", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!read.Success)
        {
            throw new InvalidOperationException($"Error reading request '{requestFile}' for service {_name}: {read.Stderr}");
        }

        // parse request
        JsonNode? requestNode;
        try
        {
            requestNode = JsonNode.Parse(read.Stdout);
        }
        catch (JsonException)
        {
            // Either the file is still being written (retry on the next poll) or the provider silently truncated an
            // oversized read; the on-disk size tells them apart.
            var size = await RequestSizeAsync(requestFile, cancellationToken).ConfigureAwait(false);
            var limit = SandboxLimits.MaxExecOutputSize;
            if (size is not null && size > limit)
            {
                await DiscardUnreadableRequestAsync(requestFile, requestId, $"exceeds the {SandboxLimits.HumanReadableSize(limit)} service request read limit", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // log metadata only -- never the payload
                ProviderLogger.Warning(
                    $"JSON decoding error reading service request '{requestFile}' ({read.Stdout.Length} chars read, on-disk size "
                    + $"{(size is null ? "unknown" : size.Value.ToString(CultureInfo.InvariantCulture))} bytes); treating as an incomplete write and retrying.");
            }

            return;
        }

        if (requestNode is not JsonObject request)
        {
            await WriteResponseAsync(requestFile, requestId, null, $"Service request is not a dict (type={JsonKind(requestNode)})", cancellationToken).ConfigureAwait(false);
            return;
        }

        var requestDataId = StringField(request, IdField);
        if (requestDataId is null || !IsFilenameToken(requestDataId))
        {
            await WriteResponseAsync(requestFile, requestId, null, "Service request id is invalid", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (requestDataId != requestId)
        {
            await WriteResponseAsync(requestFile, requestId, null, "Service request id does not match request filename", cancellationToken).ConfigureAwait(false);
            return;
        }

        // read and validate params
        request.TryGetPropertyValue(MethodField, out var methodNode);
        request.TryGetPropertyValue(ParamsField, out var paramsNode);
        var methodName = StringField(request, MethodField);
        if (methodName is null)
        {
            await WriteResponseAsync(requestFile, requestId, null, $"Service {MethodField} not passed or not a string (type={JsonKind(methodNode)})", cancellationToken).ConfigureAwait(false);
        }
        else if (!_methods.TryGetValue(methodName, out var method))
        {
            await WriteResponseAsync(requestFile, requestId, null, $"Unknown method '{methodName}'", cancellationToken).ConfigureAwait(false);
        }
        else if (paramsNode is not JsonObject parameters)
        {
            await WriteResponseAsync(requestFile, requestId, null, $"{ParamsField} not passed or not a dict (type={paramsNode?.ToJsonString() ?? "None"})", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // all clear, call the method
            JsonNode? result;
            try
            {
                result = await method(parameters, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (LimitExceededException ex)
            {
                _limitExceeded ??= ex;
                await WriteResponseAsync(requestFile, requestId, null, $"Limit exceeded calling method {methodName}: {ex.Message}", cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                // Log host-side, but keep host details (paths, stack) out of the response delivered into the sandbox.
                ProviderLogger.Warning($"Error calling sandbox service method {methodName}: {ex}");
                await WriteResponseAsync(requestFile, requestId, null, $"Error calling method {methodName}: {ex.Message}", cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteResponseAsync(requestFile, requestId, result, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteResponseAsync(string requestFile, string requestId, JsonNode? result, string? error, CancellationToken cancellationToken)
    {
        var response = new JsonObject
        {
            [IdField] = requestId,
            [ResultField] = result?.DeepClone(),
            [ErrorField] = error,
        };
        await WriteTextFileAsync(ResponsePath(requestId), response.ToJsonString(), cancellationToken).ConfigureAwait(false);
        await RemoveRequestFileAsync(requestFile, cancellationToken).ConfigureAwait(false);
    }

    private string ResponsePath(string requestId)
    {
        if (!IsFilenameToken(requestId))
        {
            throw new ArgumentException($"invalid request id: '{requestId}'", nameof(requestId));
        }

        return $"{_responsesDir}/{requestId}.json";
    }

    private async Task RemoveRequestFileAsync(string requestFile, CancellationToken cancellationToken)
    {
        if (PosixParent(requestFile) != _requestsDir)
        {
            throw new ArgumentException($"request file is outside request directory: {requestFile}", nameof(requestFile));
        }

        var result = await ExecAsync(["rm", "-f", "--", requestFile], null, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Error removing request file '{requestFile}': {result.Stderr}");
        }
    }

    private async Task<long?> RequestSizeAsync(string requestFile, CancellationToken cancellationToken)
    {
        var result = await ExecAsync(["wc", "-c", "--", requestFile], null, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return null;
        }

        var first = result.Stdout.TrimStart().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return long.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : null;
    }

    private async Task DiscardUnreadableRequestAsync(string requestFile, string requestId, string detail, CancellationToken cancellationToken)
    {
        var error = $"Service '{_name}' request payload could not be read ({detail}); the request was discarded.";
        ProviderLogger.Warning($"{error} (request_file='{requestFile}')");
        await WriteResponseAsync(requestFile, requestId, null, error, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureServiceDirAsync(CancellationToken cancellationToken)
    {
        var sandbox = RequireSandbox();

        // Make the shared parent 1777 so users other than the one that created it can still place their service
        // dirs inside; run as the sandbox default user and best-effort, as in Python.
        try
        {
            await sandbox.ExecAsync(
                ["sh", "-c", $"mkdir -p {ServicesDir} && chmod {ServicesDirMode} {ServicesDir} 2>/dev/null; true"],
                timeout: ExecTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxTimeoutException)
        {
            throw new InvalidOperationException($"Timed out preparing shared services directory {ServicesDir}");
        }

        var result = await ExecAsync(["mkdir", "-p", _serviceDir], null, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            var parent = PosixParent(_serviceDir);
            var writable = await ExecAsync(["test", "-w", parent], null, cancellationToken).ConfigureAwait(false);
            if (!writable.Success)
            {
                var user = _user ?? "the sandbox default user";
                throw new PrerequisiteError(
                    $"Sandbox service '{_name}' cannot create '{_serviceDir}': its parent directory '{parent}' is not writable by user '{user}'. "
                    + "Another service may have created it with restrictive permissions, or claimed this name.");
            }

            throw new InvalidOperationException($"Error creating service directory '{_serviceDir}' for sandbox service '{_name}': {result.Stderr}");
        }

        // Squat check: test -O passes iff the path is owned by the effective uid (the service user).
        var dirsToCheck = new List<string> { _serviceDir };
        if (_serviceDir != _rootServiceDir)
        {
            dirsToCheck.Add(_rootServiceDir);
        }

        foreach (var path in dirsToCheck)
        {
            var owned = await ExecAsync(["test", "-O", path], null, cancellationToken).ConfigureAwait(false);
            if (!owned.Success)
            {
                var user = _user ?? "the sandbox default user";
                throw new PrerequisiteError(
                    $"Sandbox service '{_name}' cannot start: '{path}' exists but is not owned by user '{user}'. Another service may have claimed this name.");
            }
        }
    }

    private async Task<string> CreateRpcDirAsync(string name, CancellationToken cancellationToken)
    {
        var rpcDir = $"{_serviceDir}/{name}";
        await ExecAsync(["rm", "-rf", rpcDir], null, cancellationToken).ConfigureAwait(false);
        var result = await ExecAsync(["mkdir", "-p", rpcDir], null, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Error creating rpc directory '{name}' for sandbox '{_name}': {result.Stderr}");
        }

        return rpcDir;
    }

    private async Task WriteTextFileAsync(string file, string contents, CancellationToken cancellationToken)
    {
        var result = await ExecAsync(["tee", "--", file], contents, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Failed to write file '{file}' into container: {result.Stderr}");
        }
    }

    private async Task<ExecResult> ExecAsync(IReadOnlyList<string> cmd, string? input, CancellationToken cancellationToken)
    {
        var sandbox = RequireSandbox();
        try
        {
            return await sandbox.ExecAsync(cmd, input: input, user: _user, timeout: ExecTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxTimeoutException)
        {
            throw new InvalidOperationException($"Timed out executing command {string.Join(" ", cmd)} in sandbox");
        }
    }

    private ISandboxEnvironment RequireSandbox() =>
        _sandbox ?? throw new InvalidOperationException($"Sandbox service '{_name}' was created without a sandbox (script generation only).");

    private void ThrowIfLimitExceeded()
    {
        if (_limitExceeded is { } limit)
        {
            _limitExceeded = null;
            throw limit;
        }
    }

    private async Task AwaitInFlightAsync()
    {
        var pending = _inFlight.Values.ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // handlers log their own failures; cancellation of the remainder is the expected outcome here
        }
    }

    /// <summary>Port of <c>_generate_client()</c>: the Python module the sandbox imports to call this service.</summary>
    internal string GenerateClient()
    {
        var name = _name;
        var interval = ClientPollingInterval.TotalSeconds.ToString(CultureInfo.InvariantCulture);
        var instanceVar = name.ToUpperInvariant() + "_INSTANCE";
        return $$"""

            from typing import Any

            def call_{{name}}(method: str, **params: Any) -> Any:
                from time import sleep
                request_id = _write_{{name}}_request(method, **params)
                while True:
                    sleep({{interval}})
                    success, result = _read_{{name}}_response(request_id, method)
                    if success:
                        return result

            async def call_{{name}}_async(method: str, **params: Any) -> Any:
                from asyncio import sleep
                request_id = _write_{{name}}_request(method, **params)
                while True:
                    await sleep({{interval}})
                    success, result = _read_{{name}}_response(request_id, method)
                    if success:
                        return result

            def _write_{{name}}_request(method: str, **params: Any) -> str:
                from json import dump
                from uuid import uuid4

                requests_dir = _{{name}}_service_dir("{{RequestsDir}}")
                request_id = str(uuid4())
                request_data = dict({{IdField}}=request_id, {{MethodField}}=method, {{ParamsField}}=params)
                request_path = requests_dir / (request_id + ".json")
                with open(request_path, "w") as f:
                    dump(request_data, f)
                return request_id

            def _read_{{name}}_response(request_id: str, method: str) -> tuple[bool, Any]:
                from json import JSONDecodeError, load

                responses_dir = _{{name}}_service_dir("{{ResponsesDir}}")
                response_path = responses_dir / (request_id + ".json")
                if response_path.exists():
                    # read and remove the file
                    with open(response_path, "r") as f:
                        # it's possible the file is still being written so
                        # just catch and wait for another retry if this occurs
                        try:
                            response = load(f)
                        except JSONDecodeError:
                            return False, None
                    response_path.unlink()

                    # raise error if we have one
                    if response.get("{{ErrorField}}", None) is not None:
                        raise Exception(response["{{ErrorField}}"])

                    # return response if we have one
                    elif "{{ResultField}}" in response:
                        return True, response["{{ResultField}}"]

                    # invalid response
                    else:
                        raise RuntimeError(
                            "No {{ErrorField}} or {{ResultField}} field in response for method " + method
                        )
                else:
                    return False, None

            def _{{name}}_service_dir(subdir: str) -> Any:
                import os
                from pathlib import Path
                service_dir = Path("{{_rootServiceDir}}")
                instance = os.environ.get("{{instanceVar}}", None)
                if instance is not None:
                    service_dir = service_dir / instance
                return service_dir / subdir

            """;
    }

    private static bool IsServiceName(string? value) => value is not null && ServiceNamePattern().IsMatch(value);

    private static bool IsFilenameToken(string? value) => value is not null && FilenameTokenPattern().IsMatch(value);

    private static string? StringField(JsonObject request, string name) =>
        request.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string JsonKind(JsonNode? node) => node switch
    {
        null => "<class 'NoneType'>",
        JsonObject => "<class 'dict'>",
        JsonArray => "<class 'list'>",
        JsonValue value when value.TryGetValue<string>(out _) => "<class 'str'>",
        JsonValue value when value.TryGetValue<bool>(out _) => "<class 'bool'>",
        _ => "<class 'number'>",
    };

    private static string PosixParent(string path)
    {
        var index = path.LastIndexOf('/');
        return index switch
        {
            < 0 => "",
            0 => "/",
            _ => path[..index],
        };
    }

    private static string PosixName(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? path : path[(index + 1)..];
    }

    private static string RemoveSuffix(string value, string suffix) =>
        value.EndsWith(suffix, StringComparison.Ordinal) ? value[..^suffix.Length] : value;

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z")]
    private static partial Regex ServiceNamePattern();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z")]
    private static partial Regex FilenameTokenPattern();
}

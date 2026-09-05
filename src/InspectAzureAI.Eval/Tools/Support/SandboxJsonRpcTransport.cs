using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tools.Support;

/// <summary>
/// Port of <c>util/_sandbox/_json_rpc_transport.py</c> <c>SandboxJSONRPCTransport</c>: runs
/// <c>{cli} exec</c> in the sandbox with the JSON-RPC request on stdin and returns stdout. When the cli is
/// the injected <see cref="SandboxToolSupport.SandboxCli"/> the response is bounded by the sandbox exec
/// output limit (advertised to the cli through <see cref="ResponseMaxBytesEnv"/>) and an oversized response
/// arrives as a chunk envelope that is reassembled with continuation requests and released afterwards.
/// </summary>
public sealed partial class SandboxJsonRpcTransport : IJsonRpcTransport
{
    public const string ResponseChunkMethod = "__inspect_json_rpc_response_chunk__";

    public const string ResponseChunkField = "__inspect_json_rpc_response_chunk__";

    public const int ResponseChunkVersion = 1;

    public const string ResponseMaxBytesEnv = "INSPECT_SANDBOX_JSON_RPC_RESPONSE_MAX_BYTES";

    /// <summary>Budget for the best-effort release of a spilled response (Python: <c>timeout=5, timeout_retry=False</c>).</summary>
    public static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(5);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly bool _responseChunking;

    /// <param name="sandbox">The sandbox environment to use.</param>
    /// <param name="cli">The path to the cli available in the sandbox.</param>
    public SandboxJsonRpcTransport(ISandboxEnvironment sandbox, string cli)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(cli);
        Sandbox = sandbox;
        Cli = cli;
        _responseChunking = cli == SandboxToolSupport.SandboxCli;
    }

    public ISandboxEnvironment Sandbox { get; }

    public string Cli { get; }

    public async Task<string> CallAsync(string method, JsonNode? parameters, bool isNotification, JsonRpcCallOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(options);
        var request = JsonRpc.CreateRequest(method, parameters, isNotification);
        long? maxResponseBytes = _responseChunking ? SandboxLimits.MaxExecOutputSize : null;
        var response = await SandboxExecAsync(request, JsonRpc.RpcCallDescription(method, parameters), options, maxResponseBytes, cancellationToken).ConfigureAwait(false);
        if (maxResponseBytes is not { } limit)
        {
            return response;
        }

        return await CompleteChunkedResponseAsync(response, options, limit, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SandboxExecAsync(string request, string description, JsonRpcCallOptions options, long? maxResponseBytes, CancellationToken cancellationToken)
    {
        Dictionary<string, string>? env = null;
        if (maxResponseBytes is { } limit)
        {
            env = new Dictionary<string, string> { [ResponseMaxBytesEnv] = limit.ToString(CultureInfo.InvariantCulture) };
        }

        var result = await Sandbox.ExecAsync(
            [Cli, "exec"],
            input: request,
            env: env,
            user: options.User,
            timeout: options.Timeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            // Prefer stderr, but fall back to stdout: some failures (an MCP server crash, an entrypoint
            // error) only surface in stdout because the sandbox CLI wrote its diagnostic there.
            var detail = result.Stderr.Length > 0 ? result.Stderr : result.Stdout.Length > 0 ? result.Stdout : "(no output captured)";
            throw new InvalidOperationException($"Sandbox.exec failure executing {description}: {detail}");
        }

        return result.Stdout;
    }

    private async Task<string> CompleteChunkedResponseAsync(string response, JsonRpcCallOptions options, long maxResponseBytes, CancellationToken cancellationToken)
    {
        var chunk = ParseResponseChunk(response, requireChunk: false);
        if (chunk is null)
        {
            return response;
        }

        var handle = chunk.Handle;
        var totalSize = chunk.TotalSize;
        var responseBytes = new MemoryStream();
        long expectedOffset = 0;
        try
        {
            while (true)
            {
                ValidateResponseChunk(chunk, handle, expectedOffset, totalSize);
                responseBytes.Write(chunk.Data);
                if (chunk.Done)
                {
                    if (responseBytes.Length != totalSize)
                    {
                        throw new InvalidOperationException("Chunked JSON-RPC response size did not match metadata");
                    }

                    try
                    {
                        return StrictUtf8.GetString(responseBytes.GetBuffer(), 0, (int)responseBytes.Length);
                    }
                    catch (DecoderFallbackException)
                    {
                        throw new InvalidOperationException("Chunked JSON-RPC response was not valid UTF-8");
                    }
                }

                expectedOffset = chunk.NextOffset;
                var next = await SandboxExecAsync(
                    JsonRpc.CreateRequest(ResponseChunkMethod, new JsonObject { ["handle"] = handle, ["offset"] = expectedOffset }, isNotification: false),
                    "chunked JSON-RPC response continuation",
                    options,
                    maxResponseBytes,
                    cancellationToken).ConfigureAwait(false);
                chunk = ParseResponseChunk(next, requireChunk: true)!;
            }
        }
        finally
        {
            await ReleaseChunkedResponseAsync(handle, options, maxResponseBytes, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReleaseChunkedResponseAsync(string handle, JsonRpcCallOptions options, long maxResponseBytes, CancellationToken cancellationToken)
    {
        var cleanupOptions = options with { Timeout = ReleaseTimeout };
        try
        {
            await SandboxExecAsync(
                JsonRpc.CreateRequest(ResponseChunkMethod, new JsonObject { ["handle"] = handle, ["release"] = true }, isNotification: false),
                "chunked JSON-RPC response cleanup",
                cleanupOptions,
                maxResponseBytes,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Best effort, as in Python's `suppress(Exception)`: the sandbox reaps stale chunks on its own.
            ProviderLogger.Info($"Chunked JSON-RPC response cleanup failed for handle {handle}: {ex.Message}");
        }
    }

    private sealed record ResponseChunk(string Handle, long Offset, long NextOffset, long TotalSize, bool Done, byte[] Data);

    private static ResponseChunk? ParseResponseChunk(string response, bool requireChunk)
    {
        JsonNode? payload;
        try
        {
            payload = PythonJson.Loads(response);
        }
        catch (JsonException)
        {
            if (requireChunk)
            {
                throw new InvalidOperationException("Chunk continuation did not return valid JSON");
            }

            return null;
        }

        if (payload is not JsonObject envelope)
        {
            if (requireChunk)
            {
                throw new InvalidOperationException("Chunk continuation did not return a JSON object");
            }

            return null;
        }

        if (!envelope.ContainsKey(ResponseChunkField))
        {
            if (requireChunk)
            {
                if (envelope.ContainsKey("error"))
                {
                    throw new InvalidOperationException($"Chunked JSON-RPC response fetch failed: {PythonErrorRepr(envelope["error"])}");
                }

                throw new InvalidOperationException("Chunk continuation did not return chunk metadata");
            }

            return null;
        }

        if (envelope["jsonrpc"] is not JsonValue versionValue || !versionValue.TryGetValue<string>(out var version) || version != "2.0")
        {
            throw new InvalidOperationException("Chunked JSON-RPC response had an invalid protocol version");
        }

        if (envelope[ResponseChunkField] is not JsonObject metadata)
        {
            throw new InvalidOperationException("Chunked JSON-RPC response metadata was not an object");
        }

        if (metadata["version"] is not JsonValue chunkVersion || !TryGetInteger(chunkVersion, out var chunkVersionNumber) || chunkVersionNumber != ResponseChunkVersion)
        {
            throw new InvalidOperationException("Unsupported chunked JSON-RPC response version");
        }

        var offset = ChunkInt(metadata, "offset");
        var nextOffset = ChunkInt(metadata, "next_offset");
        var totalSize = ChunkInt(metadata, "total_size");
        if (metadata["handle"] is not JsonValue handleValue || !handleValue.TryGetValue<string>(out var handle) || !ChunkHandleRegex().IsMatch(handle))
        {
            throw new InvalidOperationException("Invalid chunked JSON-RPC response handle");
        }

        if (metadata["done"] is not JsonValue doneValue || !doneValue.TryGetValue<bool>(out var done))
        {
            throw new InvalidOperationException("Invalid chunked JSON-RPC response completion flag");
        }

        if (metadata["chunk"] is not JsonValue chunkValue || !chunkValue.TryGetValue<string>(out var encoded))
        {
            throw new InvalidOperationException("Invalid chunked JSON-RPC response payload");
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Invalid base64 in chunked JSON-RPC response");
        }

        return new ResponseChunk(handle, offset, nextOffset, totalSize, done, data);
    }

    private static long ChunkInt(JsonObject metadata, string field)
    {
        if (metadata[field] is JsonValue value && TryGetInteger(value, out var integer))
        {
            return integer;
        }

        throw new InvalidOperationException($"Invalid chunked JSON-RPC response {field}");
    }

    private static void ValidateResponseChunk(ResponseChunk chunk, string expectedHandle, long expectedOffset, long expectedTotalSize)
    {
        if (chunk.Handle != expectedHandle)
        {
            throw new InvalidOperationException("Chunked JSON-RPC response handle changed");
        }

        if (chunk.Offset != expectedOffset)
        {
            throw new InvalidOperationException("Chunked JSON-RPC response chunks arrived out of order");
        }

        if (chunk.TotalSize != expectedTotalSize || chunk.TotalSize <= 0)
        {
            throw new InvalidOperationException("Chunked JSON-RPC response total size changed");
        }

        if (chunk.Data.Length == 0 || chunk.NextOffset != chunk.Offset + chunk.Data.Length)
        {
            throw new InvalidOperationException("Chunked JSON-RPC response did not make valid progress");
        }

        if (chunk.NextOffset > chunk.TotalSize)
        {
            throw new InvalidOperationException("Chunked JSON-RPC response exceeded its declared size");
        }

        if (chunk.Done != (chunk.NextOffset == chunk.TotalSize))
        {
            throw new InvalidOperationException("Chunked JSON-RPC response completion metadata was invalid");
        }
    }

    private static bool TryGetInteger(JsonValue value, out long integer)
    {
        integer = 0;
        if (value.TryGetValue<bool>(out _))
        {
            return false;
        }

        if (value.TryGetValue<long>(out integer))
        {
            return true;
        }

        if (value.TryGetValue<int>(out var small))
        {
            integer = small;
            return true;
        }

        return false;
    }

    /// <summary>Python's <c>{payload['error']}</c>: the dict repr, e.g. <c>{'code': -32000, 'message': 'chunk missing'}</c>.</summary>
    private static string PythonErrorRepr(JsonNode? error)
    {
        if (error is not JsonObject obj)
        {
            return error?.ToJsonString() ?? "None";
        }

        return "{" + string.Join(", ", obj.Select(pair => $"'{pair.Key}': {Repr(pair.Value)}")) + "}";

        static string Repr(JsonNode? node) => node switch
        {
            null => "None",
            JsonValue value when value.TryGetValue<string>(out var text) => "'" + text.Replace("'", "\\'", StringComparison.Ordinal) + "'",
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag ? "True" : "False",
            JsonValue value when TryGetInteger(value, out var integer) => integer.ToString(CultureInfo.InvariantCulture),
            JsonObject nested => PythonErrorRepr(nested),
            _ => node.ToJsonString(),
        };
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex ChunkHandleRegex();
}

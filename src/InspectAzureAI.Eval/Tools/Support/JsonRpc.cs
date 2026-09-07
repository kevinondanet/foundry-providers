using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tools.Support;

/// <summary>Port of <c>_util/_json_rpc.py</c> <c>JSONRPCError</c>: the error object of a JSON-RPC 2.0 error response.</summary>
public sealed record JsonRpcError(int Code, string Message, JsonNode? Data = null);

/// <summary>
/// Port of the <c>transport_extra_args</c> the sandbox transport understands: the exec timeout and the
/// user the injected CLI runs as (the "tools user", root when the tree could be installed as root).
/// </summary>
public sealed record JsonRpcCallOptions(TimeSpan? Timeout = null, string? User = null)
{
    public static readonly JsonRpcCallOptions None = new();
}

/// <summary>
/// Port of <c>_util/_json_rpc.py</c> <c>JSONRPCTransport</c>: serializes one JSON-RPC request, sends it
/// through some channel (docker exec, a socket, ...) and returns the raw response text for parsing.
/// </summary>
public interface IJsonRpcTransport
{
    /// <summary>Sends <paramref name="method"/> with <paramref name="parameters"/> and returns the raw response (empty for a notification).</summary>
    Task<string> CallAsync(string method, JsonNode? parameters, bool isNotification, JsonRpcCallOptions options, CancellationToken cancellationToken = default);
}

/// <summary>
/// Port of <c>JSONRPCErrorMapper</c>: maps the three categories of JSON-RPC error codes to exceptions, so
/// a tool-layer mapper can turn them into <see cref="ToolError"/>s fed back to the model rather than
/// failures that end the sample.
/// </summary>
public abstract class JsonRpcErrorMapper
{
    /// <summary>Maps a server-defined error code (-32000..-32099) to an exception.</summary>
    public abstract Exception ServerError(int code, string message, string method, JsonNode? parameters);

    /// <summary>Maps a -32602 (Invalid params) error to an exception.</summary>
    public abstract Exception InvalidParams(string message, string method, JsonNode? parameters);

    /// <summary>Maps a -32603 (Internal error) error to an exception.</summary>
    public abstract Exception InternalError(string message, string method, JsonNode? parameters);
}

/// <summary>Port of <c>GenericJSONRPCErrorMapper</c>: <c>RuntimeError</c> becomes <see cref="InvalidOperationException"/>, <c>ValueError</c> becomes <see cref="ArgumentException"/>.</summary>
public sealed class GenericJsonRpcErrorMapper : JsonRpcErrorMapper
{
    public static readonly GenericJsonRpcErrorMapper Instance = new();

    public override Exception ServerError(int code, string message, string method, JsonNode? parameters) => new InvalidOperationException(message);

    public override Exception InvalidParams(string message, string method, JsonNode? parameters) => new ArgumentException(message);

    public override Exception InternalError(string message, string method, JsonNode? parameters) => new InvalidOperationException(message);
}

/// <summary>
/// Port of <c>_util/_json_rpc.py</c>: request creation (the same <c>json.dumps</c> wire format, ids counting
/// from 666), response parsing, error-code mapping and the typed request helpers
/// (<c>exec_scalar_request</c>, <c>exec_model_request</c>, <c>exec_notification</c>).
/// </summary>
public static class JsonRpc
{
    /// <summary>Python's <c>id_generator = count(666)</c>; process-wide so ids never repeat within a run.</summary>
    private static long _nextId = 665;

    private static readonly JsonSerializerOptions ModelOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>
    /// Port of <c>create_json_rpc_request</c>: <c>{"jsonrpc": "2.0", "method": m, "params": p, "id": n}</c> as
    /// <c>json.dumps</c> renders it; <c>params</c> is omitted when null or empty, <c>id</c> for a
    /// notification, and null values are stripped recursively (<c>remove_none_values</c>).
    /// </summary>
    public static string CreateRequest(string method, JsonNode? parameters, bool isNotification)
    {
        ArgumentNullException.ThrowIfNull(method);
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (HasParams(parameters))
        {
            request["params"] = parameters!.DeepClone();
        }

        if (!isNotification)
        {
            request["id"] = Interlocked.Increment(ref _nextId);
        }

        return PythonJson.Dumps(RemoveNoneValues(request));
    }

    /// <summary>Port of <c>remove_none_values</c>: drops null object members and null list items recursively (returns a detached copy).</summary>
    public static JsonNode? RemoveNoneValues(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                var cleaned = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    if (value is not null)
                    {
                        cleaned[key] = RemoveNoneValues(value);
                    }
                }

                return cleaned;
            case JsonArray array:
                var items = new JsonArray();
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        items.Add(RemoveNoneValues(item));
                    }
                }

                return items;
            default:
                return node.DeepClone();
        }
    }

    /// <summary>
    /// Port of <c>rpc_call_description</c>: <c>subtract(minuend: 42, subtrahend: 23)</c> for a params object,
    /// <c>subtract(42, 23)</c> for a params list, <c>version()</c> for none.
    /// </summary>
    public static string RpcCallDescription(string method, JsonNode? parameters)
    {
        ArgumentNullException.ThrowIfNull(method);
        var normalized = parameters switch
        {
            null => [],
            JsonArray array => array.Select(PythonStr),
            JsonObject obj => obj.Select(pair => $"{pair.Key}: {PythonStr(pair.Value)}"),
            _ => [PythonStr(parameters)],
        };
        return $"{method}({string.Join(", ", normalized)})";
    }

    /// <summary>
    /// Port of <c>parse_json_rpc_response</c>: returns the <c>result</c> of a success response, throws the
    /// mapped exception for an error response and <see cref="InvalidOperationException"/> for anything that
    /// is not a JSON-RPC 2.0 response.
    /// </summary>
    public static JsonNode? ParseResponse(string responseText, string method, JsonNode? parameters, JsonRpcErrorMapper errorMapper)
    {
        ArgumentNullException.ThrowIfNull(responseText);
        ArgumentNullException.ThrowIfNull(errorMapper);
        JsonNode? node;
        try
        {
            node = PythonJson.Loads(responseText);
        }
        catch (JsonException)
        {
            throw Unexpected(responseText, method, parameters);
        }

        if (node is not JsonObject response
            || !IsProtocolVersion(response["jsonrpc"])
            || !IsResponseId(response["id"]))
        {
            throw Unexpected(responseText, method, parameters);
        }

        if (response.ContainsKey("result"))
        {
            return response["result"]?.DeepClone();
        }

        if (response["error"] is JsonObject error
            && error["code"] is JsonValue codeValue && TryGetInteger(codeValue, out var code)
            && error["message"] is JsonValue messageValue && messageValue.TryGetValue<string>(out var message))
        {
            throw ExceptionForRpcResponseError((int)code, message, method, parameters, errorMapper);
        }

        throw Unexpected(responseText, method, parameters);
    }

    /// <summary>
    /// Port of <c>exception_for_rpc_response_error</c>: server errors (-32099..-32000), invalid params
    /// (-32602) and internal errors (-32603) go through the mapper; the request-oriented codes (-32600,
    /// -32601, -32700) are a coding error and become an <see cref="InvalidOperationException"/>.
    /// </summary>
    public static Exception ExceptionForRpcResponseError(int code, string message, string method, JsonNode? parameters, JsonRpcErrorMapper errorMapper)
    {
        ArgumentNullException.ThrowIfNull(errorMapper);
        if (code is >= -32099 and <= -32000)
        {
            return errorMapper.ServerError(code, message, method, parameters);
        }

        if (code == -32602)
        {
            return errorMapper.InvalidParams(message, method, parameters);
        }

        if (code == -32603)
        {
            return errorMapper.InternalError(message, method, parameters);
        }

        var description = method.Length > 0 && HasParams(parameters) ? $"  {RpcCallDescription(method, parameters)}" : "";
        return new InvalidOperationException($"Error executing tool command{description}: code={code.ToString(CultureInfo.InvariantCulture)} {message}");
    }

    /// <summary>
    /// Port of <c>exec_scalar_request</c>: sends the request and returns its result as <typeparamref name="T"/>
    /// (<see cref="string"/>, <see cref="bool"/>, <see cref="int"/>, <see cref="long"/> or <see cref="double"/>);
    /// a result of another JSON type is an <see cref="InvalidOperationException"/> (Python's <c>ValueError</c>).
    /// </summary>
    public static async Task<T> ExecScalarRequestAsync<T>(
        string method,
        JsonNode? parameters,
        IJsonRpcTransport transport,
        JsonRpcErrorMapper errorMapper,
        JsonRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecRequestAsync(method, parameters, transport, errorMapper, options ?? JsonRpcCallOptions.None, cancellationToken).ConfigureAwait(false);
        return ConvertScalar<T>(result);
    }

    /// <summary>
    /// Port of <c>exec_model_request</c>: sends the request and deserializes its (object) result into
    /// <typeparamref name="T"/> with snake_case member names; a missing required member or a result of the
    /// wrong shape is an <see cref="InvalidOperationException"/> (Python's pydantic <c>ValidationError</c>).
    /// </summary>
    public static async Task<T> ExecModelRequestAsync<T>(
        string method,
        JsonNode? parameters,
        IJsonRpcTransport transport,
        JsonRpcErrorMapper errorMapper,
        JsonRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecRequestAsync(method, parameters, transport, errorMapper, options ?? JsonRpcCallOptions.None, cancellationToken).ConfigureAwait(false);
        var description = RpcCallDescription(method, parameters);
        if (result is not JsonObject)
        {
            throw new InvalidOperationException($"Expected {typeof(T).Name} result for {description}, got {PythonTypeName(result)}");
        }

        try
        {
            return result.Deserialize<T>(ModelOptions)
                ?? throw new InvalidOperationException($"Expected {typeof(T).Name} result for {description}, got None");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid {typeof(T).Name} result for {description}: {ex.Message}", ex);
        }
    }

    /// <summary>Port of <c>exec_notification</c>: a notification expects no response; any output is an <see cref="InvalidOperationException"/>.</summary>
    public static async Task ExecNotificationAsync(
        string method,
        JsonNode? parameters,
        IJsonRpcTransport transport,
        JsonRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var stdout = await transport.CallAsync(method, parameters, isNotification: true, options ?? JsonRpcCallOptions.None, cancellationToken).ConfigureAwait(false);
        if (stdout.Trim().Length > 0)
        {
            throw new InvalidOperationException($"Unexpected response to a Notification: {RpcCallDescription(method, parameters)}: {stdout}");
        }
    }

    /// <summary>Python's <c>&lt;class 'str'&gt;</c>-style name of a JSON value's type, for the type-mismatch messages.</summary>
    public static string PythonTypeName(JsonNode? node) => node switch
    {
        null => "<class 'NoneType'>",
        JsonObject => "<class 'dict'>",
        JsonArray => "<class 'list'>",
        JsonValue value when value.TryGetValue<string>(out _) => "<class 'str'>",
        JsonValue value when value.TryGetValue<bool>(out _) => "<class 'bool'>",
        JsonValue value when TryGetInteger(value, out _) => "<class 'int'>",
        JsonValue value when value.TryGetValue<double>(out _) => "<class 'float'>",
        _ => $"<class '{node.GetType().Name}'>",
    };

    private static async Task<JsonNode?> ExecRequestAsync(
        string method,
        JsonNode? parameters,
        IJsonRpcTransport transport,
        JsonRpcErrorMapper errorMapper,
        JsonRpcCallOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(errorMapper);
        var response = await transport.CallAsync(method, parameters, isNotification: false, options, cancellationToken).ConfigureAwait(false);
        return ParseResponse(response, method, parameters, errorMapper);
    }

    private static T ConvertScalar<T>(JsonNode? result)
    {
        var type = typeof(T);
        if (result is JsonValue value)
        {
            if (type == typeof(string) && value.TryGetValue<string>(out var text))
            {
                return (T)(object)text;
            }

            if (type == typeof(bool) && value.TryGetValue<bool>(out var flag))
            {
                return (T)(object)flag;
            }

            if (type == typeof(long) && TryGetInteger(value, out var whole))
            {
                return (T)(object)whole;
            }

            if (type == typeof(int) && TryGetInteger(value, out var number) && number is >= int.MinValue and <= int.MaxValue)
            {
                return (T)(object)(int)number;
            }

            if (type == typeof(double) && !value.TryGetValue<bool>(out _) && value.TryGetValue<double>(out var real))
            {
                return (T)(object)real;
            }
        }

        throw new InvalidOperationException($"Expected {PythonTypeName(type)} result, got {PythonTypeName(result)}");
    }

    private static string PythonTypeName(Type type) =>
        type == typeof(string) ? "<class 'str'>"
        : type == typeof(bool) ? "<class 'bool'>"
        : type == typeof(int) || type == typeof(long) ? "<class 'int'>"
        : type == typeof(double) ? "<class 'float'>"
        : $"<class '{type.Name}'>";

    private static bool HasParams(JsonNode? parameters) => parameters switch
    {
        JsonObject obj => obj.Count > 0,
        JsonArray array => array.Count > 0,
        null => false,
        _ => true,
    };

    private static bool IsProtocolVersion(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var version) && version == "2.0";

    private static bool IsResponseId(JsonNode? node) =>
        node is JsonValue value && (value.TryGetValue<string>(out _) || (!value.TryGetValue<bool>(out _) && value.TryGetValue<double>(out _)));

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

        // A JsonValue created from a boxed int (rather than a parsed element) only answers to its own type.
        if (value.TryGetValue<int>(out var small))
        {
            integer = small;
            return true;
        }

        return false;
    }

    private static InvalidOperationException Unexpected(string responseText, string method, JsonNode? parameters) =>
        new($"Unexpected JSON RPC response to request {RpcCallDescription(method, parameters)}: {responseText}");

    /// <summary>Python's <c>str()</c> of a decoded JSON value (a bare string prints unquoted; containers use repr).</summary>
    private static string PythonStr(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : PythonRepr(node);

    private static string PythonRepr(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "None";
            case JsonObject obj:
                return "{" + string.Join(", ", obj.Select(pair => $"{PythonRepr(JsonValue.Create(pair.Key))}: {PythonRepr(pair.Value)}")) + "}";
            case JsonArray array:
                return "[" + string.Join(", ", array.Select(PythonRepr)) + "]";
            case JsonValue value when value.TryGetValue<string>(out var text):
                return "'" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
            case JsonValue value when value.TryGetValue<bool>(out var flag):
                return flag ? "True" : "False";
            case JsonValue value when TryGetInteger(value, out var integer):
                return integer.ToString(CultureInfo.InvariantCulture);
            case JsonValue value when value.TryGetValue<double>(out var real):
                return real.ToString("R", CultureInfo.InvariantCulture);
            default:
                return node.ToJsonString();
        }
    }
}

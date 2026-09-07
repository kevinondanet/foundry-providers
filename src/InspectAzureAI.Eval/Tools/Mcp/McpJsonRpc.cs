using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Tools.Mcp;

/// <summary>
/// Port of the parts of <c>_util/_json_rpc.py</c> the MCP code uses: request envelopes, response parsing and the
/// error-code to exception mapping shared by the MCP error mapper (<c>_local.py</c>) and the sandbox-tools error
/// mapper (<c>_sandbox_tools_utils/_error_mapper.py</c>).
/// </summary>
internal static class McpJsonRpc
{
    // Python's id_generator = count(666).
    private static long _lastId = 665;

    /// <summary>Port of <c>_McpErrorMapper.server_error</c>: MCP servers are opaque, so every server-defined code is fed back to the model.</summary>
    public static Exception McpServerError(int code, string message) => new ToolError(message);

    /// <summary>Port of <c>SandboxToolsErrorMapper.server_error</c>: -32099 is a tool exception in the container; anything else is a bug.</summary>
    public static Exception SandboxToolsServerError(int code, string message) =>
        code == -32099 ? new ToolError(message) : new InvalidOperationException(message);

    /// <summary>Port of <c>create_json_rpc_request</c>: <c>params</c> is omitted when empty and nulls are stripped from it recursively; notifications carry no id.</summary>
    public static JsonObject CreateRequest(string method, JsonNode? parameters, bool isNotification)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is JsonObject { Count: > 0 } or JsonArray { Count: > 0 })
        {
            request["params"] = RemoveNoneValues(parameters);
        }

        if (!isNotification)
        {
            request["id"] = Interlocked.Increment(ref _lastId);
        }

        return request;
    }

    /// <summary>Port of <c>remove_none_values</c>: a copy with null object members and null array items dropped at every depth.</summary>
    public static JsonNode? RemoveNoneValues(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    if (value is not null)
                    {
                        result[key] = RemoveNoneValues(value);
                    }
                }

                return result;
            case JsonArray array:
                return new JsonArray(array.Where(item => item is not null).Select(RemoveNoneValues).ToArray());
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>
    /// Port of <c>parse_json_rpc_response</c>: the <c>result</c> of a success response; an error response becomes
    /// the exception <see cref="ExceptionForRpcResponseError"/> maps it to; anything else is an
    /// <see cref="InvalidOperationException"/> quoting the text.
    /// </summary>
    public static JsonNode? ParseResponse(string responseText, string method, JsonNode? parameters, Func<int, string, Exception> serverError)
    {
        JsonNode? node = null;
        try
        {
            node = JsonNode.Parse(responseText);
        }
        catch (JsonException)
        {
            // handled below as an unexpected response
        }

        if (node is JsonObject response && response["jsonrpc"] is JsonValue version && version.TryGetValue<string>(out var jsonrpc) && jsonrpc == "2.0" && response.ContainsKey("id"))
        {
            if (response.ContainsKey("result"))
            {
                return response["result"];
            }

            if (response["error"] is JsonObject error
                && error["code"] is JsonValue codeValue && codeValue.TryGetValue<int>(out var code)
                && error["message"] is JsonValue messageValue && messageValue.TryGetValue<string>(out var message))
            {
                throw ExceptionForRpcResponseError(code, message, method, parameters, serverError);
            }
        }

        throw new InvalidOperationException($"Unexpected JSON RPC response to request {RpcCallDescription(method, parameters)}: {responseText}");
    }

    /// <summary>
    /// Port of <c>exception_for_rpc_response_error</c>: server-defined codes (-32000..-32099) go to
    /// <paramref name="serverError"/>, -32602 is a <see cref="ToolParsingError"/>, -32603 a <see cref="ToolError"/>,
    /// and the request-oriented codes (-32600, -32601, -32700) are a code bug reported as an
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    public static Exception ExceptionForRpcResponseError(int code, string message, string method, JsonNode? parameters, Func<int, string, Exception> serverError, Exception? inner = null)
    {
        if (code is >= -32099 and <= -32000)
        {
            return serverError(code, message);
        }

        if (code == -32602)
        {
            return new ToolParsingError(message);
        }

        if (code == -32603)
        {
            return new ToolError(message);
        }

        var description = !string.IsNullOrEmpty(method) && parameters is JsonObject { Count: > 0 } or JsonArray { Count: > 0 }
            ? $"  {RpcCallDescription(method, parameters)}"
            : "";
        return new InvalidOperationException($"Error executing tool command{description}: code={code} {message}", inner);
    }

    /// <summary>Port of <c>rpc_call_description</c>: <c>method(k: v, ...)</c> for an object, <c>method(a, b)</c> for an array.</summary>
    public static string RpcCallDescription(string method, JsonNode? parameters)
    {
        var normalized = parameters switch
        {
            null => [],
            JsonArray array => array.Select(PythonStr),
            JsonObject obj => obj.Select(kv => $"{kv.Key}: {PythonStr(kv.Value)}"),
            _ => [PythonStr(parameters)],
        };
        return $"{method}({string.Join(", ", normalized)})";
    }

    // Python's str() of the decoded JSON value; containers fall back to their JSON text.
    private static string PythonStr(JsonNode? node) => node switch
    {
        null => "None",
        JsonValue value when value.TryGetValue<string>(out var s) => s,
        JsonValue value when value.TryGetValue<bool>(out var b) => b ? "True" : "False",
        JsonValue value when value.TryGetValue<double>(out var d) => d.ToString(CultureInfo.InvariantCulture),
        _ => node.ToJsonString(),
    };
}

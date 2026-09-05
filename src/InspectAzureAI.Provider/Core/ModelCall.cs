using System.Text.Json.Nodes;

namespace InspectAzureAI.Provider.Core;

/// <summary>
/// Redaction callback applied to every node of a recorded request/response (port of
/// <c>ModelCallFilter</c> in <c>src/inspect_ai/model/_model_call.py</c>). <paramref name="key"/> is
/// the property name the value sits under (null for the root and for list items).
/// </summary>
public delegate JsonNode? ModelCallFilter(string? key, JsonNode? value);

/// <summary>
/// Record of a raw model API call (port of <c>ModelCall</c>, <c>src/inspect_ai/model/_model_call.py</c>).
/// The request snapshot is taken before the call; <see cref="SetResponse"/> / <see cref="SetError"/>
/// fill in the response afterwards, applying the same filter.
/// </summary>
public sealed class ModelCall
{
    private readonly ModelCallFilter? _filter;

    private ModelCall(JsonObject request, ModelCallFilter? filter)
    {
        Request = request;
        _filter = filter;
    }

    /// <summary>Raw request (redacted).</summary>
    public JsonObject Request { get; }

    /// <summary>Raw response (redacted), or the error payload when <see cref="Error"/> is true.</summary>
    public JsonNode? Response { get; private set; }

    /// <summary>True when the call ended in an error.</summary>
    public bool? Error { get; private set; }

    /// <summary>Time spent on the request, if recorded.</summary>
    public double? Time { get; private set; }

    /// <summary>
    /// Port of <c>ModelCall.call_refs</c>: ranges into the call pool of a condensed log that stand in for the
    /// request's messages (kept verbatim by the log reader; not resolved by the provider).
    /// </summary>
    public IReadOnlyList<CallRef>? CallRefs { get; set; }

    /// <summary>Port of <c>ModelCall.call_key</c>: the request key under which the messages were pooled.</summary>
    public string? CallKey { get; set; }

    /// <summary>Port of <c>ModelCall.create</c> (with a null response).</summary>
    public static ModelCall Create(JsonObject request, ModelCallFilter? filter = null)
    {
        var filtered = filter is null ? request.DeepClone().AsObject() : (JsonObject)WalkJsonValue(null, request, filter)!;
        return new ModelCall(filtered, filter);
    }

    /// <summary>Port of <c>ModelCall.set_response</c>.</summary>
    public void SetResponse(JsonNode? response, double? time = null)
    {
        Response = _filter is null ? response?.DeepClone() : WalkJsonValue(null, response, _filter);
        Time = time;
    }

    /// <summary>Port of <c>ModelCall.set_error</c>.</summary>
    public void SetError(JsonNode? response, double? time = null)
    {
        Error = true;
        SetResponse(response, time);
    }

    /// <summary>Port of <c>_walk_json_value</c>: applies the filter to every node, recursing into lists and objects.</summary>
    public static JsonNode? WalkJsonValue(string? key, JsonNode? value, ModelCallFilter filter)
    {
        value = filter(key, value);
        switch (value)
        {
            case JsonArray array:
                return new JsonArray(array.Select(v => WalkJsonValue(null, v, filter)).ToArray());
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (k, v) in obj)
                {
                    result[k] = WalkJsonValue(k, v, filter);
                }

                return result;
            default:
                return value?.DeepClone();
        }
    }
}

/// <summary>Port of a <c>ModelCall.call_refs</c> entry: a <c>(start, end_exclusive)</c> range into the call pool.</summary>
public readonly record struct CallRef(int Start, int End);

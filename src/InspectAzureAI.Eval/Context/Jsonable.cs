using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Solvers;

namespace InspectAzureAI.Eval.Context;

/// <summary>
/// Port of the <c>to_jsonable_python(..., exclude_none=True, fallback=lambda _x: None)</c> snapshots the change
/// tracking diffs: <c>store_jsonable</c>, <c>dict_jsonable</c> and <c>state_jsonable</c>. Values are serialized
/// with the log's options (so messages, tool info and model output take their Python shape and null record
/// members are dropped); a value the serializer cannot handle becomes null, as Python's fallback makes it.
/// Dictionaries and sequences are converted element by element so one unserializable item does not lose its siblings.
/// </summary>
public static class Jsonable
{
    /// <summary>Port of <c>store_jsonable</c>: the store's contents as a JSON object (insertion order).</summary>
    public static JsonObject FromStore(Store store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return FromDictionary(store.ToDictionary());
    }

    /// <summary>Port of <c>dict_jsonable</c>.</summary>
    public static JsonObject FromDictionary(IEnumerable<KeyValuePair<string, object?>> data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var obj = new JsonObject();
        foreach (var (key, value) in data)
        {
            obj[key] = FromValue(value);
        }

        return obj;
    }

    /// <summary>
    /// Port of <c>state_jsonable</c>: <c>messages</c>, <c>tools</c> (as tool info), <c>tool_choice</c>, <c>store</c>,
    /// <c>output</c>, <c>completed</c> and <c>metadata</c>, in that order; <c>tool_choice</c> is an explicit null when unset.
    /// </summary>
    public static JsonObject FromState(TaskState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new JsonObject
        {
            ["messages"] = FromValue(state.Messages.ToList()),
            ["tools"] = FromValue(state.Tools.Select(tool => tool.ToInfo()).ToList()),
            ["tool_choice"] = FromValue(state.ToolChoice),
            ["store"] = FromStore(state.Store),
            ["output"] = FromValue(state.Output),
            ["completed"] = state.Completed,
            ["metadata"] = FromDictionary(state.Metadata),
        };
    }

    /// <summary>
    /// A value as JSON: nodes are cloned, strings and primitives are wrapped, dictionaries and sequences convert
    /// element by element, anything else goes through the log serializer. A <see cref="TaskState"/> is null (Python
    /// cannot serialize it either), as is any value the serializer rejects.
    /// </summary>
    public static JsonNode? FromValue(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonNode node:
                return node.DeepClone();
            case JsonElement element:
                return element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : JsonNode.Parse(element.GetRawText());
            case string text:
                return JsonValue.Create(text);
            case bool flag:
                return JsonValue.Create(flag);
            case float single:
                return Serialize((double)single);
            case TaskState:
                return null;
            case IDictionary dictionary:
                var obj = new JsonObject();
                foreach (DictionaryEntry entry in dictionary)
                {
                    obj[entry.Key.ToString() ?? ""] = FromValue(entry.Value);
                }

                return obj;
            case byte[]:
                return Serialize(value);
            case IEnumerable sequence:
                var array = new JsonArray();
                foreach (var item in sequence)
                {
                    array.Add(FromValue(item));
                }

                return array;
            default:
                return Serialize(value);
        }
    }

    private static JsonNode? Serialize(object value)
    {
        try
        {
            return JsonSerializer.SerializeToNode(value, value.GetType(), EvalLogWriter.Options);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            return null;
        }
    }
}

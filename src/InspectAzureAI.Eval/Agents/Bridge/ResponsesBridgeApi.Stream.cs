using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Agents.Bridge;

/// <summary>Responses SSE synthesis (port of the proxy's Responses stream, <c>proxy.py:737-1405</c>, with deviations D-R5 and D-R6).</summary>
public static partial class ResponsesBridgeApi
{
    /// <summary>
    /// The named events for a finished response, built after generation. Every event's data carries <c>type</c> and a
    /// <c>sequence_number</c> that starts at 1.
    /// <list type="number">
    /// <item><c>response.created</c> and <c>response.in_progress</c> carry the response with <c>status: in_progress</c>, <c>output: []</c> and <c>usage: null</c>.</item>
    /// <item>Each output item gets <c>response.output_item.added</c> with an empty in-progress copy, then its per-type events, then <c>response.output_item.done</c>.</item>
    /// <item>Per type: a message gets content-part, text-delta and text-done events for each part (one delta per part, none when empty); a function call gets arguments delta and done; a custom tool call gets input delta and done, both carrying <c>call_id</c>; a web search call gets its three lifecycle events.</item>
    /// <item><c>response.completed</c> carries the response with <c>status</c> forced to <c>completed</c>.</item>
    /// </list>
    /// <c>response.incomplete</c> is never sent, because Codex treats it as fatal.
    /// </summary>
    public static IReadOnlyList<SseEvent> StreamEvents(JsonObject response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var events = new List<SseEvent>();
        var sequence = 0;

        void Emit(string type, JsonObject fields)
        {
            var data = new JsonObject { ["type"] = type, ["sequence_number"] = ++sequence };
            foreach (var key in fields.Select(pair => pair.Key).ToList())
            {
                var value = fields[key];
                fields.Remove(key);
                data[key] = value;
            }

            events.Add(new SseEvent(type, data));
        }

        var inProgress = response.DeepClone().AsObject();
        inProgress["status"] = "in_progress";
        inProgress["output"] = new JsonArray();
        inProgress["usage"] = null;
        Emit("response.created", new JsonObject { ["response"] = inProgress.DeepClone() });
        Emit("response.in_progress", new JsonObject { ["response"] = inProgress });

        var output = response["output"] as JsonArray ?? [];
        for (var index = 0; index < output.Count; index++)
        {
            if (output[index] is not JsonObject item)
            {
                continue;
            }

            var itemId = BridgeJson.GetString(item, "id") ?? $"item_{index}";
            var itemType = BridgeJson.GetString(item, "type");
            var added = item.DeepClone().AsObject();
            added["status"] = "in_progress";
            switch (itemType)
            {
                case "message":
                    added["content"] = new JsonArray();
                    break;
                case "function_call":
                    added["arguments"] = "";
                    break;
                case "custom_tool_call":
                    added["input"] = "";
                    break;
            }

            Emit("response.output_item.added", new JsonObject { ["output_index"] = index, ["item"] = added });

            switch (itemType)
            {
                case "message":
                    var parts = item["content"] as JsonArray ?? [];
                    for (var partIndex = 0; partIndex < parts.Count; partIndex++)
                    {
                        if (parts[partIndex] is not JsonObject part)
                        {
                            continue;
                        }

                        var refusal = BridgeJson.GetString(part, "type") == "refusal";
                        var text = BridgeJson.GetString(part, refusal ? "refusal" : "text") ?? "";
                        Emit("response.content_part.added", new JsonObject
                        {
                            ["item_id"] = itemId,
                            ["output_index"] = index,
                            ["content_index"] = partIndex,
                            ["part"] = refusal
                                ? new JsonObject { ["type"] = "refusal", ["refusal"] = "" }
                                : new JsonObject { ["type"] = "output_text", ["text"] = "", ["annotations"] = new JsonArray() },
                        });
                        if (refusal)
                        {
                            if (text.Length > 0)
                            {
                                Emit("response.refusal.delta", new JsonObject { ["item_id"] = itemId, ["output_index"] = index, ["content_index"] = partIndex, ["delta"] = text });
                            }

                            Emit("response.refusal.done", new JsonObject { ["item_id"] = itemId, ["output_index"] = index, ["content_index"] = partIndex, ["refusal"] = text });
                        }
                        else
                        {
                            if (text.Length > 0)
                            {
                                Emit("response.output_text.delta", new JsonObject
                                {
                                    ["item_id"] = itemId,
                                    ["output_index"] = index,
                                    ["content_index"] = partIndex,
                                    ["delta"] = text,
                                    ["logprobs"] = new JsonArray(),
                                });
                            }

                            Emit("response.output_text.done", new JsonObject
                            {
                                ["item_id"] = itemId,
                                ["output_index"] = index,
                                ["content_index"] = partIndex,
                                ["text"] = text,
                                ["logprobs"] = new JsonArray(),
                            });
                        }

                        Emit("response.content_part.done", new JsonObject
                        {
                            ["item_id"] = itemId,
                            ["output_index"] = index,
                            ["content_index"] = partIndex,
                            ["part"] = part.DeepClone(),
                        });
                    }

                    break;

                case "function_call":
                    var arguments = BridgeJson.GetString(item, "arguments") ?? "";
                    Emit("response.function_call_arguments.delta", new JsonObject { ["item_id"] = itemId, ["output_index"] = index, ["delta"] = arguments });
                    Emit("response.function_call_arguments.done", new JsonObject
                    {
                        ["item_id"] = itemId,
                        ["output_index"] = index,
                        ["name"] = item["name"]?.DeepClone(),
                        ["arguments"] = arguments,
                    });
                    break;

                case "custom_tool_call":
                    var input = BridgeJson.GetString(item, "input") ?? "";
                    Emit("response.custom_tool_call_input.delta", new JsonObject
                    {
                        ["item_id"] = itemId,
                        ["call_id"] = item["call_id"]?.DeepClone(),
                        ["output_index"] = index,
                        ["delta"] = input,
                    });
                    Emit("response.custom_tool_call_input.done", new JsonObject
                    {
                        ["item_id"] = itemId,
                        ["call_id"] = item["call_id"]?.DeepClone(),
                        ["output_index"] = index,
                        ["input"] = input,
                    });
                    break;

                case "web_search_call":
                    foreach (var phase in new[] { "in_progress", "searching", "completed" })
                    {
                        Emit($"response.web_search_call.{phase}", new JsonObject { ["item_id"] = itemId, ["output_index"] = index });
                    }

                    break;
            }

            Emit("response.output_item.done", new JsonObject { ["output_index"] = index, ["item"] = item.DeepClone() });
        }

        var completed = response.DeepClone().AsObject();
        completed["status"] = "completed";
        Emit("response.completed", new JsonObject { ["response"] = completed });
        return events;
    }

    /// <summary>
    /// A best-effort <c>response.failed</c> event for an error raised after the stream started. Its <c>error.code</c> is
    /// <c>server_error</c> for a 5xx status and <c>invalid_request_error</c> otherwise.
    /// </summary>
    public static SseEvent FailedEvent(string responseId, int sequenceNumber, int status, string message)
    {
        ArgumentNullException.ThrowIfNull(responseId);
        ArgumentNullException.ThrowIfNull(message);
        return new SseEvent("response.failed", new JsonObject
        {
            ["type"] = "response.failed",
            ["sequence_number"] = sequenceNumber,
            ["response"] = new JsonObject
            {
                ["id"] = responseId,
                ["object"] = "response",
                ["status"] = "failed",
                ["output"] = new JsonArray(),
                ["error"] = new JsonObject
                {
                    ["code"] = status >= 500 ? "server_error" : "invalid_request_error",
                    ["message"] = message,
                },
            },
        });
    }
}

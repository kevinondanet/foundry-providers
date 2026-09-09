using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Provider.OpenAI;

/// <summary>
/// Reads a Responses API event stream, delivering text, reasoning and tool-call deltas as they arrive, and
/// returns the complete response object carried by the terminal event (<c>response.completed</c>,
/// <c>response.incomplete</c> or <c>response.failed</c>), so the streamed and non-streamed paths parse the same
/// document (port of the streaming branch of <c>generate_responses</c>). An <c>error</c> event ends the
/// stream with a synthetic <c>{error: {...}}</c> response so the caller's error mapping applies to it too.
/// Every other event only reports progress (the stall detector).
/// </summary>
public static class ResponsesStreamAccumulator
{
    public const string NoTerminalEventError = "Streaming response ended without a terminal response event.";

    public static async Task<JsonObject> AccumulateAsync(IAsyncEnumerable<JsonObject> events, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        JsonObject? terminal = null;
        var functionCalls = new Dictionary<string, (string CallId, string Name)>(StringComparer.Ordinal);
        await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            switch (evt["type"]?.ToString())
            {
                case "response.completed" or "response.incomplete" or "response.failed":
                    terminal = evt["response"] is JsonObject response ? response.DeepClone().AsObject() : terminal;
                    ModelStreamObserver.ReportModelStreamProgress(OutputTokens(terminal));
                    break;
                case "error":
                    return ErrorResponse(evt);
                case "response.output_text.delta":
                    await ModelStreamObserver.ReportModelStreamDeltaAsync(new StreamTextEvent(evt["delta"]?.ToString() ?? "")).ConfigureAwait(false);
                    break;
                case "response.reasoning_text.delta" or "response.reasoning_summary_text.delta":
                    await ModelStreamObserver.ReportModelStreamDeltaAsync(new StreamReasoningEvent(evt["delta"]?.ToString() ?? "")).ConfigureAwait(false);
                    break;
                case "response.output_item.added" when evt["item"] is JsonObject added && added["type"]?.ToString() == "function_call":
                    functionCalls[added["id"]?.ToString() ?? ""] = (added["call_id"]?.ToString() ?? "", added["name"]?.ToString() ?? "");
                    ModelStreamObserver.ReportModelStreamProgress();
                    break;
                case "response.function_call_arguments.delta":
                {
                    var (callId, name) = functionCalls.TryGetValue(evt["item_id"]?.ToString() ?? "", out var call) ? call : (null, null);
                    await ModelStreamObserver.ReportModelStreamDeltaAsync(new StreamToolCallEvent(callId, name, evt["delta"]?.ToString() ?? "")).ConfigureAwait(false);
                    break;
                }

                default:
                    ModelStreamObserver.ReportModelStreamProgress();
                    break;
            }
        }

        return terminal ?? throw new ServiceResponseException(NoTerminalEventError);
    }

    private static int? OutputTokens(JsonObject? response) =>
        response?["usage"]?["output_tokens"] is JsonValue value && value.TryGetValue<int>(out var tokens) ? tokens : null;

    /// <summary>The stream-level <c>error</c> event (<c>{type, code, message}</c>, or <c>{type, error: {...}}</c>) as a failed response.</summary>
    private static JsonObject ErrorResponse(JsonObject evt)
    {
        var error = evt["error"] is JsonObject nested ? nested.DeepClone().AsObject() : new JsonObject();
        error["code"] ??= evt["code"]?.DeepClone();
        error["type"] ??= evt["error_type"]?.DeepClone();
        error["message"] ??= evt["message"]?.DeepClone() ?? JsonValue.Create("stream error");
        return new JsonObject { ["status"] = "failed", ["output"] = new JsonArray(), ["error"] = error };
    }
}

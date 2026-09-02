using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Provider;

/// <summary>Accumulated state for one streamed choice (port of <c>_StreamChoice</c> in <c>azureai.py</c>).</summary>
internal sealed class StreamChoice
{
    public List<string> Content { get; } = [];

    /// <summary>Keyed by wire index (or synthesised slot).</summary>
    public SortedDictionary<int, StreamToolCall> ToolCalls { get; } = [];

    public string? FinishReason { get; set; }

    public JsonObject? ContentFilterResults { get; set; }

    /// <summary>
    /// Port of <c>_StreamChoice.tool_call_slot</c>: an integer <c>index</c> wins; otherwise a fragment
    /// bearing an <c>id</c> (or the first fragment) starts a new slot and bare argument fragments
    /// extend the latest one.
    /// </summary>
    public int ToolCallSlot(JsonObject fragment)
    {
        if (fragment["index"] is JsonValue indexValue && indexValue.TryGetValue<int>(out var index))
        {
            return index;
        }

        if (PythonSemantics.Truthy(fragment["id"]) || ToolCalls.Count == 0)
        {
            return ToolCalls.Count > 0 ? ToolCalls.Keys.Max() + 1 : 0;
        }

        return ToolCalls.Keys.Max();
    }
}

/// <summary>One accumulated tool call: first id/name seen plus the argument fragments.</summary>
internal sealed class StreamToolCall
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public List<string> Arguments { get; } = [];
}

/// <summary>Port of <c>azureai_completion_from_stream</c> in <c>azureai.py</c>.</summary>
public static class AzureAIStreamAccumulator
{
    /// <summary>
    /// Consumes raw stream updates into a completion. Reports each update once to the ambient
    /// <see cref="ModelStreamObserver"/>: content/tool-call deltas from choice 0 only, and only when
    /// <see cref="ModelStreamObserver.ModelStreamRequested"/>; usage chunks report progress; updates
    /// that produced neither report a bare heartbeat. Per-choice <c>content_filter_results</c> keep the
    /// last non-empty annotation seen; tool-call fragments are assembled by slot.
    /// </summary>
    public static async Task<AzureChatCompletions> CompletionFromStreamAsync(IAsyncEnumerable<JsonObject> updates, CancellationToken cancellationToken = default)
    {
        ModelStreamObserver.ReportModelStreamStart();
        string? completionId = null;
        long? created = null;
        string? model = null;
        JsonObject? usage = null;
        var choices = new SortedDictionary<int, StreamChoice>();

        await foreach (var update in updates.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            completionId = PythonSemantics.Truthy(completionId) ? completionId : update["id"]?.ToString();
            created ??= update["created"] is JsonValue createdValue && createdValue.TryGetValue<long>(out var createdLong) ? createdLong : null;
            model = PythonSemantics.Truthy(model) ? model : update["model"]?.ToString();
            var updateUsage = update["usage"] as JsonObject;
            if (updateUsage is not null)
            {
                usage = updateUsage;
                ModelStreamObserver.ReportModelStreamProgress(updateUsage["completion_tokens"]?.GetValue<int>());
            }

            var deltasRequested = ModelStreamObserver.ModelStreamRequested();
            var reported = false;
            foreach (var updateChoice in (update["choices"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                var index = updateChoice["index"]?.GetValue<int>() ?? 0;
                if (!choices.TryGetValue(index, out var choice))
                {
                    choice = new StreamChoice();
                    choices[index] = choice;
                }

                if (updateChoice["finish_reason"] is not null)
                {
                    choice.FinishReason = updateChoice["finish_reason"]!.ToString();
                }

                if (updateChoice["content_filter_results"] is JsonObject { Count: > 0 } filterResults)
                {
                    choice.ContentFilterResults = filterResults;
                }

                var delta = updateChoice["delta"] as JsonObject ?? new JsonObject();
                var report = index == 0 && deltasRequested;
                var content = delta["content"]?.ToString();
                if (!string.IsNullOrEmpty(content))
                {
                    choice.Content.Add(content);
                    if (report)
                    {
                        await ModelStreamObserver.ReportModelStreamDeltaAsync(new StreamTextEvent(content)).ConfigureAwait(false);
                        reported = true;
                    }
                }

                foreach (var toolCall in (delta["tool_calls"] as JsonArray)?.OfType<JsonObject>() ?? [])
                {
                    var function = toolCall["function"] as JsonObject ?? new JsonObject();
                    var arguments = function["arguments"]?.ToString() ?? "";
                    var slot = choice.ToolCallSlot(toolCall);
                    if (!choice.ToolCalls.TryGetValue(slot, out var current))
                    {
                        current = new StreamToolCall();
                        choice.ToolCalls[slot] = current;
                    }

                    current.Id = PythonSemantics.Truthy(current.Id) ? current.Id : toolCall["id"]?.ToString();
                    current.Name = PythonSemantics.Truthy(current.Name) ? current.Name : function["name"]?.ToString();
                    if (arguments.Length > 0)
                    {
                        current.Arguments.Add(arguments);
                    }

                    if (report)
                    {
                        await ModelStreamObserver.ReportModelStreamDeltaAsync(
                            new StreamToolCallEvent(current.Id, current.Name, arguments)).ConfigureAwait(false);
                        reported = true;
                    }
                }
            }

            if (!reported && updateUsage is null)
            {
                ModelStreamObserver.ReportModelStreamProgress();
            }
        }

        if (completionId is null && model is null)
        {
            throw new InvalidOperationException("Streaming response ended without delivering any chunks.");
        }

        var response = new JsonObject
        {
            ["id"] = completionId ?? "",
            ["created"] = created ?? 0,
            ["model"] = model ?? "",
            ["object"] = "chat.completion",
        };
        var choicesArray = new JsonArray();
        foreach (var (index, choice) in choices)
        {
            var message = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = string.Concat(choice.Content),
            };
            if (choice.ToolCalls.Count > 0)
            {
                var toolCalls = new JsonArray();
                foreach (var (slot, toolCall) in choice.ToolCalls)
                {
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = PythonSemantics.Truthy(toolCall.Id) ? toolCall.Id : $"tool_call_{index}_{slot}",
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = toolCall.Name ?? "",
                            ["arguments"] = string.Concat(toolCall.Arguments),
                        },
                    });
                }

                message["tool_calls"] = toolCalls;
            }

            var choiceObject = new JsonObject
            {
                ["index"] = index,
                ["finish_reason"] = choice.FinishReason,
                ["message"] = message,
            };
            if (choice.ContentFilterResults is not null)
            {
                choiceObject["content_filter_results"] = choice.ContentFilterResults.DeepClone();
            }

            choicesArray.Add(choiceObject);
        }

        response["choices"] = choicesArray;
        if (usage is not null)
        {
            response["usage"] = usage.DeepClone();
        }

        return new AzureChatCompletions(response);
    }
}

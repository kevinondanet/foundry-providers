using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Tools;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Provider.OpenAI;

/// <summary>
/// Builds the <see cref="ModelOutput"/> from a Responses API response object (port of
/// <c>_chat_message_assistant_from_openai_response</c>, <c>model_usage_from_response_usage</c> and
/// <c>openai_handle_bad_request</c>). Reasoning items become <see cref="ContentReasoning"/> with the item id
/// as <see cref="ContentReasoning.Signature"/>, the encrypted blob as the reasoning text when the model exposes
/// nothing readable (<see cref="ContentReasoning.Redacted"/>) and the summary parts joined into
/// <see cref="ContentReasoning.Summary"/>. Output item types this route does not port (web search, MCP,
/// computer use, code interpreter, image generation) are skipped with a one-time warning rather than failing
/// the sample.
/// </summary>
public static class ResponsesOutput
{
    /// <summary>Provider name used in stop-details diagnostics.</summary>
    public const string ProviderName = "openai_responses";

    /// <summary>Parses a completed (or incomplete) response object into a single-choice output.</summary>
    public static ModelOutput Parse(JsonObject response, string deploymentName)
    {
        ArgumentNullException.ThrowIfNull(response);
        var items = new List<Content>();
        var toolCalls = new List<ToolCall>();
        var refusals = new List<string>();
        foreach (var node in response["output"]?.AsArray() ?? [])
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            switch (item["type"]?.ToString())
            {
                case "message":
                    foreach (var part in item["content"]?.AsArray() ?? [])
                    {
                        switch (part?["type"]?.ToString())
                        {
                            case "output_text":
                                items.Add(new ContentText(part["text"]?.ToString() ?? ""));
                                break;
                            case "refusal":
                                var refusal = part["refusal"]?.ToString() ?? "";
                                refusals.Add(refusal);
                                items.Add(new ContentText(refusal) { Refusal = true });
                                break;
                        }
                    }

                    break;
                case "reasoning":
                    items.Add(ReasoningFromItem(item));
                    break;
                case "function_call":
                    toolCalls.Add(ToolCallParsing.ParseToolCall(
                        item["call_id"]?.ToString() ?? item["id"]?.ToString() ?? "",
                        ResponsesTools.Unalias(item["name"]?.ToString() ?? ""),
                        item["arguments"]?.ToString()));
                    break;
                case { } other:
                    ProviderLogger.WarnOnce($"OpenAI Responses on Azure: output items of type '{other}' are not supported on this route and were ignored.");
                    break;
            }
        }

        var incompleteReason = response["incomplete_details"]?["reason"]?.ToString();
        var stopReason = toolCalls.Count > 0
            ? StopReason.ToolCalls
            : incompleteReason switch
            {
                "max_output_tokens" => StopReason.MaxTokens,
                "content_filter" => StopReason.ContentFilter,
                _ => StopReason.Stop,
            };
        var details = ModelOutputUtil.CollectStopDetails(ProviderName, () => StopDetails(incompleteReason, refusals));
        var model = response["model"]?.ToString() is { Length: > 0 } name ? name : deploymentName;
        var content = items.Count == 0 ? MessageContent.FromString("") : MessageContent.FromItems(items);
        var assistant = new ChatMessageAssistant(content, toolCalls.Count > 0 ? toolCalls : null, model, "generate");
        return new ModelOutput
        {
            Model = model,
            Choices = [new ChatCompletionChoice(assistant, stopReason, details)],
            Usage = Usage(response["usage"] as JsonObject),
        };
    }

    /// <summary>
    /// A reasoning output item: readable <c>content</c> text when the model exposes it (joined by newlines),
    /// otherwise the encrypted blob marked <see cref="ContentReasoning.Redacted"/>; the summary parts joined by
    /// blank lines; the item id as the signature so the item can be replayed.
    /// </summary>
    public static ContentReasoning ReasoningFromItem(JsonObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var readable = JoinText(item["content"] as JsonArray, "\n");
        var summary = JoinText(item["summary"] as JsonArray, "\n\n");
        var encrypted = item["encrypted_content"]?.ToString();
        var redacted = readable is null && encrypted is { Length: > 0 };
        return new ContentReasoning(readable ?? encrypted ?? "", item["id"]?.ToString(), redacted) { Summary = summary };
    }

    /// <summary>
    /// Usage with the cached (and cache-write) tokens taken out of <c>input_tokens</c>, as Python does: the API
    /// reports input tokens inclusive of the cache reads, and the cost code prices those separately.
    /// </summary>
    public static ModelUsage? Usage(JsonObject? usage)
    {
        if (usage is null)
        {
            return null;
        }

        var input = Int(usage["input_tokens"]);
        var output = Int(usage["output_tokens"]);
        var total = usage["total_tokens"] is null ? input + output : Int(usage["total_tokens"]);
        var cached = Int(usage["input_tokens_details"]?["cached_tokens"]);
        var cacheWrite = Int(usage["input_tokens_details"]?["cache_write_tokens"]);
        var reasoning = usage["output_tokens_details"]?["reasoning_tokens"];
        return new ModelUsage(input - cached - cacheWrite, output, total)
        {
            InputTokensCacheRead = cached > 0 ? cached : null,
            InputTokensCacheWrite = cacheWrite > 0 ? cacheWrite : null,
            ReasoningTokens = reasoning is null ? null : Int(reasoning),
        };
    }

    /// <summary>
    /// Port of <c>openai_handle_bad_request</c>: an error the model layer treats as an output rather than a
    /// failure. <c>context_length_exceeded</c> (or a message about the maximum context length) becomes a
    /// <c>model_length</c> stop; the content-policy codes (<c>content_filter</c>, <c>content_policy_violation</c>,
    /// <c>invalid_prompt</c>, <c>cyber_policy</c>, or an <c>invalid_request_error</c> saying the prompt was
    /// blocked) become a <c>content_filter</c> stop with the message as the explanation. Null for any other error.
    /// </summary>
    public static ModelOutput? RefusalOutput(string model, string? code, string? type, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var lower = message.ToLowerInvariant();
        if (code == "context_length_exceeded" || lower.Contains("maximum context length"))
        {
            return ModelOutput.FromContent(model, MessageContent.FromString(message), StopReason.ModelLength);
        }

        var filtered = code is "content_filter" or "content_policy_violation" or "invalid_prompt" or "cyber_policy"
            || (type == "invalid_request_error" && lower.Contains("blocked"));
        if (!filtered)
        {
            return null;
        }

        var details = new StopDetails
        {
            Type = "refusal",
            Explanation = message,
            Category = code == "cyber_policy" ? "cyber" : null,
            Categories = code == "cyber_policy" ? [new StopCategory("cyber")] : [],
        };
        return ModelOutput.FromContent(model, MessageContent.FromString(message), StopReason.ContentFilter, stopDetails: details);
    }

    private static StopDetails? StopDetails(string? incompleteReason, List<string> refusals)
    {
        var explanation = refusals.Count > 0 ? string.Join("\n", refusals) : null;
        if (incompleteReason == "content_filter")
        {
            return new StopDetails { Type = "content_filter", Explanation = explanation };
        }

        return explanation is null ? null : new StopDetails { Type = "refusal", Explanation = explanation };
    }

    private static string? JoinText(JsonArray? parts, string separator)
    {
        if (parts is null)
        {
            return null;
        }

        var texts = parts.Select(p => p?["text"]?.ToString()).Where(t => t is { Length: > 0 }).ToList();
        return texts.Count == 0 ? null : string.Join(separator, texts);
    }

    private static int Int(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var i) ? i : 0;
}

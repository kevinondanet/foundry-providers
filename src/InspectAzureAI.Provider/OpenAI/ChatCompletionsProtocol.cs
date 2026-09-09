using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Tools;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Provider.OpenAI;

/// <summary>Raw HTTP Chat Completions protocol. No Azure SDK or api-version parameters.</summary>
internal static class ChatCompletionsProtocol
{
    public static JsonObject Build(OpenAIModelApi api, IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, ToolChoice choice, GenerateConfig config, bool streaming)
    {
        var request = new JsonObject { ["model"] = api.ModelName, ["messages"] = new JsonArray(input.Select(m => (JsonNode?)Message(m, api.Gpt5Plus || api.OSeries)).ToArray()) };
        void Put(string key, object? value) { if (value is not null) request[key] = JsonSerializer.SerializeToNode(value); }
        if (tools.Count > 0)
        {
            request["tools"] = new JsonArray(tools.Select(t => (JsonNode?)new JsonObject { ["type"] = "function", ["function"] = new JsonObject {
                ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = JsonSchemaDump.Dump(t.Parameters.ToJson()) } }).ToArray());
            request["tool_choice"] = choice is ToolFunction f ? new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = f.Name } } : JsonValue.Create(choice.ToString() == "any" ? "required" : choice.ToString());
            if (!api.OSeries) Put("parallel_tool_calls", config.ParallelToolCalls);
        }
        var max = api.ModelName.Contains("vision", StringComparison.OrdinalIgnoreCase) ? Math.Max(config.MaxTokens ?? 0, 4096) : config.MaxTokens;
        Put(api.Gpt5Plus || api.OSeries ? "max_completion_tokens" : "max_tokens", max);
        if (!(api.Gpt5Plus || api.OSeries)) Put("temperature", config.Temperature);
        Put("top_p", config.TopP);
        Put("frequency_penalty", config.FrequencyPenalty);
        Put("presence_penalty", config.PresencePenalty);
        Put("stop", config.StopSeqs);
        Put("seed", config.Seed);
        Put("logit_bias", config.LogitBias);
        Put("n", config.NumChoices);
        Put("logprobs", config.Logprobs);
        Put("top_logprobs", config.TopLogprobs);
        if (!api.ModelName.Contains("gpt", StringComparison.OrdinalIgnoreCase) || api.Gpt5Plus) Put("reasoning_effort", config.ReasoningEffort);
        if (config.ReasoningMode is not null) ProviderLogger.WarnOnce("reasoning_mode requires the Responses API; Chat Completions ignores it.");
        if (config.ResponseSchema is { } schema) request["response_format"] = new JsonObject { ["type"] = "json_schema", ["json_schema"] = new JsonObject {
            ["name"] = schema.Name, ["description"] = schema.Description, ["schema"] = JsonSchemaDump.Dump(schema.JsonSchema.ToJson()), ["strict"] = schema.Strict is { } strict ? JsonValue.Create(strict) : null } };
        if (streaming) { request["stream"] = true; request["stream_options"] = new JsonObject { ["include_usage"] = true }; }
        return request;
    }
    private static JsonObject Message(ChatMessage message, bool developer)
    {
        switch (message)
        {
            case ChatMessageSystem system: return new() { ["role"] = developer ? "developer" : "system", ["content"] = system.Text };
            case ChatMessageUser user: return new() { ["role"] = "user", ["content"] = user.Content.IsString ? JsonValue.Create(user.Text) : new JsonArray(user.ContentList.Select(c => (JsonNode?)Part(c)).ToArray()) };
            case ChatMessageTool tool: return new() { ["role"] = "tool", ["content"] = tool.Error is not null ? "Error: " + tool.Error.Message : tool.Text, ["tool_call_id"] = tool.ToolCallId ?? "None" };
            case ChatMessageAssistant assistant:
                var content = assistant.Content.IsString ? assistant.Text : string.Concat(assistant.ContentList.Select(c => c switch {
                    ContentText text => "\n" + text.Text,
                    ContentReasoning { Redacted: false } reasoning => "\n<think>" + reasoning.Reasoning + "</think>\n",
                    _ => "" }));
                var result = new JsonObject { ["role"] = "assistant", ["content"] = content };
                if (assistant.ToolCalls is { Count: > 0 }) result["tool_calls"] = new JsonArray(assistant.ToolCalls.Select(c => (JsonNode?)new JsonObject {
                    ["type"] = "function", ["id"] = c.Id, ["function"] = new JsonObject { ["name"] = c.Function, ["arguments"] = PythonJson.Dumps(c.Arguments) } }).ToArray());
                return result;
            default: throw new PrerequisiteError("Unsupported Chat Completions message role.");
        }
    }
    private static JsonObject Part(Content content) => content switch
    {
        ContentText text => new() { ["type"] = "text", ["text"] = text.Text },
        ContentImage image => new() { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = ResponsesInput.ImagePart(image)["image_url"]!.DeepClone(), ["detail"] = image.Detail == "original" ? "high" : image.Detail } },
        _ => throw new PrerequisiteError("Direct Chat Completions supports text and image inputs on this route."),
    };
    public static ModelOutput Parse(JsonObject response, string model)
    {
        var choices = new List<ChatCompletionChoice>();
        foreach (var choice in (response["choices"] as JsonArray ?? []).OrderBy(c => c?["index"]?.GetValue<int>() ?? 0))
        {
            var msg = choice!["message"]!;
            var text = msg["content"]?.ToString() ?? msg["refusal"]?.ToString() ?? "";
            var content = new List<Content>();
            if (msg["reasoning_content"] is { } reasoning) content.Add(new ContentReasoning(reasoning.ToString()));
            content.Add(new ContentText(text) { Refusal = msg["refusal"] is not null ? true : null });
            var calls = (msg["tool_calls"] as JsonArray ?? []).Where(c => c?["type"]?.ToString() == "function").Select(c => ToolCallParsing.ParseToolCall(c!["id"]?.ToString() ?? "", c["function"]!["name"]!.ToString(), c["function"]!["arguments"]?.ToString())).ToList();
            var stop = choice["finish_reason"]?.ToString() switch { "stop" => StopReason.Stop, "length" => StopReason.MaxTokens, "tool_calls" or "function_call" => StopReason.ToolCalls, "content_filter" => StopReason.ContentFilter, _ => StopReason.Unknown };
            choices.Add(new(new ChatMessageAssistant(MessageContent.FromItems(content), calls.Count > 0 ? calls : null, response["model"]?.ToString() ?? model, "generate"), stop));
        }
        var usage = response["usage"];
        return new() { Model = response["model"]?.ToString() ?? model, Choices = choices, Usage = usage is null ? null : ResponsesOutput.Usage(new JsonObject {
            ["input_tokens"] = usage["prompt_tokens"]?.DeepClone(), ["output_tokens"] = usage["completion_tokens"]?.DeepClone(), ["total_tokens"] = usage["total_tokens"]?.DeepClone(),
            ["input_tokens_details"] = usage["prompt_tokens_details"]?.DeepClone(), ["output_tokens_details"] = usage["completion_tokens_details"]?.DeepClone() }) };
    }
    public static async Task<JsonObject> AccumulateAsync(IAsyncEnumerable<JsonObject> events, CancellationToken cancellationToken)
    {
        var result = new JsonObject();
        var choices = new SortedDictionary<int, JsonObject>();
        var calls = new Dictionary<int, SortedDictionary<int, JsonObject>>();
        await foreach (var update in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (update["error"] is { } error) throw new ProviderHttpException(error["code"]?.ToString() == "rate_limit_exceeded" ? 429 : 500, new Dictionary<string,string>(), new JsonObject { ["error"] = error.DeepClone() }.ToJsonString());
            if (update["model"] is { } model) result["model"] = model.DeepClone();
            if (update["usage"] is { } usage) { result["usage"] = usage.DeepClone(); ModelStreamObserver.ReportModelStreamProgress(usage["completion_tokens"]?.GetValue<int>()); }
            foreach (var choice in update["choices"] as JsonArray ?? [])
            {
                var index = choice!["index"]?.GetValue<int>() ?? 0;
                if (!choices.TryGetValue(index, out var output))
                {
                    output = new JsonObject { ["index"] = index, ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "" } };
                    choices[index] = output;
                    calls[index] = new();
                }
                if (choice["finish_reason"] is { } finish) output["finish_reason"] = finish.DeepClone();
                var message = output["message"]!.AsObject();
                var delta = choice["delta"];
                foreach (var key in new[] { "content", "refusal", "reasoning_content" })
                {
                    if (delta?[key] is not { } fragment) continue;
                    message[key] = (message[key]?.ToString() ?? "") + fragment.ToString();
                    if (index == 0) await ModelStreamObserver.ReportModelStreamDeltaAsync(key == "reasoning_content" ? new StreamReasoningEvent(fragment.ToString()) : new StreamTextEvent(fragment.ToString())).ConfigureAwait(false);
                }
                foreach (var part in delta?["tool_calls"] as JsonArray ?? [])
                {
                    var toolIndex = part!["index"]?.GetValue<int>() ?? 0;
                    if (!calls[index].TryGetValue(toolIndex, out var call)) calls[index][toolIndex] = call = new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "", ["arguments"] = "" } };
                    if (part["id"] is { } id) call["id"] = id.DeepClone();
                    foreach (var key in new[] { "name", "arguments" }) if (part["function"]?[key] is { } fragment) call["function"]![key] = call["function"]![key]!.ToString() + fragment.ToString();
                    if (index == 0) await ModelStreamObserver.ReportModelStreamDeltaAsync(new StreamToolCallEvent(call["id"]?.ToString(), call["function"]!["name"]?.ToString(), part["function"]?["arguments"]?.ToString() ?? "")).ConfigureAwait(false);
                }
            }
            ModelStreamObserver.ReportModelStreamProgress();
        }
        if (choices.Count == 0 || choices.Values.Any(c => c["finish_reason"] is null)) throw new ServiceResponseException("Chat Completions stream ended before all choices finished.");
        foreach (var pair in choices) if (calls[pair.Key].Count > 0) pair.Value["message"]!["tool_calls"] = new JsonArray(calls[pair.Key].Values.Select(c => (JsonNode?)c).ToArray());
        result["choices"] = new JsonArray(choices.Values.Select(c => (JsonNode?)c).ToArray());
        return result;
    }
}

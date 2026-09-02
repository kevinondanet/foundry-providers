using System.Text.Json;
using System.Text.Json.Nodes;

namespace InspectAzureAI.Provider;

/// <summary>
/// Dict-backed view of a chat-completions response, mirroring how the Python provider reads
/// <c>azure.ai.inference.models.ChatCompletions</c> (a <c>Mapping</c>): every accessor reads the raw
/// JSON so undeclared fields such as the per-choice <c>content_filter_results</c> survive. The .NET SDK's
/// typed <c>ChatCompletions</c> drops those fields, which is why the provider parses the raw response.
/// </summary>
public sealed class AzureChatCompletions
{
    public AzureChatCompletions(JsonObject raw)
    {
        Raw = raw;
    }

    /// <summary>The raw response object (<c>as_dict()</c>).</summary>
    public JsonObject Raw { get; }

    public string Id => Raw["id"]?.ToString() ?? "";

    public string Model => Raw["model"]?.ToString() ?? "";

    public IReadOnlyList<AzureChatChoice> Choices =>
        (Raw["choices"] as JsonArray)?.OfType<JsonObject>().Select(c => new AzureChatChoice(c)).ToList() ?? [];

    public AzureCompletionsUsage? Usage => Raw["usage"] is JsonObject usage ? new AzureCompletionsUsage(usage) : null;

    /// <summary>Parses a response body.</summary>
    public static AzureChatCompletions FromJson(BinaryData content) =>
        new(JsonNode.Parse(content.ToMemory().Span)?.AsObject() ?? throw new JsonException("Empty chat completions response."));

    /// <summary>Port of <c>as_dict()</c>: a copy of the raw payload.</summary>
    public JsonObject ToJson() => Raw.DeepClone().AsObject();
}

/// <summary>Dict-backed view of one <c>choices[]</c> entry (port of the mapping form of <c>ChatChoice</c>).</summary>
public sealed class AzureChatChoice(JsonObject raw)
{
    public JsonObject Raw { get; } = raw;

    public int Index => Raw["index"]?.GetValue<int>() ?? 0;

    /// <summary>Raw <c>finish_reason</c> string, or null.</summary>
    public string? FinishReason => Raw["finish_reason"]?.ToString();

    public AzureChatResponseMessage Message => new(Raw["message"] as JsonObject ?? new JsonObject());

    /// <summary>The undeclared Azure <c>content_filter_results</c> field, when present.</summary>
    public JsonObject? ContentFilterResults => Raw["content_filter_results"] as JsonObject;
}

/// <summary>Dict-backed view of a response <c>message</c> (port of <c>ChatResponseMessage</c>).</summary>
public sealed class AzureChatResponseMessage(JsonObject raw)
{
    public JsonObject Raw { get; } = raw;

    public string? Content => Raw["content"]?.ToString();

    /// <summary>Tool calls, or null when the field is absent (Python distinguishes None from []).</summary>
    public IReadOnlyList<AzureToolCall>? ToolCalls =>
        Raw["tool_calls"] is JsonArray calls ? calls.OfType<JsonObject>().Select(c => new AzureToolCall(c)).ToList() : null;
}

/// <summary>Dict-backed view of a response tool call (port of <c>ChatCompletionsToolCall</c>).</summary>
public sealed class AzureToolCall(JsonObject raw)
{
    public string Id => raw["id"]?.ToString() ?? "";

    public string Name => raw["function"]?["name"]?.ToString() ?? "";

    public string? Arguments => raw["function"]?["arguments"]?.ToString();
}

/// <summary>Dict-backed view of <c>usage</c> (port of <c>CompletionsUsage</c>).</summary>
public sealed class AzureCompletionsUsage(JsonObject raw)
{
    public int PromptTokens => raw["prompt_tokens"]?.GetValue<int>() ?? 0;

    public int CompletionTokens => raw["completion_tokens"]?.GetValue<int>() ?? 0;

    public int TotalTokens => raw["total_tokens"]?.GetValue<int>() ?? 0;
}

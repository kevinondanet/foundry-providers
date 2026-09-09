// ============================================================================
//  LAYER 6: PROVIDERS — a second real one, for Claude deployments on Foundry
//  Python: inspect_ai/model/_providers/anthropic.py  (anthropic/azure/<deployment>)
//
//  Claude deployments on Azure AI Foundry are not served on the chat-completions
//  route; they speak the Anthropic Messages API at /anthropic/v1/messages on
//  the same resource, with the same Entra ID token. So this is a different
//  wire format behind the same ModelAPI contract, and it proves the claim in
//  the layer 6 comments: a new provider is one new file plus one attribute.
//  Nothing in layers 1 to 5 or 7 changed to make `--model anthropic/<deployment>`
//  work.
//
//  The differences a provider has to absorb:
//    - the system prompt is a top-level field, not a message;
//    - content is a list of typed blocks (text, tool_use, tool_result);
//    - tool results travel inside a *user* message, and consecutive results
//      must share one message;
//    - tools declare `input_schema`, and stop reasons are named differently;
//    - 529 (overloaded) joins 429 and 5xx as retryable.
// ============================================================================
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using inspect_ai._util.display;
using inspect_ai.model;

namespace inspect_ai.model._providers;

internal static class AnthropicProviderRegistration
{
    /// <summary>Python: `@modelapi(name="anthropic")`.</summary>
    [ModelApi("anthropic")]
    public static ModelAPI Create(string modelName) => new AnthropicFoundryModelAPI(modelName);
}

internal sealed class AnthropicFoundryModelAPI : ModelAPI
{
    private const string Tag = "L6 _providers/anthropic";
    private const string AnthropicVersion = "2023-06-01";
    private const string BaseUrlVar = "AZUREAI_ANTHROPIC_BASE_URL";
    private const string InferenceUrlVar = "AZUREAI_BASE_URL";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(180) };
    private readonly Uri _endpoint;

    public AnthropicFoundryModelAPI(string modelName) : base(modelName)
    {
        // Explicit Anthropic URL, else derive it from the inference URL:
        // https://<resource>.services.ai.azure.com/models -> .../anthropic/v1/messages
        var url = Environment.GetEnvironmentVariable(BaseUrlVar);
        if (string.IsNullOrWhiteSpace(url))
        {
            var inference = Environment.GetEnvironmentVariable(InferenceUrlVar);
            if (string.IsNullOrWhiteSpace(inference))
                throw new InvalidOperationException(
                    $"The anthropic provider needs {BaseUrlVar} or {InferenceUrlVar} (the /models URL; /anthropic is derived from it) and an `az login` session for the token.");
            url = inference.TrimEnd('/');
            if (url.EndsWith("/models", StringComparison.OrdinalIgnoreCase)) url = url[..^"/models".Length];
            url += "/anthropic";
        }
        url = url.TrimEnd('/');
        _endpoint = new Uri(url.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase) ? url : url + "/v1/messages");
        Display.Step(Tag, $"endpoint {_endpoint.Host}{_endpoint.AbsolutePath}, deployment '{modelName}', same bearer token as azureai");
    }

    public override bool ShouldRetry(Exception ex) => ex switch
    {
        FoundryHttpException http => (int)http.Status is 429 or 500 or 502 or 503 or 504 or 529,   // not 408: our own timeout
        HttpRequestException => true,
        _ => false,
    };

    public override TimeSpan? RetryAfter(Exception ex) => (ex as FoundryHttpException)?.RetryAfter;

    public override async Task<ModelOutput> Generate(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, GenerateConfig config)
    {
        // 1. Inspect messages -> Messages API.
        var body = new JsonObject
        {
            ["model"] = ModelName,
            ["max_tokens"] = config.MaxTokens,
            ["temperature"] = config.Temperature,
            ["messages"] = MessagesToWire(input, out var system),
        };
        if (system is not null) body["system"] = system;
        if (tools.Count > 0) body["tools"] = new JsonArray(tools.Select(ToolToWire).ToArray());
        var request = body.ToJsonString();
        Display.Step(Tag, $"POST {_endpoint.AbsolutePath} ({request.Length} bytes)");

        // 2. Send.
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(request, Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await EntraToken.Get());
        message.Headers.Add("anthropic-version", AnthropicVersion);

        using var http = await AzureAIModelAPI.Post(Http, message, Tag);
        var response = await http.Content.ReadAsStringAsync();
        if (!http.IsSuccessStatusCode)
        {
            Display.Step(Tag, $"HTTP {(int)http.StatusCode} {http.ReasonPhrase}");
            throw new FoundryHttpException(http.StatusCode, $"{(int)http.StatusCode} {http.ReasonPhrase}: {Truncate(response)}", FoundryHttpException.RetryAfterOf(http));
        }

        // 3. Messages API -> Inspect's ModelOutput.
        var root = JsonNode.Parse(response)!;
        var text = new StringBuilder();
        var toolCalls = new List<ToolCall>();
        foreach (var block in root["content"]!.AsArray())
        {
            switch (block!["type"]?.GetValue<string>())
            {
                case "text": text.Append(block["text"]?.GetValue<string>()); break;
                case "tool_use": toolCalls.Add(new ToolCall(block["id"]!.GetValue<string>(), block["name"]!.GetValue<string>(), InputToArguments(block["input"]))); break;
                // "thinking" and other block types are not part of the answer
            }
        }
        var vendorStop = root["stop_reason"]?.GetValue<string>() ?? "end_turn";
        var stopReason = vendorStop switch { "tool_use" => "tool_calls", "max_tokens" => "max_tokens", _ => "stop" };
        var usage = root["usage"];
        var modelUsage = new ModelUsage(usage?["input_tokens"]?.GetValue<int>() ?? 0, usage?["output_tokens"]?.GetValue<int>() ?? 0);

        Display.Step(Tag, $"HTTP 200, stop_reason={vendorStop} -> {stopReason}, tokens in={modelUsage.InputTokens} out={modelUsage.OutputTokens}"
            + (toolCalls.Count > 0 ? $", tool_use={string.Join(",", toolCalls.Select(t => t.Function))}" : ""));

        var assistant = new ChatMessage("assistant", text.ToString(), toolCalls.Count > 0 ? toolCalls : null);
        return new ModelOutput(assistant, stopReason, modelUsage, new ModelCall(request, response));
    }

    // ---- neutral -> wire ---------------------------------------------------

    /// <summary>System prompt out to a top-level field; content as typed blocks;
    /// tool results inside a user message, consecutive ones sharing it.</summary>
    private static JsonArray MessagesToWire(IReadOnlyList<ChatMessage> input, out string? system)
    {
        var systems = new List<string>();
        var messages = new JsonArray();
        foreach (var m in input)
        {
            switch (m.Role)
            {
                case "system":
                    systems.Add(m.Content);
                    break;

                case "user":
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(TextBlock(m.Content)) });
                    break;

                case "assistant":
                    var blocks = new JsonArray();
                    if (m.Content.Trim().Length > 0) blocks.Add(TextBlock(m.Content));
                    foreach (var c in m.ToolCalls ?? Array.Empty<ToolCall>())
                        blocks.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = c.Id,
                            ["name"] = c.Function,
                            ["input"] = new JsonObject(c.Arguments.Select(a => KeyValuePair.Create<string, JsonNode?>(a.Key, a.Value))),
                        });
                    if (blocks.Count == 0) blocks.Add(TextBlock("(no content)"));   // the API rejects an empty assistant turn
                    messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = blocks });
                    break;

                case "tool":
                    var result = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = m.ToolCallId, ["content"] = m.Content };
                    if (messages.Count > 0 && messages[^1] is JsonObject last
                        && last["role"]?.GetValue<string>() == "user"
                        && last["content"] is JsonArray content && content.Count > 0
                        && content[0]?["type"]?.GetValue<string>() == "tool_result")
                        content.Add(result);   // several tool calls in one turn -> several results in one message
                    else
                        messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(result) });
                    break;
            }
        }
        system = systems.Count == 0 ? null : string.Join("\n\n", systems);
        return messages;
    }

    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

    private static JsonNode ToolToWire(ToolInfo t) => new JsonObject
    {
        ["name"] = t.Name,
        ["description"] = t.Description,
        ["input_schema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(t.Parameters.Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key, new JsonObject { ["type"] = p.Value }))),
            ["required"] = new JsonArray(t.Parameters.Keys.Select(k => (JsonNode?)k).ToArray()),
        },
    };

    // ---- wire -> neutral ---------------------------------------------------

    private static Dictionary<string, string> InputToArguments(JsonNode? input)
    {
        var arguments = new Dictionary<string, string>();
        if (input is JsonObject o)
            foreach (var (key, value) in o)
                arguments[key] = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value?.ToJsonString() ?? "";
        return arguments;
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..197] + "...";
}

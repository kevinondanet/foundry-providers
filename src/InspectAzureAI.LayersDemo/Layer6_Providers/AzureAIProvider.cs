// ============================================================================
//  LAYER 6: PROVIDERS — a real one
//  Python: inspect_ai/model/_providers/azureai.py
//
//  Same contract as the mock next door, but the bytes go over the network:
//  POST {AZUREAI_BASE_URL}/chat/completions on an Azure AI Foundry
//  model-inference endpoint, authenticated with an Entra ID bearer token
//  from DefaultAzureCredential, so `az login` is the only setup. Nothing
//  above layer 6 changes: the model layer still calls Generate() and asks
//  ShouldRetry(); the engine, solvers and scorers never learn that this
//  provider exists. `--model azureai/<deployment>` is the whole switch.
//
//  What a provider has to know that nobody else does:
//    - the wire format: OpenAI-style chat completions, plus Foundry's
//      api-version query and `extra-parameters: pass-through` header;
//    - vendor quirks: gpt-5 and o-series deployments take
//      max_completion_tokens and reject temperature;
//    - which failures are transient (429, 5xx, timeouts) and so retryable.
//
//  It is also the only file in the app with an external dependency
//  (Azure.Identity); no other layer needs to know how tokens are minted.
// ============================================================================
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using inspect_ai._util.display;
using inspect_ai.model;

namespace inspect_ai.model._providers;

/// <summary>A non-2xx reply. Layer 5 never sees the type; it only asks ShouldRetry.</summary>
internal sealed class AzureAIHttpException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

internal static class AzureAIProviderRegistration
{
    /// <summary>Python: `@modelapi(name="azureai")`.</summary>
    [ModelApi("azureai")]
    public static ModelAPI Create(string modelName) => new AzureAIModelAPI(modelName);
}

internal sealed class AzureAIModelAPI : ModelAPI
{
    private const string Tag = "L6 _providers/azureai";
    private const string ApiVersion = "2024-05-01-preview";
    private const string BaseUrlVar = "AZUREAI_BASE_URL";
    private const string AudienceVar = "AZUREAI_AUDIENCE";
    private const string DefaultAudience = "https://cognitiveservices.azure.com/.default";

    // Module-level state with no locks: one HttpClient (one connection pool)
    // and one token request per process, touched only from the event-loop
    // thread. Caching the *task* rather than the token means concurrent samples
    // that all need a token at start-up share a single request.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private static readonly TokenCredential Credential = new DefaultAzureCredential();
    private static Task<AccessToken>? _tokenTask;

    private readonly Uri _endpoint;
    private readonly bool _reasoningFamily;

    public AzureAIModelAPI(string modelName) : base(modelName)
    {
        var baseUrl = Environment.GetEnvironmentVariable(BaseUrlVar);
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException(
                $"The azureai provider needs {BaseUrlVar} (e.g. https://<resource>.services.ai.azure.com/models) and an `az login` session for the token.");

        _endpoint = new Uri($"{baseUrl.TrimEnd('/')}/chat/completions?api-version={ApiVersion}");
        _reasoningFamily = IsReasoningFamily(modelName);
        Display.Step(Tag, $"endpoint {_endpoint.Host}{_endpoint.AbsolutePath}, deployment '{modelName}', bearer token from DefaultAzureCredential");
    }

    /// <summary>gpt-5.* and o1/o3/o4 deployments: max_completion_tokens instead of max_tokens, and no temperature.</summary>
    private static bool IsReasoningFamily(string name)
        => name.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
        || (name.Length > 1 && char.ToLowerInvariant(name[0]) == 'o' && char.IsDigit(name[1]));

    public override bool ShouldRetry(Exception ex) => ex switch
    {
        AzureAIHttpException http => http.Status is HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout,
        HttpRequestException or TaskCanceledException => true,   // connection reset, DNS blip, client timeout
        _ => false,
    };

    public override async Task<ModelOutput> Generate(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, GenerateConfig config)
    {
        // 1. Inspect messages -> the vendor wire format.
        var body = new JsonObject
        {
            ["model"] = ModelName,
            ["messages"] = new JsonArray(input.Select(MessageToWire).ToArray()),
        };
        if (tools.Count > 0) body["tools"] = new JsonArray(tools.Select(ToolToWire).ToArray());
        if (_reasoningFamily)
        {
            body["max_completion_tokens"] = config.MaxTokens;
        }
        else
        {
            body["max_tokens"] = config.MaxTokens;
            body["temperature"] = config.Temperature;
        }
        var request = body.ToJsonString();
        Display.Step(Tag, $"POST {_endpoint.AbsolutePath} ({request.Length} bytes)");

        // 2. Send it with a bearer token. Awaiting real I/O here is what lets
        //    the single event loop run other samples in the meantime.
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(request, Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token());
        message.Headers.Add("extra-parameters", "pass-through");   // Foundry: forward vendor-specific fields untouched

        using var http = await Http.SendAsync(message);
        var response = await http.Content.ReadAsStringAsync();
        if (!http.IsSuccessStatusCode)
        {
            Display.Step(Tag, $"HTTP {(int)http.StatusCode} {http.ReasonPhrase}");
            throw new AzureAIHttpException(http.StatusCode, $"{(int)http.StatusCode} {http.ReasonPhrase}: {Truncate(response)}");
        }

        // 3. Vendor wire format -> Inspect's ModelOutput.
        var root = JsonNode.Parse(response)!;
        var choice = root["choices"]![0]!;
        var reply = choice["message"]!;
        var finishReason = choice["finish_reason"]?.GetValue<string>() ?? "stop";
        var content = TextOf(reply["content"]);
        var toolCalls = (reply["tool_calls"] as JsonArray)?.Select(ToolCallFromWire).ToList();
        var usage = root["usage"];
        var modelUsage = new ModelUsage(
            usage?["prompt_tokens"]?.GetValue<int>() ?? 0,
            usage?["completion_tokens"]?.GetValue<int>() ?? 0);

        Display.Step(Tag, $"HTTP 200, finish_reason={finishReason}, tokens in={modelUsage.InputTokens} out={modelUsage.OutputTokens}"
            + (toolCalls is { Count: > 0 } ? $", tool_call={string.Join(",", toolCalls.Select(t => t.Function))}" : ""));

        var assistant = new ChatMessage("assistant", content, toolCalls is { Count: > 0 } ? toolCalls : null);
        return new ModelOutput(assistant, finishReason, modelUsage, new ModelCall(request, response));
    }

    /// <summary>One token per process, refreshed when it is within five minutes of expiry.</summary>
    private static async Task<string> Token()
    {
        var fresh = _tokenTask is { IsCompletedSuccessfully: true } done
                    && done.Result.ExpiresOn - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(5);
        if (!fresh && (_tokenTask is null || _tokenTask.IsCompleted))
            _tokenTask = RequestToken();   // none yet, expiring, or failed: start one request that every caller awaits
        return (await _tokenTask!).Token;
    }

    private static async Task<AccessToken> RequestToken()
    {
        var scope = Environment.GetEnvironmentVariable(AudienceVar) is { Length: > 0 } audience ? audience : DefaultAudience;
        Display.Step(Tag, $"requesting an Entra ID token for {scope} (env -> managed identity -> Visual Studio -> az login -> ...)");
        var token = await Credential.GetTokenAsync(new TokenRequestContext(new[] { scope }), CancellationToken.None);
        Display.Step(Tag, $"token acquired, expires {token.ExpiresOn:HH:mm:ss}Z");
        return token;
    }

    // ---- neutral -> wire ---------------------------------------------------

    private static JsonNode MessageToWire(ChatMessage m)
    {
        var node = new JsonObject { ["role"] = m.Role };
        switch (m.Role)
        {
            case "assistant":
                node["content"] = m.Content.Length == 0 ? null : m.Content;
                if (m.ToolCalls is { Count: > 0 })
                    node["tool_calls"] = new JsonArray(m.ToolCalls.Select(c => (JsonNode)new JsonObject
                    {
                        ["id"] = c.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = c.Function, ["arguments"] = JsonSerializer.Serialize(c.Arguments) },
                    }).ToArray());
                break;
            case "tool":
                node["tool_call_id"] = m.ToolCallId;
                node["content"] = m.Content;
                break;
            default:
                node["content"] = m.Content;
                break;
        }
        return node;
    }

    /// <summary>ToolInfo carries "name -> type"; the wire wants a JSON schema.</summary>
    private static JsonNode ToolToWire(ToolInfo t) => new JsonObject
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(t.Parameters.Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key, new JsonObject { ["type"] = p.Value }))),
                ["required"] = new JsonArray(t.Parameters.Keys.Select(k => (JsonNode?)k).ToArray()),
            },
        },
    };

    // ---- wire -> neutral ---------------------------------------------------

    private static ToolCall ToolCallFromWire(JsonNode? call)
    {
        var function = call!["function"]!;
        var arguments = new Dictionary<string, string>();
        var raw = function["arguments"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                foreach (var (key, value) in JsonNode.Parse(raw)!.AsObject())
                    arguments[key] = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value?.ToJsonString() ?? "";
            }
            catch (JsonException)
            {
                arguments["_raw"] = raw;   // let the tool report the malformed call back to the model
            }
        }
        return new ToolCall(call["id"]!.GetValue<string>(), function["name"]!.GetValue<string>(), arguments);
    }

    /// <summary>Content is usually a string; some deployments return a list of text parts.</summary>
    private static string TextOf(JsonNode? content) => content switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray parts => string.Concat(parts.Select(p => p?["text"]?.GetValue<string>() ?? "")),
        _ => content.ToJsonString(),
    };

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..197] + "...";
}

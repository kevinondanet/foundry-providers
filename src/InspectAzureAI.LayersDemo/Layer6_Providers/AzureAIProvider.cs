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
//      max_completion_tokens and reject temperature, and Foundry hosts many
//      other vendors' models behind the same route with their own quirks, so
//      the provider also learns a deployment's dialect from its first 400
//      and resends once;
//    - which failures are transient (429, 5xx, timeouts) and so retryable.
//
//  Token minting is shared with the Anthropic provider (EntraToken.cs).
// ============================================================================
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using inspect_ai._util.display;
using inspect_ai.model;

namespace inspect_ai.model._providers;

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

    // One HttpClient (one connection pool) per process; module-level state with no lock.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(180) };

    private readonly Uri _endpoint;

    // The deployment's dialect. Seeded from the name, corrected by the first
    // 400 that names the offending parameter (per instance, no lock).
    private bool _maxCompletionTokens;
    private bool _noTemperature;

    public AzureAIModelAPI(string modelName) : base(modelName)
    {
        var baseUrl = Environment.GetEnvironmentVariable(BaseUrlVar);
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException(
                $"The azureai provider needs {BaseUrlVar} (e.g. https://<resource>.services.ai.azure.com/models) and an `az login` session for the token.");

        _endpoint = new Uri($"{baseUrl.TrimEnd('/')}/chat/completions?api-version={ApiVersion}");
        _maxCompletionTokens = _noTemperature = IsReasoningFamily(modelName);
        Display.Step(Tag, $"endpoint {_endpoint.Host}{_endpoint.AbsolutePath}, deployment '{modelName}', bearer token from DefaultAzureCredential");
    }

    /// <summary>gpt-5.* and o1/o3/o4 deployments: max_completion_tokens instead of max_tokens, and no temperature.</summary>
    private static bool IsReasoningFamily(string name)
        => name.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
        || (name.Length > 1 && char.ToLowerInvariant(name[0]) == 'o' && char.IsDigit(name[1]));

    public override bool ShouldRetry(Exception ex) => ex switch
    {
        FoundryHttpException http => http.Status is HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout,   // not 408: our own timeout, fail fast
        HttpRequestException => true,   // connection reset, DNS blip
        _ => false,
    };

    public override TimeSpan? RetryAfter(Exception ex) => (ex as FoundryHttpException)?.RetryAfter;

    public override async Task<ModelOutput> Generate(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, GenerateConfig config)
    {
        // A 400 that names a parameter this deployment does not take is not an
        // error to report; it is the deployment telling us its dialect. Adjust
        // and resend, at most once per parameter. Concurrent samples all get the
        // same 400: one learns, the others see the dialect changed under them.
        for (var attempt = 1; ; attempt++)
        {
            var dialect = (_maxCompletionTokens, _noTemperature);
            try
            {
                return await Send(input, tools, config);
            }
            catch (FoundryHttpException ex) when (ex.Status == HttpStatusCode.BadRequest && attempt <= 3
                                                  && (LearnDialect(ex.Message) || dialect != (_maxCompletionTokens, _noTemperature)))
            {
                Display.Step(Tag, "400 named a parameter this deployment rejects; resending in its dialect");
            }
        }
    }

    private bool LearnDialect(string error)
    {
        if (!_maxCompletionTokens && error.Contains("max_completion_tokens"))
            return _maxCompletionTokens = true;
        if (!_noTemperature && error.Contains("temperature"))
            return _noTemperature = true;
        return false;
    }

    private async Task<ModelOutput> Send(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, GenerateConfig config)
    {
        // 1. Inspect messages -> the vendor wire format.
        var body = new JsonObject
        {
            ["model"] = ModelName,
            ["messages"] = new JsonArray(input.Select(MessageToWire).ToArray()),
        };
        if (tools.Count > 0) body["tools"] = new JsonArray(tools.Select(ToolToWire).ToArray());
        body[_maxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = config.MaxTokens;
        if (!_noTemperature) body["temperature"] = config.Temperature;
        var request = body.ToJsonString();
        Display.Step(Tag, $"POST {_endpoint.AbsolutePath} ({request.Length} bytes)");

        // 2. Send it with a bearer token. Awaiting real I/O here is what lets
        //    the single event loop run other samples in the meantime.
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(request, Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await EntraToken.Get());
        message.Headers.Add("extra-parameters", "pass-through");   // Foundry: forward vendor-specific fields untouched

        using var http = await Post(Http, message, Tag);
        var response = await http.Content.ReadAsStringAsync();
        if (!http.IsSuccessStatusCode)
        {
            Display.Step(Tag, $"HTTP {(int)http.StatusCode} {http.ReasonPhrase}");
            throw new FoundryHttpException(http.StatusCode, $"{(int)http.StatusCode} {http.ReasonPhrase}: {Truncate(response)}", FoundryHttpException.RetryAfterOf(http));
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

    /// <summary>Send, turning the client's own timeout into a 408 so it reads as "the deployment
    /// is unhealthy" rather than as a cancellation (which is what the control plane's token means).</summary>
    internal static async Task<HttpResponseMessage> Post(HttpClient http, HttpRequestMessage message, string tag)
    {
        try
        {
            return await http.SendAsync(message);
        }
        catch (TaskCanceledException)
        {
            Display.Step(tag, $"no reply within {http.Timeout.TotalSeconds:0}s");
            throw new FoundryHttpException(HttpStatusCode.RequestTimeout, $"408 no reply within {http.Timeout.TotalSeconds:0}s; the deployment looks unhealthy");
        }
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..197] + "...";
}

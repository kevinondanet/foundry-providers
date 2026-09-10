using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Provider.OpenAI;

/// <summary>Direct OpenAI generation. Credentials and endpoints never use Azure fallback.</summary>
public sealed class OpenAIModelApi : DirectModelApi
{
    public OpenAIModelApi(string modelName, string? baseUrl = null, string? apiKey = null,
        GenerateConfig? config = null, object? streaming = null,
        IReadOnlyDictionary<string, object?>? modelArgs = null, DirectClientSettings? settings = null)
        : base(modelName, config, new DirectProviderOptions("openai", baseUrl, apiKey, streaming, modelArgs, settings,
            "responses_api", "responses_store", "responses_phase", "background", "service_tier", "organization", "project",
            "safety_identifier", "prompt_cache_key", "prompt_cache_retention"))
    {
        _ = Options.Boolean("responses_api");
        _ = Options.Boolean("responses_store");
        _ = Options.Boolean("responses_phase");
        _ = Options.Boolean("background");
    }
    private string Family => ModelName.ToLowerInvariant();
    private Match Version => Regex.Match(Family, @"^gpt-(\d+)(?:\.(\d+))?");
    private bool Latest => !new[] { "embedding", "whisper", "dall-e", "tts", "moderation", "image-1", "sora", "gpt", "codex", "deep-research" }.Any(Family.Contains) && !OSeries;
    internal bool OSeries => Regex.IsMatch(Family, @"^o\d+") || (!Family.Contains("gpt") && Regex.IsMatch(Family, @"o\d+"));
    internal bool Gpt5Plus => Version.Success && int.Parse(Version.Groups[1].Value) >= 5 || Latest;
    internal bool ReasoningOptions => OSeries || (Gpt5Plus && !Family.Contains("-chat")) || Family.Contains("codex");
    internal bool ReasoningEnabled(GenerateConfig config) => OSeries ||
        (Gpt5Plus && Version.Success && Version.Groups[1].Value == "5" && !Version.Groups[2].Success) ||
        (Gpt5Plus && (config.ReasoningEffort is not (null or "none") || config.ReasoningMode == "pro"));
    internal string? Effort(GenerateConfig config) => config.ReasoningEffort == "max" && !(Latest || Version.Success &&
        (int.Parse(Version.Groups[1].Value) > 5 || Version.Groups[1].Value == "5" && int.TryParse(Version.Groups[2].Value, out var minor) && minor >= 6)) ? "xhigh" : config.ReasoningEffort;
    public bool Background(GenerateConfig config) => Options.Boolean("background") ??
        (Family.Contains("deep-research") || Gpt5Plus && Family.Contains("-pro") || config.ReasoningMode == "pro");
    public bool UsesResponses(GenerateConfig config) => Background(config) || (Options.Boolean("responses_api") ??
        ((Gpt5Plus || OSeries || Family.Contains("codex")) && config.NumChoices is null));
    public override bool ApplyRedactedReasoningTokensToInput() => true;
    protected override string RequestPath(GenerateConfig config) => UsesResponses(config) ? "/responses" : "/chat/completions";
    protected override bool AutoStream(GenerateConfig config) => !Background(config) && base.AutoStream(config);
    protected override void AddHeaders(HttpRequestMessage request, GenerateConfig config)
    {
        Set("OpenAI-Organization", Options.String("organization") ?? Environment.GetEnvironmentVariable("OPENAI_ORG_ID"));
        Set("OpenAI-Project", Options.String("project") ?? Environment.GetEnvironmentVariable("OPENAI_PROJECT_ID"));
        void Set(string name, string? value) { if (value is not null) { request.Headers.Remove(name); request.Headers.TryAddWithoutValidation(name, value); } }
    }
    internal void CommonFields(JsonObject request)
    {
        foreach (var key in new[] { "service_tier", "prompt_cache_key", "prompt_cache_retention", "safety_identifier" })
            if (Options.Values[key] is { } value) request[key] = value.DeepClone();
        if (!request.ContainsKey("safety_identifier") && Environment.GetEnvironmentVariable("OPENAI_SAFETY_IDENTIFIER") is { Length: > 0 } safety)
            request["safety_identifier"] = safety;
    }
    public override JsonObject BuildRequest(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, ToolChoice toolChoice, GenerateConfig config, bool streaming)
    {
        if (!UsesResponses(config))
        {
            var chat = ChatCompletionsProtocol.Build(this, input, tools, toolChoice, config, streaming);
            CommonFields(chat);
            foreach (var pair in Options.BodyExtras(config)) if (pair.Key != "metadata") chat[pair.Key] = pair.Value?.DeepClone();
            if (config.PromptLogprobs is { } prompt) chat["prompt_logprobs"] = prompt;
            return chat;
        }
        var extras = Options.BodyExtras(config);
        var request = new ResponsesProtocol(ModelName, new Dictionary<string, object?>()).BuildRequest(input, tools, toolChoice,
            config with { ExtraBody = null, Logprobs = null, TopLogprobs = null, Temperature = null, TopP = null }, streaming && !Background(config));
        var store = Options.Boolean("responses_store") ?? (extras["store"]?.GetValue<bool>() == true);
        request["store"] = store;
        request["include"] = !store && ReasoningOptions ? new JsonArray("reasoning.encrypted_content") : new JsonArray();
        if (!ReasoningOptions) request.Remove("reasoning");
        else if (request["reasoning"] is JsonObject reasoning && Effort(config) is { } effort) reasoning["effort"] = effort;
        if (!ReasoningEnabled(config))
        {
            if (config.Temperature is { } temp) request["temperature"] = temp;
            if (config.TopP is { } topP) request["top_p"] = topP;
            if (config.Logprobs is not null) request["include"]!.AsArray().Add("message.output_text.logprobs");
            if (config.TopLogprobs is { } topLogs) request["top_logprobs"] = topLogs;
        }
        if (config.ResponseSchema is { } schema)
        {
            request["text"]!["format"]!["description"] = schema.Description ?? schema.Name;
            request["text"]!["format"]!["strict"] = schema.Strict is { } strict ? JsonValue.Create(strict) : null;
        }
        if (Background(config) || Options.Boolean("background") is not null) request["background"] = Background(config);
        // Phase is attached to each text run, so separate messages retain different API-returned phases.
        request["input"] = DirectInput(input, Options.Boolean("responses_phase") == true);
        CommonFields(request);
        foreach (var pair in extras) request[pair.Key] = pair.Value?.DeepClone();
        if (Options.Boolean("responses_store") is { } explicitStore) request["store"] = explicitStore;
        return request;
    }
    internal static JsonArray DirectInput(IReadOnlyList<ChatMessage> input, bool synthesizePhase)
    {
        var items = new JsonArray();
        foreach (var message in input)
        {
            IReadOnlyList<string?>? phases = null;
            if (message.Metadata?.TryGetValue("openai_text_phases", out var saved) == true)
                phases = JsonSerializer.SerializeToNode(saved)?.Deserialize<List<string?>>();
            if (synthesizePhase && message is ChatMessageAssistant original)
                phases = original.ContentList.OfType<ContentText>().Select((_, i) => phases is not null && i < phases.Count && phases[i] is not null
                    ? phases[i] : original.ToolCalls is { Count: > 0 } ? "commentary" : "final_answer").ToList();
            IEnumerable<JsonNode?> converted = message is ChatMessageAssistant assistant
                ? ResponsesInput.AssistantItems(assistant, phases) : ResponsesInput.InputItems([message]);
            foreach (var item in converted)
            {
                var clone = item!.DeepClone();
                if (clone["role"]?.ToString() == "assistant" && clone["content"] is JsonArray content)
                    foreach (var part in content) if (part?["type"]?.ToString() == "output_text") part["logprobs"] = new JsonArray();
                items.Add(clone);
            }
        }
        return items;
    }
    protected override ModelOutput ParseOutput(JsonObject response, GenerateConfig config)
    {
        if (!UsesResponses(config)) return ChatCompletionsProtocol.Parse(response, ModelName);
        var output = ResponsesOutput.Parse(response, ModelName);
        var phases = (response["output"] as JsonArray ?? []).Where(p => p?["type"]?.ToString() == "message")
            .SelectMany(p => (p!["content"] as JsonArray ?? []).Where(c => c?["type"]?.ToString() is "output_text" or "refusal").Select(_ => p["phase"]?.ToString())).ToList();
        if (phases.Any(p => p is not null)) output = output with { Choices = output.Choices.Select(c => c with { Message = c.Message with { Metadata = new Dictionary<string, object?> { ["openai_text_phases"] = phases } } }).ToList() };
        return output with { Metadata = response["metadata"]?.Deserialize<Dictionary<string, object?>>() };
    }
    protected override Task<JsonObject> AccumulateAsync(IAsyncEnumerable<JsonObject> events, GenerateConfig config, CancellationToken cancellationToken) => UsesResponses(config) ? ResponsesStreamAccumulator.AccumulateAsync(events, cancellationToken) : ChatCompletionsProtocol.AccumulateAsync(events, cancellationToken);
    protected override async Task<JsonObject> CompleteResponseAsync(JsonObject response, GenerateConfig config, CancellationToken cancellationToken)
    {
        var id = response["id"]?.ToString();
        try
        {
            while (response["status"]?.ToString() is "queued" or "in_progress")
            {
                if (string.IsNullOrEmpty(id)) throw new ServiceResponseException("Background response omitted its id.");
                await DelayAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                var watch = Stopwatch.StartNew();
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        using var polled = await SendAsync(HttpMethod.Get, "/responses/" + Uri.EscapeDataString(id), null, config, cancellationToken).ConfigureAwait(false);
                        response = JsonNode.Parse(await polled.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!.AsObject();
                        break;
                    }
                    catch (Exception ex) when (attempt < 4 && watch.Elapsed.TotalSeconds < 60 && ShouldRetry(ex).Retry)
                    {
                        var seconds = Math.Min(ShouldRetry(ex).RetryAfter ?? Math.Pow(2, attempt), Math.Max(0, 60 - watch.Elapsed.TotalSeconds));
                        await DelayAsync(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (id is not null)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { using var cancelled = await SendAsync(HttpMethod.Post, "/responses/" + Uri.EscapeDataString(id) + "/cancel", null, config, cleanup.Token).ConfigureAwait(false); }
            catch (Exception) { ProviderLogger.WarnOnce("OpenAI background cancellation could not be confirmed."); }
            throw;
        }
        if (response["error"] is { } error)
        {
            var code = error["code"]?.ToString();
            throw new ProviderHttpException(code is "server_error" or "internal_error" ? 500 : code == "rate_limit_exceeded" ? 429 : 400,
                new Dictionary<string, string>(), new JsonObject { ["error"] = error.DeepClone() }.ToJsonString());
        }
        if (response["status"]?.ToString() is "cancelled" or "failed") throw new ServiceResponseException($"OpenAI response ended with status {response["status"]}.");
        return response;
    }
}

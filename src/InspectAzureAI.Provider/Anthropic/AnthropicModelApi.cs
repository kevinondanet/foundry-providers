using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Provider.Anthropic;

/// <summary>Direct Claude Messages API with per-generation model-family reasoning rules.</summary>
public sealed class AnthropicModelApi : DirectModelApi
{
    public AnthropicModelApi(string modelName, string? baseUrl = null, string? apiKey = null,
        GenerateConfig? config = null, object? streaming = null, IReadOnlyDictionary<string, object?>? modelArgs = null,
        DirectClientSettings? settings = null)
        : base(modelName, config, new DirectProviderOptions("anthropic", baseUrl, apiKey, streaming, modelArgs, settings, "betas", "anthropic_beta")) { }

    private string Family => ModelName.ToLowerInvariant().Replace('.', '-');
    private bool Claude3 => Regex.IsMatch(Family, @"claude-3-[a-z]") || Family.Contains("claude-3-5");
    private bool Claude4 => Regex.IsMatch(Family, @"claude-[a-z]+-4(?:-|$)") || Family.Contains("claude-4");
    private bool Claude5 => Regex.IsMatch(Family, @"claude-[a-z]+-5(?:-|$)") || Family.Contains("claude-5");
    private bool Minor(int minor) => Family.Contains($"claude-4-{minor}") || Regex.IsMatch(Family, @"claude-[a-z]+-4-" + minor + @"(?:-|$)");
    private bool Latest => !Claude3 && !Family.Contains("claude-3-7") && !Claude5 && (!Claude4 || !(Minor(0) || Minor(1) || Minor(5) || Minor(6) || Minor(7) || Minor(8)));
    private bool Frontier => Minor(6) || Minor(7) || Minor(8) || Claude5 || Latest;
    private bool AdaptiveOnly => Minor(7) || Minor(8) || Claude5 || Latest;
    private bool CanDisable => AdaptiveOnly && (!Claude5 || Family.Contains("claude-opus-5") || Family.Contains("claude-sonnet-5"));
    private static int? EffortTokens(string? effort) => effort switch { "minimal" => 2048, "low" => 4096, "medium" => 10000, "high" => 16000, "xhigh" or "max" => 32000, _ => null };
    private string? ReasoningEffort(GenerateConfig config) => Frontier ? config.ReasoningEffort switch
    {
        "minimal" or "low" => "low", "medium" => "medium", "high" => "high", "xhigh" => AdaptiveOnly ? "xhigh" : "high", "max" => "max", _ => null,
    } : null;
    private int? Budget(GenerateConfig config) => config.ReasoningTokens ?? (!Frontier ? EffortTokens(config.ReasoningEffort) : null);
    private bool Thinking(GenerateConfig config) => !Claude3 && (Budget(config) is not null || ReasoningEffort(config) is not null);
    public override int? MaxTokens() => Claude3 ? 4096 : 32000;
    public override int? MaxTokensForConfig(GenerateConfig config)
    {
        ValidateThinking(config);
        var tokens = MaxTokens()!.Value;
        if (!Claude3) tokens += ReasoningEffort(config) is { } effort ? EffortTokens(effort) ?? 16000 : Budget(config) ?? 0;
        if (config.Effort is "xhigh" or "max") tokens = Math.Max(tokens, 64000);
        var cap = Frontier && Family.Contains("opus") && Claude4 || Claude5 || Latest && !Claude4 ? 128000
            : Minor(5) || Frontier ? 64000 : Claude4 && Family.Contains("opus") ? 32000 : Family.Contains("claude-3-7") ? 128000 : 64000;
        return Math.Min(tokens, cap);
    }
    private void ValidateThinking(GenerateConfig config)
    {
        if (AdaptiveOnly && config.ReasoningTokens is not null)
            throw new PrerequisiteError($"{ModelName} does not support reasoning_tokens (budget_tokens). Use reasoning_effort instead.");
        if (config.ReasoningEffort == "none" && AdaptiveOnly && !CanDisable)
            ProviderLogger.WarnOnce($"{ModelName} does not support disabling thinking; reasoning_effort=none is ignored.");
    }
    public override bool CollapseUserMessages() => true;
    protected override string RequestPath(GenerateConfig config) => BaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? "/messages" : "/v1/messages";
    protected override ModelOutput ParseOutput(JsonObject response, GenerateConfig config)
    {
        var output = new AnthropicProtocol(ModelName, new Dictionary<string, object?>()).ParseMessage(response);
        var order = response["content"]!.AsArray().Select(AnthropicProtocol.BlockOrderKey).ToArray();
        return output with { Choices = output.Choices.Select(choice => choice with { Message = choice.Message with { Metadata = new Dictionary<string, object?> { ["anthropic_block_order"] = order } } }).ToArray() };
    }
    protected override Task<JsonObject> AccumulateAsync(IAsyncEnumerable<JsonObject> events, GenerateConfig config, CancellationToken cancellationToken) => AnthropicProtocol.AccumulateAsync(ValidateStream(events, cancellationToken), cancellationToken);
    private static async IAsyncEnumerable<JsonObject> ValidateStream(IAsyncEnumerable<JsonObject> events, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var completed = false;
        await foreach (var evt in events.WithCancellation(token))
        {
            if (evt["type"]?.ToString() == "message_stop") completed = true;
            if (evt["type"]?.ToString() == "error")
            {
                var status = evt["error"]?["type"]?.ToString() switch { "overloaded_error" => 529, "rate_limit_error" => 429, "authentication_error" => 401, _ => 400 };
                throw new ProviderHttpException(status, new Dictionary<string,string>(), evt.ToJsonString());
            }
            yield return evt;
        }
        if (!completed) throw new ServiceResponseException("Claude stream ended without message_stop.");
    }
    protected override bool AutoStream(GenerateConfig config) => base.AutoStream(config) || Thinking(config) || config.MaxTokens >= 8192;
    public override JsonObject BuildRequest(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, ToolChoice toolChoice, GenerateConfig config, bool streaming)
    {
        ValidateThinking(config);
        var maxTokens = config.MaxTokens ?? MaxTokensForConfig(config)!.Value;
        var forbidSampling = AdaptiveOnly || Thinking(config);
        foreach (var parameter in new[] { ("temperature", config.Temperature), ("top_p", config.TopP), ("top_k", (double?)config.TopK) })
            if (forbidSampling && parameter.Item2 is not null) ProviderLogger.WarnOnce($"{ModelName}: {parameter.Item1} ignored while thinking is enabled.");
        var normalized = config with { MaxTokens = maxTokens, ReasoningEffort = null, ReasoningTokens = null,
            Temperature = forbidSampling ? null : config.Temperature, TopP = forbidSampling ? null : config.TopP };
        var body = new AnthropicProtocol(ModelName, new Dictionary<string, object?>()).BuildRequest(input, tools, toolChoice, normalized, streaming);
        if (body["system"] is JsonValue system) body["system"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = system.ToString() });
        body["tools"] ??= new JsonArray();
        if (Thinking(config)) body.Remove("tool_choice");
        foreach (var message in body["messages"]!.AsArray())
            if (message?["content"] is JsonArray { Count: 1 } blocks && blocks[0]?["type"]?.ToString() == "text")
                message["content"] = blocks[0]!["text"]!.ToString();
        if (!forbidSampling && config.TopK is { } topK) body["top_k"] = topK;
        if (config.Effort is { } effort)
            body["output_config"] = new JsonObject { ["effort"] = effort == "max" && !Frontier || effort == "xhigh" && !AdaptiveOnly ? "high" : effort };
        if (Thinking(config))
        {
            JsonObject thinking;
            if (ReasoningEffort(config) is { } reasoningEffort)
            {
                thinking = new() { ["type"] = "adaptive", ["display"] = "summarized" };
                body["output_config"] = new JsonObject { ["effort"] = reasoningEffort };
            }
            else thinking = new() { ["type"] = "enabled", ["budget_tokens"] = Budget(config), ["display"] = "summarized" };
            if (BetaHeader(config).Split(',').Contains("dev-full-thinking-2025-05-14")) thinking.Remove("display");
            body["thinking"] = thinking;
        }
        else if (config.ReasoningEffort == "none" && CanDisable)
        {
            body["thinking"] = new JsonObject { ["type"] = "disabled" };
            if (Family.Contains("claude-opus-5") && body["output_config"]?["effort"]?.ToString() is "xhigh" or "max")
            {
                ProviderLogger.WarnOnce($"{ModelName}: disabled thinking limits effort to high.");
                body["output_config"] = new JsonObject { ["effort"] = "high" };
            }
        }
        foreach (var pair in Options.BodyExtras(config)) body[pair.Key] = pair.Value?.DeepClone();
        return body;
    }
    public string BetaHeader(GenerateConfig config)
    {
        var betas = new List<string>();
        void Add(string? value) { if (value is not null) betas.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); }
        foreach (var option in new[] { "betas", "anthropic_beta" })
            if (Options.Values[option] is JsonArray array) foreach (var value in array) Add(value?.ToString()); else Add(Options.String(option));
        foreach (var pair in Options.Headers.Concat(config.ExtraHeaders ?? new Dictionary<string, string>()))
            if (pair.Key.Equals("anthropic-beta", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("anthropic_beta", StringComparison.OrdinalIgnoreCase)) Add(pair.Value);
        if (config.Effort is not null) Add("effort-2025-11-24");
        if (config.ResponseSchema is not null) Add("structured-outputs-2025-11-13");
        if (Thinking(config) && (config.MaxTokens ?? MaxTokensForConfig(config)) > 8192) Add("output-128k-2025-02-19");
        if (Thinking(config) && (Claude4 || Claude5 || Latest)) Add("interleaved-thinking-2025-05-14");
        return string.Join(',', betas.Distinct(StringComparer.Ordinal));
    }
    protected override void AddHeaders(HttpRequestMessage request, GenerateConfig config)
    {
        request.Headers.Remove("anthropic-version");
        request.Headers.Add("anthropic-version", AnthropicFoundryModelApi.AnthropicVersion);
        request.Headers.Remove("anthropic_beta");
        request.Headers.Remove("anthropic-beta");
        if (BetaHeader(config) is { Length: > 0 } beta) request.Headers.Add("anthropic-beta", beta);
    }
}

using System.Text.Json.Nodes;

namespace InspectAzureAI.Provider.Core;

/// <summary>
/// Subset of <c>GenerateConfig</c> (<c>src/inspect_ai/model/_generate_config.py</c>) relevant to the
/// azureai provider. Only <c>frequency_penalty</c>, <c>presence_penalty</c>, <c>temperature</c>,
/// <c>top_p</c>, <c>max_tokens</c>, <c>stop_seqs</c> and <c>seed</c> are forwarded by
/// <see cref="AzureAIModelApi.CompletionParams"/>; the remaining fields are carried so callers can
/// observe that they are silently ignored, exactly as in Python.
/// </summary>
public sealed record GenerateConfig
{
    public int? MaxRetries { get; init; }

    public int? Timeout { get; init; }

    public int? MaxConnections { get; init; }

    public string? SystemMessage { get; init; }

    public int? MaxTokens { get; init; }

    public double? TopP { get; init; }

    public double? Temperature { get; init; }

    public IReadOnlyList<string>? StopSeqs { get; init; }

    public int? BestOf { get; init; }

    public double? FrequencyPenalty { get; init; }

    public double? PresencePenalty { get; init; }

    public int? Seed { get; init; }

    public int? TopK { get; init; }

    public int? NumChoices { get; init; }

    public bool? Logprobs { get; init; }

    public int? TopLogprobs { get; init; }

    public bool? ParallelToolCalls { get; init; }

    public string? ReasoningEffort { get; init; }

    public int? ReasoningTokens { get; init; }

    public int? StreamIdleTimeout { get; init; }

    /// <summary>Timeout (in seconds) for any given attempt; on expiry the attempt is abandoned and retried according to the retry policy.</summary>
    public int? AttemptTimeout { get; init; }

    /// <summary>
    /// Port of <c>adaptive_connections</c> (<c>bool | int | AdaptiveConcurrency</c>): a bool, an int (the maximum) or
    /// an adaptive-concurrency settings object. Carried only; connection scheduling lives in the eval engine.
    /// </summary>
    public object? AdaptiveConnections { get; init; }

    /// <summary>Map token ids to a bias value from -100 to 100 (OpenAI, Grok and vLLM only; not sent by the Foundry providers).</summary>
    public IReadOnlyDictionary<int, double>? LogitBias { get; init; }

    private readonly int? _promptLogprobs;

    /// <summary>Number of log probabilities to return per prompt token (1-20, validated like Python; vLLM only).</summary>
    public int? PromptLogprobs
    {
        get => _promptLogprobs;
        init
        {
            if (value is < 1 or > 20)
            {
                throw new ArgumentOutOfRangeException(nameof(PromptLogprobs), value, "prompt_logprobs must be between 1 and 20");
            }

            _promptLogprobs = value;
        }
    }

    /// <summary>Whether to automatically map tools to model internal implementations (e.g. <c>computer</c> for Anthropic).</summary>
    public bool? InternalTools { get; init; }

    /// <summary>Maximum tool output (in bytes). Defaults to 16 * 1024.</summary>
    public int? MaxToolOutput { get; init; }

    /// <summary>Port of <c>cache_prompt</c> (<c>Literal["auto"] | bool</c>): <c>true</c>, <c>false</c> or the string <c>"auto"</c>.</summary>
    public object? CachePrompt { get; init; }

    /// <summary>
    /// Fallback models tried in order when the model's safety classifiers refuse the request. Anthropic Claude
    /// API only: Python ignores it (with a one-time warning) on Bedrock/Vertex/Azure, so both Foundry routes ignore it too.
    /// </summary>
    public IReadOnlyList<string>? FallbackModels { get; init; }

    /// <summary>Constrains the verbosity of the response: <c>low</c>, <c>medium</c> or <c>high</c> (GPT 5.x only).</summary>
    public string? Verbosity { get; init; }

    /// <summary>Response effort: <c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c> or <c>max</c> (Anthropic Claude Opus 4.5+ only).</summary>
    public string? Effort { get; init; }

    /// <summary>Reasoning mode: <c>standard</c> or <c>pro</c> (OpenAI GPT-5.6+ only).</summary>
    public string? ReasoningMode { get; init; }

    /// <summary>Summary of reasoning steps: <c>none</c>, <c>concise</c>, <c>detailed</c> or <c>auto</c> (OpenAI reasoning models only).</summary>
    public string? ReasoningSummary { get; init; }

    /// <summary>Include reasoning in the chat history sent to generate: <c>none</c>, <c>all</c>, <c>last</c> or <c>auto</c>.</summary>
    public string? ReasoningHistory { get; init; }

    /// <summary>
    /// Request a response format as JSON Schema (output should still be validated). Sent as
    /// <c>response_format: json_schema</c> on the azureai route and as <c>output_format</c> on the Anthropic route.
    /// </summary>
    public ResponseSchema? ResponseSchema { get; init; }

    /// <summary>Extra headers to be sent with requests (not supported for AzureAI in Python; carried only here).</summary>
    public IReadOnlyDictionary<string, string>? ExtraHeaders { get; init; }

    /// <summary>Extra body fields for OpenAI-compatible servers (carried only; use model args for Foundry extras).</summary>
    public JsonObject? ExtraBody { get; init; }

    /// <summary>Additional output modalities to enable beyond text (OpenAI and Google only).</summary>
    public IReadOnlyList<OutputModality>? Modalities { get; init; }

    /// <summary>Port of <c>cache</c> (<c>bool | CachePolicy</c>): the policy for caching of model generations.</summary>
    public object? Cache { get; init; }

    /// <summary>Port of <c>batch</c> (<c>bool | int | BatchConfig</c>); see <see cref="GenerateConfigUtil.NormalizedBatchConfig"/>.</summary>
    public object? Batch { get; init; }
}

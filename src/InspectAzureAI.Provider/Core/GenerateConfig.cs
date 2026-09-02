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
}

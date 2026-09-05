using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Model;

/// <summary>Port of <c>GenerateConfig.merge</c>: every non-null field of the override wins.</summary>
public static class GenerateConfigExtensions
{
    public static GenerateConfig Merge(this GenerateConfig baseConfig, GenerateConfig? overrides)
    {
        ArgumentNullException.ThrowIfNull(baseConfig);
        if (overrides is null)
        {
            return baseConfig;
        }

        return baseConfig with
        {
            MaxRetries = overrides.MaxRetries ?? baseConfig.MaxRetries,
            Timeout = overrides.Timeout ?? baseConfig.Timeout,
            MaxConnections = overrides.MaxConnections ?? baseConfig.MaxConnections,
            SystemMessage = overrides.SystemMessage ?? baseConfig.SystemMessage,
            MaxTokens = overrides.MaxTokens ?? baseConfig.MaxTokens,
            TopP = overrides.TopP ?? baseConfig.TopP,
            Temperature = overrides.Temperature ?? baseConfig.Temperature,
            StopSeqs = overrides.StopSeqs ?? baseConfig.StopSeqs,
            BestOf = overrides.BestOf ?? baseConfig.BestOf,
            FrequencyPenalty = overrides.FrequencyPenalty ?? baseConfig.FrequencyPenalty,
            PresencePenalty = overrides.PresencePenalty ?? baseConfig.PresencePenalty,
            Seed = overrides.Seed ?? baseConfig.Seed,
            TopK = overrides.TopK ?? baseConfig.TopK,
            NumChoices = overrides.NumChoices ?? baseConfig.NumChoices,
            Logprobs = overrides.Logprobs ?? baseConfig.Logprobs,
            TopLogprobs = overrides.TopLogprobs ?? baseConfig.TopLogprobs,
            ParallelToolCalls = overrides.ParallelToolCalls ?? baseConfig.ParallelToolCalls,
            ReasoningEffort = overrides.ReasoningEffort ?? baseConfig.ReasoningEffort,
            ReasoningTokens = overrides.ReasoningTokens ?? baseConfig.ReasoningTokens,
            StreamIdleTimeout = overrides.StreamIdleTimeout ?? baseConfig.StreamIdleTimeout,
        };
    }
}

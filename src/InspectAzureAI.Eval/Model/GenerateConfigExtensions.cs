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
            AttemptTimeout = overrides.AttemptTimeout ?? baseConfig.AttemptTimeout,
            AdaptiveConnections = overrides.AdaptiveConnections ?? baseConfig.AdaptiveConnections,
            LogitBias = overrides.LogitBias ?? baseConfig.LogitBias,
            PromptLogprobs = overrides.PromptLogprobs ?? baseConfig.PromptLogprobs,
            InternalTools = overrides.InternalTools ?? baseConfig.InternalTools,
            MaxToolOutput = overrides.MaxToolOutput ?? baseConfig.MaxToolOutput,
            CachePrompt = overrides.CachePrompt ?? baseConfig.CachePrompt,
            FallbackModels = overrides.FallbackModels ?? baseConfig.FallbackModels,
            Verbosity = overrides.Verbosity ?? baseConfig.Verbosity,
            Effort = overrides.Effort ?? baseConfig.Effort,
            ReasoningMode = overrides.ReasoningMode ?? baseConfig.ReasoningMode,
            ReasoningSummary = overrides.ReasoningSummary ?? baseConfig.ReasoningSummary,
            ReasoningHistory = overrides.ReasoningHistory ?? baseConfig.ReasoningHistory,
            ResponseSchema = overrides.ResponseSchema ?? baseConfig.ResponseSchema,
            ExtraHeaders = overrides.ExtraHeaders ?? baseConfig.ExtraHeaders,
            ExtraBody = overrides.ExtraBody ?? baseConfig.ExtraBody,
            Modalities = overrides.Modalities ?? baseConfig.Modalities,
            Cache = overrides.Cache ?? baseConfig.Cache,
            Batch = overrides.Batch ?? baseConfig.Batch,
        };
    }
}

using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Provider.Util;

/// <summary>The vendor behind a Foundry deployment, as far as the reasoning parameter mapping cares.</summary>
public enum ModelFamilyHint
{
    Unknown,

    /// <summary>gpt-5 / o-series: reasoning models that take <c>reasoning_effort</c>.</summary>
    OpenAI,

    /// <summary>Other OpenAI chat models (gpt-4o, …): no reasoning controls (gpt-4o rejects <c>reasoning_effort</c>).</summary>
    OpenAILegacy,

    Router,
    XAI,
    Microsoft,
    DeepSeek,
    MoonshotAI,
    Cohere,
    Mistral,
    Anthropic,
}

/// <summary>How a family switches thinking on and off.</summary>
public enum ThinkingToggle
{
    /// <summary>No request field controls thinking (or the effort field alone does).</summary>
    None,

    /// <summary>An OpenAI-style <c>thinking: {"type": "enabled" | "disabled"}</c> object (DeepSeek, Kimi, Cohere).</summary>
    EnabledDisabled,

    /// <summary>Anthropic's <c>thinking: {"type": "adaptive"}</c> (handled by <c>AnthropicFoundryModelApi</c>).</summary>
    Adaptive,
}

/// <summary>What a family accepts for reasoning, for diagnostics (<c>config</c> / <c>naming</c>) and the mapping itself.</summary>
public sealed record ReasoningSupport(string? EffortKey, ThinkingToggle Toggle, string? BudgetKey, string Notes);

/// <summary>
/// Port-only: Inspect's Python providers forward <c>reasoning_effort</c> only where the vendor API defines it
/// (openai-compatible and anthropic); the Foundry model-inference route fronts several vendors, so this
/// table maps <c>GenerateConfig.ReasoningEffort</c> / <c>ReasoningTokens</c> to each family's wire field.
/// The table is seeded from vendor documentation and corrected by the sample's <c>params</c> probe; a
/// <c>--model-arg</c> with the same key always wins because model args are applied after these.
/// </summary>
public static class ReasoningParams
{
    /// <summary>Accepted <c>reasoning_effort</c> values (Inspect's set plus <c>none</c> for "thinking off").</summary>
    public static readonly IReadOnlyList<string> EffortLevels = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// The family: the ARM <c>Format</c> string wins when given (<c>OpenAI</c>, <c>xAI</c>, <c>Microsoft</c>,
    /// <c>DeepSeek</c>, <c>MoonshotAI</c>, <c>Cohere</c>, <c>Mistral AI</c>, <c>Anthropic</c>), otherwise the
    /// deployment name decides (<c>model-router</c>, gpt-/o-series, grok, mai-, deepseek, kimi, cohere/command,
    /// claude, mistral).
    /// </summary>
    public static ModelFamilyHint FamilyOf(string? format, string serviceModelName)
    {
        var name = serviceModelName.ToLowerInvariant();
        if (name.Contains("model-router"))
        {
            return ModelFamilyHint.Router;
        }

        switch (format?.Trim().ToLowerInvariant())
        {
            case "openai":
                return OpenAIUtil.NeedsMaxCompletionTokens(name) ? ModelFamilyHint.OpenAI : ModelFamilyHint.OpenAILegacy;
            case "xai":
                return ModelFamilyHint.XAI;
            case "microsoft":
                return ModelFamilyHint.Microsoft;
            case "deepseek":
                return ModelFamilyHint.DeepSeek;
            case "moonshotai":
                return ModelFamilyHint.MoonshotAI;
            case "cohere":
                return ModelFamilyHint.Cohere;
            case "mistral ai" or "mistral":
                return ModelFamilyHint.Mistral;
            case "anthropic":
                return ModelFamilyHint.Anthropic;
        }

        if (AzureAIModelApi.IsOpenAIModelName(name))
        {
            return OpenAIUtil.NeedsMaxCompletionTokens(name) ? ModelFamilyHint.OpenAI : ModelFamilyHint.OpenAILegacy;
        }

        if (name.Contains("grok"))
        {
            return ModelFamilyHint.XAI;
        }

        if (name.StartsWith("mai-", StringComparison.Ordinal))
        {
            return ModelFamilyHint.Microsoft;
        }

        if (name.Contains("deepseek"))
        {
            return ModelFamilyHint.DeepSeek;
        }

        if (name.Contains("kimi"))
        {
            return ModelFamilyHint.MoonshotAI;
        }

        if (name.Contains("cohere") || name.StartsWith("command", StringComparison.Ordinal))
        {
            return ModelFamilyHint.Cohere;
        }

        if (name.Contains("claude"))
        {
            return ModelFamilyHint.Anthropic;
        }

        if (AzureAIModelApi.IsMistralModel(name))
        {
            return ModelFamilyHint.Mistral;
        }

        return ModelFamilyHint.Unknown;
    }

    /// <summary>The reasoning controls of a family (the single place to correct after a probe run).</summary>
    public static ReasoningSupport Describe(ModelFamilyHint family) => family switch
    {
        ModelFamilyHint.OpenAI => new("reasoning_effort", ThinkingToggle.None, null,
            "gpt-5 / o-series: reasoning_effort (none|minimal|low|medium|high|xhigh); the reasoning text is never returned, usage.completion_tokens_details.reasoning_tokens counts it"),
        ModelFamilyHint.OpenAILegacy => new(null, ThinkingToggle.None, null,
            "OpenAI chat model without reasoning (gpt-4o rejects reasoning_effort with HTTP 400); nothing is derived, like Inspect's gating"),
        ModelFamilyHint.Router => new("reasoning_effort", ThinkingToggle.None, null,
            "model-router: reasoning_effort is forwarded to the routed model; reasoning_content is streamed when that model exposes it"),
        ModelFamilyHint.XAI => new("reasoning_effort", ThinkingToggle.None, null,
            "grok-4.6: reasoning_effort accepted (grok always reasons; none does not switch it off); the text is hidden, reasoning_tokens is reported"),
        ModelFamilyHint.Microsoft => new("reasoning_effort", ThinkingToggle.None, null,
            "MAI-Thinking-1: reasoning_effort scales reasoning_tokens (low < high); the text is hidden; needs max_completion_tokens"),
        ModelFamilyHint.DeepSeek => new("reasoning_effort", ThinkingToggle.None, null,
            "DeepSeek V4 on Foundry: reasoning_effort turns thinking on (off by default) and reasoning_content comes back; the thinking {type} object is ignored (probed 3 Sep 2026)"),
        ModelFamilyHint.MoonshotAI => new(null, ThinkingToggle.EnabledDisabled, null,
            "Kimi K2: thinking on by default, reasoning_content returned; thinking {type: disabled} is accepted but ignored on Foundry (probed 3 Sep 2026)"),
        ModelFamilyHint.Cohere => new(null, ThinkingToggle.EnabledDisabled, "token_budget",
            "Cohere command: thinking {type: enabled, token_budget} sets the budget, reasoning_content returned; {type: disabled} is accepted but ignored on Foundry (probed 3 Sep 2026)"),
        ModelFamilyHint.Mistral => new(null, ThinkingToggle.None, null,
            "Mistral: no reasoning control on the chat-completions route"),
        ModelFamilyHint.Anthropic => new("output_config.effort", ThinkingToggle.Adaptive, "budget_tokens",
            "Claude: thinking {type: adaptive} + output_config.effort (low|medium|high|max); budget_tokens is the deprecated 4.6 form; served on the Anthropic Messages route"),
        _ => new(null, ThinkingToggle.None, null,
            "unknown family: nothing is derived; pass -M reasoning_effort=... or -M thinking={...} explicitly"),
    };

    /// <summary>
    /// The body fields for the config's reasoning settings on the model-inference route; empty when neither
    /// <c>ReasoningEffort</c> nor <c>ReasoningTokens</c> is set, or the family has no control (like Inspect's
    /// gating of <c>reasoning_effort</c> to gpt-5 / o-series). Effort families get the value verbatim;
    /// toggle families get <c>thinking</c> enabled (with the budget where the vendor has one), or disabled
    /// for effort <c>none</c>. The Anthropic family is mapped by its own provider.
    /// </summary>
    public static JsonObject RequestParams(ModelFamilyHint family, GenerateConfig config)
    {
        var parameters = new JsonObject();
        var effort = config.ReasoningEffort?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(effort) && config.ReasoningTokens is null)
        {
            return parameters;
        }

        var support = Describe(family);
        if (support.EffortKey is not null && support.Toggle == ThinkingToggle.None && !string.IsNullOrEmpty(effort))
        {
            parameters[support.EffortKey] = effort;
        }

        if (support.Toggle == ThinkingToggle.EnabledDisabled)
        {
            if (effort == "none")
            {
                parameters["thinking"] = new JsonObject { ["type"] = "disabled" };
            }
            else
            {
                var thinking = new JsonObject { ["type"] = "enabled" };
                if (support.BudgetKey is not null && config.ReasoningTokens is > 0)
                {
                    thinking[support.BudgetKey] = config.ReasoningTokens;
                }

                parameters["thinking"] = thinking;
            }
        }

        return parameters;
    }
}

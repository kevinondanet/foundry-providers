namespace InspectAzureAI.Eval.Model.Cost;

/// <summary>Port of <c>model/_model_data/model_data.py</c> <c>ModelInfo</c>: model information and metadata.</summary>
public sealed record ModelInfo
{
    /// <summary>Model organization (e.g. Anthropic, OpenAI).</summary>
    public string? Organization { get; init; }

    /// <summary>Model name (e.g. Gemini 2.5 Flash).</summary>
    public string? Model { get; init; }

    /// <summary>A snapshot (version) string, if available (e.g. "latest" or "20240229").</summary>
    public string? Snapshot { get; init; }

    /// <summary>The model's release date.</summary>
    public DateOnly? ReleaseDate { get; init; }

    /// <summary>The model's knowledge cutoff date.</summary>
    public DateOnly? KnowledgeCutoffDate { get; init; }

    /// <summary>The model's context length in tokens.</summary>
    public int? ContextLength { get; init; }

    /// <summary>The model's maximum output tokens.</summary>
    public int? OutputTokens { get; init; }

    /// <summary>Is this a reasoning model.</summary>
    public bool? Reasoning { get; init; }

    /// <summary>
    /// Documented provider default for <c>reasoning_effort</c> on this model: one of the standard effort values
    /// or a sentinel such as <c>adaptive</c> or <c>fixed</c>. Null means undocumented. Metadata only; never sent.
    /// </summary>
    public string? ReasoningEffortDefault { get; init; }

    /// <summary>
    /// Reference model name used for capability and request-shape detection. When set, capability checks match
    /// against this string instead of the configured model name; it does not change the identifier sent to the provider.
    /// </summary>
    public string? Family { get; init; }

    /// <summary>Cost per million tokens for this model.</summary>
    public ModelCost? Cost { get; init; }

    /// <summary>Explicit <c>input_tokens</c> from the model data (Python's private <c>_input_tokens</c>); null falls back to <see cref="ContextLength"/>.</summary>
    public int? InputTokensOverride { get; init; }

    /// <summary>
    /// Port of the <c>input_tokens</c> property: the effective input capacity in tokens, the explicit value
    /// from the model data when set, otherwise <see cref="ContextLength"/>.
    /// </summary>
    public int? InputTokens => InputTokensOverride ?? ContextLength;
}

namespace InspectAzureAI.Provider.Core;

/// <summary>
/// The generate contract shared by the model-inference provider (<c>AzureAIModelApi</c>) and the
/// Anthropic Messages companion (<c>AnthropicFoundryModelApi</c>), so a host can drive any Foundry
/// deployment through one call shape. Mirrors the Python <c>ModelAPI.generate</c> surface the sample uses.
/// </summary>
public interface IModelApi
{
    /// <summary>Model or deployment name as given.</summary>
    string ModelName { get; }

    /// <summary>Default <c>max_tokens</c> for the model family (null: let the service decide).</summary>
    int? MaxTokens();

    /// <summary>Generates a completion; retryable failures are thrown, a terminal 400 is returned in the result.</summary>
    Task<GenerateResult> GenerateAsync(
        IReadOnlyList<ChatMessage> input,
        IReadOnlyList<ToolInfo> tools,
        ToolChoice toolChoice,
        GenerateConfig config,
        StreamHandler? onStream,
        CancellationToken cancellationToken = default);

    /// <summary>Generates without streaming callbacks.</summary>
    Task<GenerateResult> GenerateAsync(
        IReadOnlyList<ChatMessage> input,
        IReadOnlyList<ToolInfo> tools,
        ToolChoice toolChoice,
        GenerateConfig config,
        CancellationToken cancellationToken = default) =>
        GenerateAsync(input, tools, toolChoice, config, null, cancellationToken);
}

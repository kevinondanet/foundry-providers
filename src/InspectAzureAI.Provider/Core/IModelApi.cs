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

    /// <summary>Resolved endpoint; null is reserved for local/in-memory implementations.</summary>
    string? BaseUrl => null;
    string ProviderName => GetType().Name.Replace("ModelApi", "", StringComparison.Ordinal).ToLowerInvariant();
    string QualifiedModelName => ModelName;
    bool IsFoundry => false;
    IReadOnlyDictionary<string, object?> ModelArgsForLog => new Dictionary<string, object?>();
    int? MaxTokensForConfig(GenerateConfig config) => MaxTokens();
    RetryDecision ShouldRetry(Exception ex) => Util.HttpRetryUtil.RetryDecisionFor(ex);
    bool IsAuthFailure(Exception ex) => false;
    bool CollapseUserMessages() => false;
    bool SupportsRemoteMcp() => false;
    bool ApplyRedactedReasoningTokensToInput() => false;

    /// <summary>Default <c>max_tokens</c> for the model family (null: let the service decide).</summary>
    int? MaxTokens();

    /// <summary>Port of <c>ModelAPI.max_connections()</c>: the connection limit when the config sets none (Python: 10).</summary>
    int MaxConnections() => 10;

    /// <summary>
    /// Port of <c>ModelAPI.connection_key()</c>: the scope within which <see cref="MaxConnections"/> (and adaptive
    /// concurrency) is enforced. Instances of one provider returning the same key share a connection pool; the
    /// model layer adds the provider namespace, so distinct providers never collide.
    /// </summary>
    string ConnectionKey() => "default";

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

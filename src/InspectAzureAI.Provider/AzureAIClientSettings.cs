using Azure.AI.Inference;
using Azure.Core;
using Azure.Core.Pipeline;

namespace InspectAzureAI.Provider;

/// <summary>
/// Host-side knobs that have no Python counterpart: an injectable transport so tests and demos run
/// offline, an optional credential replacing <c>DefaultAzureCredential</c>, and a hook to tweak the SDK
/// client options.
/// </summary>
public sealed record AzureAIClientSettings
{
    /// <summary>HTTP transport used by the <c>ChatCompletionsClient</c> (defaults to the SDK's HttpClient transport).</summary>
    public HttpPipelineTransport? Transport { get; init; }

    /// <summary>Credential used for Entra ID auth instead of <c>DefaultAzureCredential</c>.</summary>
    public TokenCredential? TokenCredential { get; init; }

    /// <summary>Last-chance customisation of the SDK client options (retry policy, diagnostics, ...).</summary>
    public Action<AzureAIInferenceClientOptions>? ConfigureClientOptions { get; init; }
}

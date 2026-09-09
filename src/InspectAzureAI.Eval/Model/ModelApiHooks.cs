using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.OpenAI;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Model;

/// <summary>
/// The <c>ModelAPI</c> hooks (<c>should_retry</c>, <c>collapse_user_messages</c>) that <c>IModelApi</c> does not
/// declare, resolved by pattern-matching the concrete providers; other apis get the HTTP-status default.
/// </summary>
internal static class ModelApiHooks
{
    public static RetryDecision ShouldRetry(IModelApi api, Exception ex) => api switch
    {
        AzureAIModelApi azure => azure.ShouldRetry(ex),
        AnthropicFoundryModelApi anthropic => anthropic.ShouldRetry(ex),
        OpenAIResponsesModelApi responses => responses.ShouldRetry(ex),
        ScriptedModelApi { ShouldRetry: { } scripted } => scripted(ex),
        FallbackModelApi fallback => ShouldRetry(fallback.Current, ex),
        _ => DefaultShouldRetry(ex),
    };

    public static bool CollapseUserMessages(IModelApi api) => api switch
    {
        AzureAIModelApi azure => azure.CollapseUserMessages(),
        // The Messages API requires strict user/assistant alternation.
        AnthropicFoundryModelApi => true,
        // The Responses API takes any sequence of input items.
        OpenAIResponsesModelApi => false,
        ScriptedModelApi scripted => scripted.CollapseUserMessages,
        FallbackModelApi fallback => CollapseUserMessages(fallback.Current),
        _ => false,
    };

    /// <summary>
    /// Port of <c>ModelAPI.supports_remote_mcp()</c>: whether the api executes remote MCP servers itself. Only the
    /// Anthropic Messages route does (Python: anthropic and openai, except on Bedrock/Vertex); the model-inference
    /// route has no MCP connector, the Responses route does not port the <c>mcp</c> built-in tool yet, and a
    /// scripted api opts in with <see cref="ScriptedModelApi.SupportsRemoteMcp"/>.
    /// </summary>
    public static bool SupportsRemoteMcp(IModelApi api) => api switch
    {
        AnthropicFoundryModelApi => true,
        OpenAIResponsesModelApi => false,
        ScriptedModelApi scripted => scripted.SupportsRemoteMcp,
        FallbackModelApi fallback => SupportsRemoteMcp(fallback.Current),
        _ => false,
    };

    /// <summary>
    /// Port of the providers' <c>connection_key()</c> overrides (azureai: <c>f"{api_key}:{model_name}"</c>): the
    /// Foundry routes authenticate with Entra rather than an api key, so the endpoint stands in for the account;
    /// any other api uses its own <see cref="IModelApi.ConnectionKey"/>.
    /// </summary>
    public static string ConnectionKey(IModelApi api) => api switch
    {
        AzureAIModelApi azure => $"{azure.EndpointUrl}:{azure.ModelName}",
        AnthropicFoundryModelApi anthropic => $"{anthropic.BaseUrl}:{anthropic.ModelName}",
        OpenAIResponsesModelApi responses => $"{responses.BaseUrl}:{responses.ModelName}",
        _ => api.ConnectionKey(),
    };

    /// <summary>Port of <c>ModelAPI.base_url</c> (a prompt cache key component): the resolved endpoint of the concrete providers, null for any other api.</summary>
    public static string? BaseUrl(IModelApi api) => api switch
    {
        AzureAIModelApi azure => azure.EndpointUrl,
        AnthropicFoundryModelApi anthropic => anthropic.BaseUrl,
        OpenAIResponsesModelApi responses => responses.BaseUrl,
        _ => null,
    };

    /// <summary>
    /// Port of <c>ModelAPI.is_auth_failure</c>: a 401 from the Foundry providers (their own overrides), the same
    /// rule for a scripted api so the hooks' auth-failure retry is testable, false for any other api (Python's base
    /// class default).
    /// </summary>
    public static bool IsAuthFailure(IModelApi api, Exception ex) => api switch
    {
        AzureAIModelApi azure => azure.IsAuthFailure(ex),
        AnthropicFoundryModelApi anthropic => anthropic.IsAuthFailure(ex),
        OpenAIResponsesModelApi responses => responses.IsAuthFailure(ex),
        ScriptedModelApi => HttpRetryUtil.StatusCodeOf(ex) == 401,
        FallbackModelApi fallback => IsAuthFailure(fallback.Current, ex),
        _ => false,
    };

    private static RetryDecision DefaultShouldRetry(Exception ex)
    {
        var status = HttpRetryUtil.StatusCodeOf(ex) ?? 0;
        if (!HttpRetryUtil.IsRetryableHttpStatus(status))
        {
            return RetryDecision.No();
        }

        var retryAfter = HttpRetryUtil.ParseRetryAfterFromException(ex);
        return status == 429 ? RetryDecision.RateLimit(retryAfter) : RetryDecision.Transient(retryAfter);
    }
}

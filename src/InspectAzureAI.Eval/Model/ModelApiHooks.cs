using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;
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
        ScriptedModelApi { ShouldRetry: { } scripted } => scripted(ex),
        _ => DefaultShouldRetry(ex),
    };

    public static bool CollapseUserMessages(IModelApi api) => api switch
    {
        AzureAIModelApi azure => azure.CollapseUserMessages(),
        // The Messages API requires strict user/assistant alternation.
        AnthropicFoundryModelApi => true,
        ScriptedModelApi scripted => scripted.CollapseUserMessages,
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
        _ => api.ConnectionKey(),
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

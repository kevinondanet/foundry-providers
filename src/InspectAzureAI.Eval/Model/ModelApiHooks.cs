using InspectAzureAI.Provider.Core;
namespace InspectAzureAI.Eval.Model;
/// <summary>Compatibility facade over the provider-owned behavior contracts.</summary>
internal static class ModelApiHooks
{
    public static RetryDecision ShouldRetry(IModelApi api, Exception ex) => api.ShouldRetry(ex);
    public static bool CollapseUserMessages(IModelApi api) => api.CollapseUserMessages();
    public static bool SupportsRemoteMcp(IModelApi api) => api.SupportsRemoteMcp();
    public static string ConnectionKey(IModelApi api) => api.ConnectionKey();
    public static string? BaseUrl(IModelApi api) => api.BaseUrl;
    public static bool IsAuthFailure(IModelApi api, Exception ex) => api.IsAuthFailure(ex);
}

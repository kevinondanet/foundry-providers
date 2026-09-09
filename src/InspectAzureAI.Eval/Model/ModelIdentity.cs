using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Model;

/// <summary>Recording identity is separate from the bare model used on the wire and in historical Foundry caches.</summary>
public static class ModelIdentity
{
    public static string ForLog(IModelApi api) => api.IsFoundry ? api.ModelName : api.QualifiedModelName;
    public static string ForCache(IModelApi api) => api.IsFoundry ? api.ModelName : api.QualifiedModelName;
}

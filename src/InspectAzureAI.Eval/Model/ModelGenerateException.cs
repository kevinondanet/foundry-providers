using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Model;

/// <summary>A terminal provider failure (<c>GenerateResult.Error</c>, an HTTP 400) surfaced by <c>Model.GenerateAsync</c>.</summary>
public sealed class ModelGenerateException(string message, Exception? inner, ModelCall? call) : Exception(message, inner)
{
    /// <summary>The recorded request/response, when the provider produced one.</summary>
    public ModelCall? Call { get; } = call;
}

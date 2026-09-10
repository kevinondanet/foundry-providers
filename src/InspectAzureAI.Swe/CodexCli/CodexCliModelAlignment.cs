using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.OpenAI;

namespace InspectAzureAI.Swe.CodexCli;

/// <summary>
/// The served-model adapter of <c>_codex_cli/codex_cli.py</c> <c>resolve_codex_model</c> (with
/// <c>is_openai_derived_api</c>, <c>is_latest_openai_model</c> and <c>openai_service_model_name</c> of
/// <c>model_catalog.py</c>): decides whether the served API counts as OpenAI and as a "latest" model, then applies
/// <see cref="CodexCliModelCatalog.ResolveSlug"/>.
/// </summary>
public static class CodexCliModelAlignment
{
    /// <summary>
    /// The slug for <paramref name="api"/>. OpenAI means <see cref="OpenAIModelApi"/> or <see cref="OpenAIResponsesModelApi"/>
    /// (Python: an <c>OpenAIAPI</c> subclass); latest is <see cref="OpenAIModelApi.IsLatest"/>, or
    /// <see cref="OpenAIModelApi.IsLatestModelName"/> for the Responses API. The name is <see cref="IModelApi.ModelName"/>,
    /// the deployment name on Foundry (deviation D-X9: Python uses <c>service_model_name()</c>).
    /// </summary>
    public static CodexModelResolution Resolve(IModelApi api, JsonObject? catalog, string? modelConfig)
    {
        ArgumentNullException.ThrowIfNull(api);
        var openAi = api is OpenAIModelApi or OpenAIResponsesModelApi;
        var isLatest = api is OpenAIModelApi openAiApi
            ? openAiApi.IsLatest
            : api is OpenAIResponsesModelApi && OpenAIModelApi.IsLatestModelName(api.ModelName);
        return CodexCliModelCatalog.ResolveSlug(api.ModelName, openAi, catalog, modelConfig, isLatest);
    }
}

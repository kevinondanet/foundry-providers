using InspectAzureAI.Provider.Foundry;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Model.Cost;

/// <summary>
/// Port-only overlay for Azure AI Foundry: maps a deployment name (what a caller passes to the provider and what
/// <see cref="Model.Name"/> reports, e.g. <c>gpt-5.4-mini</c>, <c>DeepSeek-V4-Flash-0731</c>, <c>grok-4.6</c>) to
/// the <c>organization/model</c> key of the metadata database, using the vendor classification of
/// <see cref="ReasoningParams.FamilyOf"/> (the ARM <c>Format</c> when known, else the name). Python's own
/// <c>azureai/</c> handling only recognises gpt/o-series, claude, mistral, gemini, kimi and deepseek names; the
/// overlay adds the vendor names Foundry serves (xAI) and ARM-format-driven mapping.
/// </summary>
public static class FoundryModelOverlay
{
    /// <summary>The database organization key of a vendor family, or null when the database has no such organization.</summary>
    public static string? OrganizationKey(ModelFamilyHint family) => family switch
    {
        ModelFamilyHint.OpenAI or ModelFamilyHint.OpenAILegacy => "openai",
        ModelFamilyHint.XAI => "grok",
        ModelFamilyHint.DeepSeek => "deepseek",
        ModelFamilyHint.MoonshotAI => "moonshotai",
        ModelFamilyHint.Mistral => "mistral",
        ModelFamilyHint.Anthropic => "anthropic",
        _ => null,
    };

    /// <summary>
    /// The candidate database key for a deployment: <c>organization/modelName</c> from the family of
    /// <paramref name="format"/> (the ARM model format, when known) and <paramref name="modelName"/>, or null for
    /// families without a database organization (model-router, Microsoft, Cohere, unknown).
    /// </summary>
    public static string? BaseModelKey(string modelName, string? format = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelName);
        return OrganizationKey(ReasoningParams.FamilyOf(format, modelName)) is { } organization
            ? ModelData.ModelKey(organization, modelName)
            : null;
    }

    /// <summary>The candidate database key of an ARM deployment, from its base model name and format.</summary>
    public static string? BaseModelKey(FoundryDeployment deployment)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        return BaseModelKey(deployment.Model, deployment.Format);
    }

    /// <summary>
    /// Registers each deployment's name as an alias of its base model's metadata, so a deployment with a custom
    /// name (say <c>prod-gpt</c> serving <c>gpt-4o</c>) resolves like the base model. A name already in the custom
    /// registry (a user override, typically carrying prices) is left untouched. Returns the names registered.
    /// </summary>
    public static IReadOnlyList<string> RegisterDeployments(IEnumerable<FoundryDeployment> deployments)
    {
        ArgumentNullException.ThrowIfNull(deployments);
        var registered = new List<string>();
        foreach (var deployment in deployments)
        {
            if (ModelInfoLookup.GetCustomModelInfo(deployment.Name) is not null || BaseModelKey(deployment) is not { } key)
            {
                continue;
            }

            if (ModelInfoLookup.LookupInDb(key) is { } info)
            {
                ModelInfoLookup.SetModelInfo(deployment.Name, info);
                registered.Add(deployment.Name);
            }
        }

        return registered;
    }
}

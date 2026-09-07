using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Model;

/// <summary>Port of <c>get_model()</c> for this repo's Foundry providers: picks the route and wraps the api in a <see cref="Model"/>.</summary>
public static class FoundryModels
{
    public const string ModelVar = "INSPECT_AZUREAI_MODEL";

    public const string DefaultModel = "gpt-5.4-mini";

    /// <summary>Route selection: names starting with "claude" (case-insensitive) or route "anthropic" → AnthropicFoundryModelApi, else AzureAIModelApi. Model name default: $INSPECT_AZUREAI_MODEL, else "gpt-5.4-mini".</summary>
    public static IModelApi CreateApi(
        string? model,
        string? route = null,
        object? streaming = null,
        IReadOnlyDictionary<string, object?>? modelArgs = null,
        AzureAIClientSettings? settings = null,
        GenerateConfig? config = null)
    {
        model ??= Environment.GetEnvironmentVariable(ModelVar);
        if (string.IsNullOrWhiteSpace(model))
        {
            model = DefaultModel;
        }

        if (route is not null
            && !route.Equals("models", StringComparison.OrdinalIgnoreCase)
            && !route.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"route expects models or anthropic, got '{route}'", nameof(route));
        }

        var anthropic = route?.Equals("anthropic", StringComparison.OrdinalIgnoreCase) == true
            || model.StartsWith("claude", StringComparison.OrdinalIgnoreCase);
        return anthropic
            ? new AnthropicFoundryModelApi(model, config: config, streaming: streaming, modelArgs: modelArgs, settings: settings)
            : new AzureAIModelApi(model, config: config, streaming: streaming, modelArgs: modelArgs, settings: settings);
    }

    public static Model Create(
        string? model,
        GenerateConfig? config = null,
        string? route = null,
        object? streaming = null,
        IReadOnlyDictionary<string, object?>? modelArgs = null,
        AzureAIClientSettings? settings = null,
        ModelRetryOptions? retry = null) =>
        new(CreateApi(model, route, streaming, modelArgs, settings, config), config, retry);
}

using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.OpenAI;

namespace InspectAzureAI.Eval.Model;

/// <summary>Shared model-specification resolution for library and command-line callers.</summary>
public static class Models
{
    public static IModelApi CreateApi(string? model, GenerateConfig? config = null, string? baseUrl = null,
        string? route = null, object? streaming = null, IReadOnlyDictionary<string, object?>? modelArgs = null,
        AzureAIClientSettings? settings = null)
    {
        model ??= Environment.GetEnvironmentVariable("INSPECT_EVAL_MODEL") ?? Environment.GetEnvironmentVariable(FoundryModels.ModelVar) ?? FoundryModels.DefaultModel;
        if (model.Split('/', 2) is [var provider, var rest])
        {
            route = provider switch
            {
                "openai" => "responses", "anthropic" => "anthropic",
                "azureai" or "azure" or "foundry" => "models",
                _ => throw new PrerequisiteError($"Unknown model provider '{provider}'."),
            };
            model = rest;
        }
        return FoundryModels.RouteFor(model, route) switch
        {
            "responses" => new OpenAIResponsesModelApi(model, baseUrl, config, streaming, modelArgs, settings),
            "anthropic" => new AnthropicFoundryModelApi(model, baseUrl, config, streaming, modelArgs, settings),
            "models" => new AzureAIModelApi(model, baseUrl, config, streaming, modelArgs: modelArgs, settings: settings),
            _ => throw new PrerequisiteError("route expects models, anthropic or responses"),
        };
    }
    public static Model Create(string? model, GenerateConfig? config = null, string? baseUrl = null,
        string? route = null, object? streaming = null, IReadOnlyDictionary<string, object?>? modelArgs = null,
        AzureAIClientSettings? settings = null, ModelRetryOptions? retry = null) =>
        new(CreateApi(model, config, baseUrl, route, streaming, modelArgs, settings), config, retry);
}

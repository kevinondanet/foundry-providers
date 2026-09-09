using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.OpenAI;

namespace InspectAzureAI.Eval.Model;

/// <summary>Shared model-specification resolution for library and command-line callers.</summary>
public static class Models
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<string, GenerateConfig?, string?, IReadOnlyDictionary<string, object?>?, Model>> Factories = new(StringComparer.Ordinal);
    public static void Register(string provider, Func<string, GenerateConfig?, string?, IReadOnlyDictionary<string, object?>?, Model> factory) => Factories[provider] = factory;
    public static bool Unregister(string provider) => Factories.TryRemove(provider, out _);
    private static string DefaultName(string? model) => string.IsNullOrWhiteSpace(model)
        ? Environment.GetEnvironmentVariable("INSPECT_EVAL_MODEL") ?? Environment.GetEnvironmentVariable(FoundryModels.ModelVar) ?? FoundryModels.DefaultModel : model;

    private static IModelApi CreateDirect(string provider, string model, GenerateConfig? config, string? baseUrl,
        object? streaming, IReadOnlyDictionary<string, object?>? modelArgs, DirectClientSettings? settings)
    {
        settings ??= new();
        settings = settings with { ApiKeyOverride = settings.ApiKeyOverride ?? ((name, value) =>
        {
            foreach (var hook in Hooks.HookEmitter.ActiveHooks.Where(h => h.Enabled))
            {
                try { if (hook.OverrideApiKey(new Hooks.ApiKeyOverride(name, value ?? "")) is { } overridden) return overridden; }
                catch (Exception) { Provider.Util.ProviderLogger.WarnOnce("A credential override hook failed; trying the remaining hooks."); }
            }
            return null;
        }) };
        return provider == "openai" ? new OpenAIModelApi(model, baseUrl, config: config, streaming: streaming, modelArgs: modelArgs, settings: settings)
            : new AnthropicModelApi(model, baseUrl, config: config, streaming: streaming, modelArgs: modelArgs, settings: settings);
    }
    public static IModelApi CreateApi(string? model, GenerateConfig? config = null, string? baseUrl = null,
        string? route = null, object? streaming = null, IReadOnlyDictionary<string, object?>? modelArgs = null,
        AzureAIClientSettings? settings = null, DirectClientSettings? directSettings = null)
    {
        model = DefaultName(model);
        if (model.Split('/', 2) is [var registered, _] && Factories.TryGetValue(registered, out var factory)) return factory(model, config, baseUrl, modelArgs).Api;
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
        AzureAIClientSettings? settings = null, ModelRetryOptions? retry = null, DirectClientSettings? directSettings = null)
    {
        model = DefaultName(model);
        if (model.Split('/', 2) is [var provider, _] && Factories.TryGetValue(provider, out var factory)) return factory(model, config, baseUrl, modelArgs);
        return new(CreateApi(model, config, baseUrl, route, streaming, modelArgs, settings, directSettings), config, retry);
    }
}

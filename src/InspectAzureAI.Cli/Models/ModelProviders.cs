using InspectAzureAI.Eval.Model;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Cli.Models;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>What a <c>--model</c> name resolved to: the full name, its provider prefix, the rest, and the call's options.</summary>
public sealed record ModelSpec(string Name, string Provider, string ModelName, GenerateConfig? Config, string? BaseUrl, IReadOnlyDictionary<string, object?>? ModelArgs);

/// <summary>Builds a <see cref="Model"/> for a spec whose provider prefix is registered.</summary>
public delegate Model ModelFactory(ModelSpec spec);

/// <summary>
/// Port of the provider half of <c>get_model()</c> for the CLI: a <c>provider/name</c> model string is routed to a
/// registered factory (<c>mockllm/model</c> is built in, as in Python; tests register their own), the
/// <c>azureai/</c> and <c>anthropic/</c> prefixes pick the Foundry route explicitly, and a bare deployment name goes
/// to <see cref="FoundryModels"/> (<c>claude*</c> to the Anthropic route). A name with an unknown prefix is a
/// <see cref="PrerequisiteError"/>. With no name, <c>INSPECT_EVAL_MODEL</c> is read, then the Foundry defaults.
/// </summary>
public static class ModelProviders
{
    /// <summary>Python's environment variable for the default eval model.</summary>
    public const string EvalModelVar = "INSPECT_EVAL_MODEL";

    private static readonly object Gate = new();

    private static readonly Dictionary<string, ModelFactory> Factories = new(StringComparer.Ordinal)
    {
        ["mockllm"] = spec => new Model(new MockLlmModelApi(spec.Name, spec.ModelArgs), spec.Config),
    };

    /// <summary>The registered provider prefixes plus the Foundry routes.</summary>
    public static IReadOnlyList<string> Providers
    {
        get
        {
            lock (Gate)
            {
                return Factories.Keys.Concat(["azureai", "anthropic"]).Order(StringComparer.Ordinal).ToList();
            }
        }
    }

    /// <summary>Registers (or replaces) the factory for <paramref name="provider"/> — the port's equivalent of a <c>@modelapi(name)</c> provider.</summary>
    public static void Register(string provider, ModelFactory factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(factory);
        lock (Gate)
        {
            Factories[provider] = factory;
        }
    }

    /// <summary>Removes a registered provider; false when there was none.</summary>
    public static bool Unregister(string provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (Gate)
        {
            return Factories.Remove(provider);
        }
    }

    /// <summary>Resolves a model name (see the class summary) to a <see cref="Model"/>.</summary>
    public static Model Resolve(string? name, GenerateConfig? config = null, string? baseUrl = null, IReadOnlyDictionary<string, object?>? modelArgs = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Environment.GetEnvironmentVariable(EvalModelVar);
        }

        string? route = null;
        var deployment = name;
        if (!string.IsNullOrWhiteSpace(name) && name.Split('/', 2) is [var provider, var rest])
        {
            ModelFactory? factory;
            lock (Gate)
            {
                Factories.TryGetValue(provider, out factory);
            }

            if (factory is not null)
            {
                return factory(new ModelSpec(name, provider, rest, config, baseUrl, modelArgs));
            }

            switch (provider)
            {
                case "azureai":
                case "azure":
                case "foundry":
                    route = "models";
                    deployment = rest;
                    break;
                case "anthropic":
                    route = "anthropic";
                    deployment = rest;
                    break;
                default:
                    throw new PrerequisiteError($"Unknown model provider '{provider}' in '{name}'. Known providers: {string.Join(", ", Providers)}; a bare deployment name (e.g. 'gpt-5.4-mini') uses Azure AI Foundry.");
            }
        }

        if (baseUrl is null)
        {
            return FoundryModels.Create(deployment, config, route, modelArgs: modelArgs);
        }

        deployment = string.IsNullOrWhiteSpace(deployment) ? Environment.GetEnvironmentVariable(FoundryModels.ModelVar) : deployment;
        if (string.IsNullOrWhiteSpace(deployment))
        {
            deployment = FoundryModels.DefaultModel;
        }

        var anthropic = route == "anthropic" || deployment.StartsWith("claude", StringComparison.OrdinalIgnoreCase);
        IModelApi api = anthropic
            ? new AnthropicFoundryModelApi(deployment, baseUrl, config, modelArgs: modelArgs)
            : new AzureAIModelApi(deployment, baseUrl, config, modelArgs: modelArgs);
        return new Model(api, config);
    }
}

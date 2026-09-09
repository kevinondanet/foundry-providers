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

    static ModelProviders()
    {
        foreach (var pair in Factories) RegisterShared(pair.Key, pair.Value);
    }
    private static void RegisterShared(string provider, ModelFactory factory) => InspectAzureAI.Eval.Model.Models.Register(provider,
        (name, config, baseUrl, args) => factory(new ModelSpec(name, provider, name.Split('/', 2)[1], config, baseUrl, args)));

    /// <summary>The registered provider prefixes plus the Foundry routes.</summary>
    public static IReadOnlyList<string> Providers
    {
        get
        {
            lock (Gate)
            {
                return Factories.Keys.Concat(["azureai", "anthropic", "openai"]).Order(StringComparer.Ordinal).ToList();
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
            RegisterShared(provider, factory);
        }
    }

    /// <summary>Removes a registered provider; false when there was none.</summary>
    public static bool Unregister(string provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (Gate)
        {
            InspectAzureAI.Eval.Model.Models.Unregister(provider);
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

        return InspectAzureAI.Eval.Model.Models.Create(name, config, baseUrl, modelArgs: modelArgs);
    }
}

using InspectAzureAI.Eval.Sandbox.Docker;
using InspectAzureAI.Eval.Sandbox.Local;

namespace InspectAzureAI.Eval.Sandbox;

/// <summary>
/// Port of the <c>@sandboxenv</c> registry (<c>util/_sandbox/registry.py</c>). "local" and "docker" are
/// registered here (a module initializer is unavailable to a library under CA2255); external providers
/// call <see cref="Register"/>.
/// </summary>
public static class SandboxRegistry
{
    private static readonly object Sync = new();

    private static readonly Dictionary<string, ISandboxProvider> Providers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["local"] = new LocalSandboxProvider(),
        ["docker"] = new DockerSandboxProvider(),
    };

    /// <summary>Registers (or replaces) the provider for <c>provider.Type</c>.</summary>
    public static void Register(ISandboxProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (Sync)
        {
            Providers[provider.Type] = provider;
        }
    }

    /// <summary>Looks up a provider by type; an unknown type is an <see cref="ArgumentException"/> naming the known types.</summary>
    public static ISandboxProvider Get(string type)
    {
        lock (Sync)
        {
            if (Providers.TryGetValue(type, out var provider))
            {
                return provider;
            }

            var known = string.Join(", ", Providers.Keys.Order(StringComparer.Ordinal));
            throw new ArgumentException($"Unknown sandbox environment type '{type}' (known types: {known}).", nameof(type));
        }
    }

    /// <summary>Registered type names.</summary>
    public static IReadOnlyList<string> Types
    {
        get
        {
            lock (Sync)
            {
                return Providers.Keys.Order(StringComparer.Ordinal).ToArray();
            }
        }
    }
}

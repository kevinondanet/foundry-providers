using System.Reflection;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Hooks;

/// <summary>Port of the registry metadata Python's <c>@hooks(name, description)</c> decorator records for a hook.</summary>
/// <param name="Name">Name of the subscriber (e.g. "audit logging").</param>
/// <param name="Description">Short description of the hook (e.g. "Copies eval files to S3 bucket for auditing.").</param>
public sealed record HookInfo(string Name, string Description);

/// <summary>
/// Port of the <c>hooks</c> registry type of <c>_util/registry.py</c> plus <c>get_all_hooks</c>, <c>has_api_key_override</c>
/// and <c>override_api_key</c> of <c>hooks/_hooks.py</c>. Python instantiates and registers a hook class at import
/// time (the <c>@hooks</c> decorator) and loads entry-point hooks; this port has no import-time or entry-point
/// discovery, so hosts call <see cref="Register"/> explicitly before running evals. Registered hooks are process
/// wide and receive every emission; per-run hooks go in <c>EvalOptions.Hooks</c> instead.
/// </summary>
public static class HookRegistry
{
    private static readonly object Gate = new();

    private static readonly OrderedDictionary<string, Hooks> Registered = new(StringComparer.Ordinal);

    private static readonly Dictionary<Hooks, HookInfo> Infos = new(ReferenceEqualityComparer.Instance);

    private static Hooks[] _snapshot = [];

    /// <summary>
    /// Port of the <c>@hooks(name, description)</c> decorator for an already constructed instance. Registering
    /// under a name that is taken replaces the earlier hook in place (Python's <c>registry_add</c> overwrites the
    /// key); the same instance may be registered once only.
    /// </summary>
    public static void Register(Hooks hook, string name, string description)
    {
        ArgumentNullException.ThrowIfNull(hook);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(description);
        lock (Gate)
        {
            if (Infos.ContainsKey(hook) && !(Registered.TryGetValue(name, out var same) && ReferenceEquals(same, hook)))
            {
                throw new InvalidOperationException($"Hook instance of type '{hook.GetType().Name}' is already registered as '{Infos[hook].Name}'.");
            }

            if (Registered.TryGetValue(name, out var previous))
            {
                Infos.Remove(previous);
            }

            Registered[name] = hook;
            Infos[hook] = new HookInfo(name, description);
            _snapshot = Registered.Values.ToArray();
        }
    }

    /// <summary>Removes the hook registered as <paramref name="name"/>; false when there is none.</summary>
    public static bool Unregister(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (Gate)
        {
            if (!Registered.Remove(name, out var hook))
            {
                return false;
            }

            Infos.Remove(hook);
            _snapshot = Registered.Values.ToArray();
            return true;
        }
    }

    /// <summary>Removes every registered hook (tests and host shutdown).</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Registered.Clear();
            Infos.Clear();
            _snapshot = [];
        }
    }

    /// <summary>Port of <c>get_all_hooks()</c>: every registered hook, enabled or not, in registration order.</summary>
    public static IReadOnlyList<Hooks> All => Volatile.Read(ref _snapshot);

    /// <summary>Port of <c>registry_lookup("hooks", name)</c>.</summary>
    public static Hooks? Lookup(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (Gate)
        {
            return Registered.TryGetValue(name, out var hook) ? hook : null;
        }
    }

    /// <summary>Port of <c>registry_info(hook)</c>: the name and description a hook was registered with, or null.</summary>
    public static HookInfo? Info(Hooks hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        lock (Gate)
        {
            return Infos.TryGetValue(hook, out var info) ? info : null;
        }
    }

    /// <summary>
    /// Port of <c>has_api_key_override()</c>: whether any registered hook overrides
    /// <see cref="Hooks.OverrideApiKey"/> (enabled or not, as in Python).
    /// </summary>
    public static bool HasApiKeyOverride => All.Any(OverridesApiKey);

    /// <summary>
    /// Port of <c>override_api_key(env_var_name, value)</c>: asks each enabled registered hook in turn and returns
    /// the first non-null override; a hook that throws is logged and skipped. Null when no hook overrides the key
    /// (Python then falls back to the legacy <c>INSPECT_API_KEY_OVERRIDE</c> import, which is not ported).
    /// </summary>
    public static string? OverrideApiKey(string envVarName, string value)
    {
        ArgumentNullException.ThrowIfNull(envVarName);
        ArgumentNullException.ThrowIfNull(value);
        var data = new ApiKeyOverride(envVarName, value);
        foreach (var hook in All)
        {
            if (!hook.Enabled)
            {
                continue;
            }

            try
            {
                if (hook.OverrideApiKey(data) is { } overridden)
                {
                    return overridden;
                }
            }
            catch (Exception ex)
            {
                ProviderLogger.Warning($"Exception calling override_api_key on hook '{hook.GetType().Name}': {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>Port of the MRO walk in <c>has_api_key_override</c>: true when the hook's type overrides <see cref="Hooks.OverrideApiKey"/>.</summary>
    public static bool OverridesApiKey(Hooks hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        var method = hook.GetType().GetMethod(nameof(Hooks.OverrideApiKey), BindingFlags.Public | BindingFlags.Instance, [typeof(ApiKeyOverride)]);
        return method is not null && method.DeclaringType != typeof(Hooks);
    }
}

using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Hooks;

/// <summary>
/// Port of <c>hooks/_startup.py</c>: the once-per-process hook load that verifies <c>INSPECT_REQUIRED_HOOKS</c> and
/// announces the enabled hooks. Python runs it from <c>get_model()</c>; this port runs it at the start of every
/// <c>Eval.RunAsync</c> (only the first call in the process does any work). The legacy <c>INSPECT_TELEMETRY</c> /
/// <c>INSPECT_API_KEY_OVERRIDE</c> module imports and entry-point discovery are not ported.
/// </summary>
public static class HookStartup
{
    /// <summary>Env var naming the hooks that must be registered, comma separated (e.g. <c>package/hooks_1,package/hooks_2</c>).</summary>
    public const string RequiredHooksVar = "INSPECT_REQUIRED_HOOKS";

    private static readonly object Gate = new();

    private static bool _registryHooksLoaded;

    /// <summary>
    /// Port of <c>init_hooks()</c>: on the first call in the process, verifies the required hooks (a missing one is
    /// a <see cref="PrerequisiteError"/>) and returns a message listing the enabled hooks; later calls return no
    /// messages. When there are messages and <paramref name="print"/> is given, the banner Python prints
    /// (<c>inspect_ai v{version}</c> plus one line per message) is passed to it.
    /// </summary>
    public static IReadOnlyList<string> InitHooks(Action<string>? print = null)
    {
        var messages = new List<string>();
        var registryHooks = LoadRegistryHooks();
        if (registryHooks.Count > 0)
        {
            var enabled = registryHooks.Where(hook => hook.Enabled).ToList();
            var names = enabled.Select(hook => $"  {FormatHookForPrinting(hook)}");
            messages.Add($"hooks enabled: {enabled.Count}\n{string.Join("\n", names)}");
        }

        if (messages.Count > 0 && print is not null)
        {
            var version = typeof(HookStartup).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
            print($"InspectAzureAI v{version}\n{string.Join("\n", messages.Select(message => $"- {message}"))}");
        }

        return messages;
    }

    /// <summary>
    /// Port of <c>_verify_all_required_hooks</c>: every name in <see cref="RequiredHooksVar"/> must be the
    /// registered name of one of <paramref name="installed"/>, else a <see cref="PrerequisiteError"/>.
    /// </summary>
    public static void VerifyAllRequiredHooks(IReadOnlyList<Hooks> installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var requiredHooksEnvVar = Environment.GetEnvironmentVariable(RequiredHooksVar) ?? "";
        var requiredNames = requiredHooksEnvVar.Split(',').Where(name => name.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (requiredNames.Count == 0)
        {
            return;
        }

        var installedNames = installed.Select(hook => HookRegistry.Info(hook)?.Name).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var missingNames = requiredNames.Except(installedNames).Order(StringComparer.Ordinal).ToList();
        if (missingNames.Count > 0)
        {
            throw new PrerequisiteError(
                $"Required hook(s) missing: {FormatSet(missingNames)}.\n"
                + $"{RequiredHooksVar} is set to '{requiredHooksEnvVar}'.\n"
                + $"Installed hooks: {FormatSet(installedNames.Order(StringComparer.Ordinal))}.\n"
                + "Please ensure required hooks are registered with HookRegistry.Register before the eval runs.");
        }
    }

    /// <summary>Forgets that the registry hooks were loaded so the next <see cref="InitHooks"/> verifies and announces again (tests only).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _registryHooksLoaded = false;
        }
    }

    /// <summary>Port of <c>_load_registry_hooks</c>: the registered hooks on the first call, an empty list afterwards. The loaded flag is set before verifying, as in Python.</summary>
    private static IReadOnlyList<Hooks> LoadRegistryHooks()
    {
        lock (Gate)
        {
            if (_registryHooksLoaded)
            {
                return [];
            }

            var hooks = HookRegistry.All;
            _registryHooksLoaded = true;
            VerifyAllRequiredHooks(hooks);
            return hooks;
        }
    }

    private static string FormatHookForPrinting(Hooks hook) =>
        HookRegistry.Info(hook) is { } info ? $"{info.Name}: {info.Description}" : hook.GetType().Name;

    /// <summary>Python's set repr, sorted for a stable message.</summary>
    private static string FormatSet(IEnumerable<string> names) => "{" + string.Join(", ", names.Select(name => $"'{name}'")) + "}";
}

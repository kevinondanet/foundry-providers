namespace InspectAzureAI.Provider.Util;

/// <summary>
/// Stand-in for the api-key override hook in <c>inspect_ai.hooks._hooks</c>
/// (<c>override_api_key</c> / <c>has_api_key_override</c>), consulted by
/// <see cref="AzureAIModelApi"/> exactly as <c>ModelAPI._apply_api_key_overrides</c> does.
/// </summary>
public static class ModelApiHooks
{
    /// <summary>
    /// Hook receiving <c>(env_var_name, value)</c> and returning a replacement key or null. Null when
    /// no hook is registered.
    /// </summary>
    public static Func<string, string, string?>? OverrideApiKey { get; set; }

    /// <summary>
    /// Port of <c>has_api_key_override()</c>. In Inspect this reflects whether any hook <em>class</em>
    /// registering an override is installed, which is independent of the module-level
    /// <c>override_api_key</c> function (the Python test patches only the function), so it is a separate
    /// flag here too. When true and no key exists anywhere, the hook is still offered an empty value so
    /// it can supply credentials from its own source.
    /// </summary>
    public static bool HasApiKeyOverride { get; set; }
}

namespace InspectAzureAI.Eval.Hooks;

/// <summary>
/// Port of <c>ModelAPI._apply_api_key_overrides</c> of <c>model/_model.py</c>: the credential resolution step a
/// provider with api-key environment variables runs at construction. This repo's Foundry providers authenticate
/// with Entra ID only (see <c>AzureHosting</c> / <c>AzureAIClientSettings</c>), so nothing in the solution calls
/// it; it is the entry point for a provider that does take an api key.
/// </summary>
public static class ApiKeyOverrides
{
    /// <summary>
    /// For each variable in <paramref name="apiKeyVars"/>: an explicit <paramref name="apiKey"/> is offered to
    /// the hooks and replaced by their answer; otherwise the variable's environment value is offered and the
    /// environment is updated with the answer; otherwise, when a hook overrides api keys at all, the hook is
    /// asked with an empty value so it can supply credentials from its own source. Returns the api key to use
    /// (the original when no hook answered).
    /// </summary>
    public static string? Apply(IReadOnlyList<string> apiKeyVars, string? apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKeyVars);
        foreach (var key in apiKeyVars)
        {
            if (apiKey is not null)
            {
                if (HookRegistry.OverrideApiKey(key, apiKey) is { } overridden)
                {
                    apiKey = overridden;
                }
            }
            else
            {
                var value = Environment.GetEnvironmentVariable(key);
                if (value is not null)
                {
                    if (HookRegistry.OverrideApiKey(key, value) is { } overridden)
                    {
                        Environment.SetEnvironmentVariable(key, overridden);
                    }
                }
                else if (HookRegistry.HasApiKeyOverride)
                {
                    if (HookRegistry.OverrideApiKey(key, "") is { } overridden)
                    {
                        apiKey = overridden;
                    }
                }
            }
        }

        return apiKey;
    }
}

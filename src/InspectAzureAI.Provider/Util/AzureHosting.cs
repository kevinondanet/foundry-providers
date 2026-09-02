using Azure.Core;
using Azure.Identity;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Provider.Util;

/// <summary>Async token provider returning a bearer token string (port of <c>get_bearer_token_provider</c>'s callable).</summary>
public delegate Task<string> TokenProvider(CancellationToken cancellationToken);

/// <summary>Port of <c>src/inspect_ai/model/_providers/util/azure_hosting.py</c> (the managed-identity helper).</summary>
public static class AzureHosting
{
    /// <summary>Env var naming the token audience.</summary>
    public const string AzureAIAudience = "AZUREAI_AUDIENCE";

    /// <summary>Default audience/scope for Entra ID tokens.</summary>
    public const string DefaultAzureAudience = "https://cognitiveservices.azure.com/.default";

    /// <summary>The scope that will be requested: <c>$AZUREAI_AUDIENCE</c> or the default.</summary>
    public static string ResolveAudience() =>
        Environment.GetEnvironmentVariable(AzureAIAudience) is { Length: > 0 } audience ? audience : DefaultAzureAudience;

    /// <summary>
    /// Port of <c>resolve_azure_token_provider</c>: wraps <see cref="DefaultAzureCredential"/> (or the
    /// supplied credential) in a provider that fetches a token for <see cref="ResolveAudience"/>. The
    /// Python <c>ImportError</c> branch (<c>azure-identity</c> not installed) cannot occur in .NET because
    /// Azure.Identity is a hard dependency; the same message is surfaced if credential creation fails.
    /// </summary>
    public static TokenProvider ResolveAzureTokenProvider(string providerName, TokenCredential? credential = null)
    {
        TokenCredential resolved;
        try
        {
            resolved = credential ?? new DefaultAzureCredential();
        }
        catch (Exception ex)
        {
            throw new PrerequisiteError(
                $"ERROR: The {providerName} provider requires the `azure-identity` package for managed identity support. ({ex.Message})");
        }

        var scope = ResolveAudience();
        return async ct =>
        {
            var token = await resolved.GetTokenAsync(new TokenRequestContext([scope]), ct).ConfigureAwait(false);
            return token.Token;
        };
    }
}

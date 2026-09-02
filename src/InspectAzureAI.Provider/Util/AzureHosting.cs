using Azure.Core;
using Azure.Identity;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Provider.Util;

/// <summary>Async token provider returning a bearer token string (port of <c>get_bearer_token_provider</c>'s callable).</summary>
public delegate Task<string> TokenProvider(CancellationToken cancellationToken);

/// <summary>
/// Port of <c>src/inspect_ai/model/_providers/util/azure_hosting.py</c> (the managed-identity helper),
/// extended so that developer sign-in works out of the box: the credential defaults to
/// <see cref="DefaultAzureCredential"/>, whose chain includes the Azure CLI (<c>az login</c>), the Azure
/// Developer CLI, Visual Studio and managed identity, and <c>AZUREAI_CREDENTIAL</c> can pin one link of
/// that chain. The token scope is always <c>AZUREAI_AUDIENCE</c> (default
/// <see cref="DefaultAzureAudience"/>), exactly as in Python.
/// </summary>
public static class AzureHosting
{
    /// <summary>Env var naming the token audience.</summary>
    public const string AzureAIAudience = "AZUREAI_AUDIENCE";

    /// <summary>Default audience/scope for Entra ID tokens (Azure AI Services and Azure OpenAI resources).</summary>
    public const string DefaultAzureAudience = "https://cognitiveservices.azure.com/.default";

    /// <summary>
    /// Env var selecting the Azure.Identity credential (no Python counterpart; Python always uses
    /// <c>DefaultAzureCredential</c>). One of <see cref="CredentialSelectors"/>; default <c>default</c>.
    /// </summary>
    public const string AzureAICredential = "AZUREAI_CREDENTIAL";

    /// <summary>Standard Azure.Identity tenant pin, honoured by the default, cli, developer-cli and interactive credentials.</summary>
    public const string AzureTenantId = "AZURE_TENANT_ID";

    /// <summary>Standard Azure.Identity client id, selecting a user-assigned managed identity.</summary>
    public const string AzureClientId = "AZURE_CLIENT_ID";

    /// <summary>Accepted values of <c>AZUREAI_CREDENTIAL</c>.</summary>
    public static readonly IReadOnlyList<string> CredentialSelectors =
        ["default", "cli", "developer-cli", "managed-identity", "environment", "interactive"];

    /// <summary>The scope that will be requested: <c>$AZUREAI_AUDIENCE</c> or the default.</summary>
    public static string ResolveAudience() =>
        Environment.GetEnvironmentVariable(AzureAIAudience) is { Length: > 0 } audience ? audience : DefaultAzureAudience;

    /// <summary>The credential selector: <c>$AZUREAI_CREDENTIAL</c> (trimmed, lower-cased) or <c>default</c>.</summary>
    public static string ResolveCredentialSelector() =>
        Environment.GetEnvironmentVariable(AzureAICredential) is { Length: > 0 } selector
            ? selector.Trim().ToLowerInvariant()
            : "default";

    /// <summary>
    /// Creates the Azure.Identity credential named by <paramref name="selector"/> (or by
    /// <c>AZUREAI_CREDENTIAL</c> when null). <c>default</c> builds a <see cref="DefaultAzureCredential"/>,
    /// which is what makes <c>az login</c> work with no further configuration; <c>cli</c> skips the
    /// managed-identity probe on developer machines and uses the Azure CLI directly.
    /// <c>AZURE_TENANT_ID</c> and <c>AZURE_CLIENT_ID</c> are honoured where they apply.
    /// </summary>
    /// <exception cref="PrerequisiteError">The selector is not one of <see cref="CredentialSelectors"/>.</exception>
    public static TokenCredential CreateCredential(string? selector = null)
    {
        selector = (selector ?? ResolveCredentialSelector()).Trim().ToLowerInvariant();
        var tenantId = Environment.GetEnvironmentVariable(AzureTenantId) is { Length: > 0 } tenant ? tenant : null;
        var clientId = Environment.GetEnvironmentVariable(AzureClientId) is { Length: > 0 } client ? client : null;

        return selector switch
        {
            "default" => new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                TenantId = tenantId,
                ManagedIdentityClientId = clientId,
            }),
            "cli" => new AzureCliCredential(new AzureCliCredentialOptions { TenantId = tenantId }),
            "developer-cli" => new AzureDeveloperCliCredential(new AzureDeveloperCliCredentialOptions { TenantId = tenantId }),
            "managed-identity" => new ManagedIdentityCredential(
                clientId is null ? ManagedIdentityId.SystemAssigned : ManagedIdentityId.FromUserAssignedClientId(clientId)),
            "environment" => new EnvironmentCredential(),
            "interactive" => new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions { TenantId = tenantId }),
            _ => throw new PrerequisiteError(
                $"ERROR: {AzureAICredential}='{selector}' is not a supported credential. Use one of: {string.Join(", ", CredentialSelectors)}."),
        };
    }

    /// <summary>
    /// Port of <c>resolve_azure_token_provider</c>, returning the credential itself rather than a bare
    /// callable so it can be handed to the SDK's <c>TokenCredential</c> constructor (which then sends the
    /// token only as <c>Authorization: Bearer</c> and refreshes it as needed). The result is pinned to
    /// <see cref="ResolveAudience"/>: the SDK would otherwise request <c>https://ml.azure.com/.default</c>,
    /// which Azure AI Services and Azure OpenAI endpoints reject. The Python <c>ImportError</c> branch
    /// (<c>azure-identity</c> not installed) cannot occur in .NET because Azure.Identity is a hard
    /// dependency; the same message is surfaced if credential creation fails.
    /// </summary>
    public static AudienceTokenCredential ResolveAzureCredential(string providerName, TokenCredential? credential = null)
    {
        TokenCredential resolved;
        try
        {
            resolved = credential ?? CreateCredential();
        }
        catch (PrerequisiteError)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PrerequisiteError(
                $"ERROR: The {providerName} provider requires the `azure-identity` package for managed identity support. ({ex.Message})");
        }

        return new AudienceTokenCredential(resolved, ResolveAudience());
    }

    /// <summary>Port of <c>resolve_azure_token_provider</c>'s callable shape, built on <see cref="ResolveAzureCredential"/>.</summary>
    public static TokenProvider ResolveAzureTokenProvider(string providerName, TokenCredential? credential = null) =>
        TokenProviderFor(ResolveAzureCredential(providerName, credential));

    /// <summary>Wraps a credential as a token-string provider for <see cref="ResolveAudience"/>.</summary>
    public static TokenProvider TokenProviderFor(TokenCredential credential) =>
        async cancellationToken =>
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext([ResolveAudience()]), cancellationToken).ConfigureAwait(false);
            return token.Token;
        };

    /// <summary>Human-readable description of a credential for diagnostics (never includes secrets).</summary>
    public static string Describe(TokenCredential credential) => credential switch
    {
        AudienceTokenCredential audience => $"{Describe(audience.Inner)}, scope {audience.Scope}",
        DefaultAzureCredential => "DefaultAzureCredential (environment → workload identity → managed identity → Visual Studio → Azure CLI `az login` → Azure PowerShell → Azure Developer CLI)",
        AzureCliCredential => "AzureCliCredential (`az login`)",
        AzureDeveloperCliCredential => "AzureDeveloperCliCredential (`azd auth login`)",
        ManagedIdentityCredential => "ManagedIdentityCredential",
        EnvironmentCredential => "EnvironmentCredential (AZURE_CLIENT_ID / AZURE_CLIENT_SECRET / AZURE_TENANT_ID)",
        InteractiveBrowserCredential => "InteractiveBrowserCredential",
        _ => credential.GetType().Name,
    };
}

/// <summary>
/// A <see cref="TokenCredential"/> that always requests one fixed scope, whatever the SDK asks for.
/// The Azure.AI.Inference client hard-codes <c>https://ml.azure.com/.default</c> for token credentials;
/// wrapping keeps the Python provider's <c>AZUREAI_AUDIENCE</c> semantics while still using the SDK's
/// bearer-token pipeline policy.
/// </summary>
public sealed class AudienceTokenCredential(TokenCredential inner, string scope) : TokenCredential
{
    /// <summary>The credential that actually acquires tokens.</summary>
    public TokenCredential Inner { get; } = inner;

    /// <summary>The scope requested on every call.</summary>
    public string Scope { get; } = scope;

    /// <inheritdoc />
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        Inner.GetToken(Rescope(requestContext), cancellationToken);

    /// <inheritdoc />
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        Inner.GetTokenAsync(Rescope(requestContext), cancellationToken);

    private TokenRequestContext Rescope(TokenRequestContext context) =>
        new([Scope], context.ParentRequestId, context.Claims, context.TenantId);
}

using Azure.Core;
using Azure.Identity;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Provider.Util;

/// <summary>
/// Port of <c>src/inspect_ai/model/_providers/util/azure_hosting.py</c> (the managed-identity helper).
/// The credential is always <see cref="DefaultAzureCredential"/>, whose chain includes the Azure CLI
/// (<c>az login</c>) as well as managed identity, so a developer sign-in and a hosted identity both work
/// with no configuration; the standard Azure.Identity variables (<c>AZURE_TENANT_ID</c>,
/// <c>AZURE_CLIENT_ID</c>) are honoured by the credential itself. The token scope is always
/// <c>AZUREAI_AUDIENCE</c> (default <see cref="DefaultAzureAudience"/>), exactly as in Python.
/// </summary>
public static class AzureHosting
{
    /// <summary>Env var naming the token audience.</summary>
    public const string AzureAIAudience = "AZUREAI_AUDIENCE";

    /// <summary>Default audience/scope for Entra ID tokens (Azure AI Services and Azure OpenAI resources).</summary>
    public const string DefaultAzureAudience = "https://cognitiveservices.azure.com/.default";

    /// <summary>The scope that will be requested: <c>$AZUREAI_AUDIENCE</c> or the default.</summary>
    public static string ResolveAudience() =>
        Environment.GetEnvironmentVariable(AzureAIAudience) is { Length: > 0 } audience ? audience : DefaultAzureAudience;

    /// <summary>
    /// The credential used when the host does not supply one: a <see cref="DefaultAzureCredential"/>,
    /// which is what makes <c>az login</c> work with no further configuration.
    /// </summary>
    public static TokenCredential CreateCredential() => new DefaultAzureCredential();

    /// <summary>
    /// Port of <c>resolve_azure_token_provider</c>, returning the credential itself rather than a bare
    /// callable so it can be handed to the SDK's <c>TokenCredential</c> constructor (which then sends the
    /// token only as <c>Authorization: Bearer</c> and refreshes it as needed). The result is pinned to
    /// <see cref="ResolveAudience"/>: the SDK would otherwise request <c>https://ml.azure.com/.default</c>,
    /// which Azure AI Services and Azure OpenAI endpoints reject. Credential construction failures surface
    /// as <see cref="PrerequisiteError"/> with the Python message.
    /// </summary>
    public static AudienceTokenCredential ResolveAzureCredential(string providerName, TokenCredential? credential = null)
    {
        TokenCredential resolved;
        try
        {
            resolved = credential ?? CreateCredential();
        }
        catch (Exception ex)
        {
            throw new PrerequisiteError(
                $"ERROR: The {providerName} provider requires the `azure-identity` package for managed identity support. ({ex.Message})");
        }

        return new AudienceTokenCredential(resolved, ResolveAudience());
    }

    /// <summary>Human-readable description of a credential for diagnostics (never includes secrets).</summary>
    public static string Describe(TokenCredential credential) => credential switch
    {
        AudienceTokenCredential audience => $"{Describe(audience.Inner)}, scope {audience.Scope}",
        DefaultAzureCredential => "DefaultAzureCredential (environment → workload identity → managed identity → Visual Studio → Azure CLI `az login` → Azure PowerShell → Azure Developer CLI)",
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

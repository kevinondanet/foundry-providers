// ============================================================================
//  LAYER 6: PROVIDERS — what the two real providers share
//  Python: inspect_ai/model/_providers/util/azure_hosting.py
//
//  Both Foundry providers authenticate the same way: an Entra ID bearer
//  token for the Cognitive Services audience, minted by DefaultAzureCredential
//  (environment -> managed identity -> Visual Studio -> `az login` -> ...).
//  One request per process is enough; concurrent samples share it.
//
//  This is the only file in the app with an external dependency
//  (Azure.Identity). No other layer needs to know how tokens are minted.
// ============================================================================
using System.Net;
using Azure.Core;
using Azure.Identity;
using inspect_ai._util.display;

namespace inspect_ai.model._providers;

/// <summary>A non-2xx reply from Foundry. Layer 5 never sees the type; it only asks ShouldRetry and RetryAfter.</summary>
internal sealed class FoundryHttpException(HttpStatusCode status, string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;

    /// <summary>The Retry-After header, when the service sent one (429s usually do).</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>Read Retry-After as either a delay in seconds or an HTTP date.</summary>
    public static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date) return date - DateTimeOffset.UtcNow is { Ticks: > 0 } until ? until : TimeSpan.Zero;
        return null;
    }
}

internal static class EntraToken
{
    private const string Tag = "L6 _providers (token)";
    private const string AudienceVar = "AZUREAI_AUDIENCE";
    private const string DefaultAudience = "https://cognitiveservices.azure.com/.default";

    // Module-level state with no locks, touched only from the event-loop thread.
    // Caching the *task* rather than the token means every caller that needs a
    // token while the first request is in flight simply awaits that request.
    private static readonly TokenCredential Credential = new DefaultAzureCredential();
    private static Task<AccessToken>? _request;

    /// <summary>The current bearer token, refreshed when it is within five minutes of expiry.</summary>
    public static async Task<string> Get()
    {
        var fresh = _request is { IsCompletedSuccessfully: true } done
                    && done.Result.ExpiresOn - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(5);
        if (!fresh && (_request is null || _request.IsCompleted))
            _request = Request();   // none yet, expiring, or failed: start one request that every caller awaits
        return (await _request!).Token;
    }

    private static async Task<AccessToken> Request()
    {
        var scope = Environment.GetEnvironmentVariable(AudienceVar) is { Length: > 0 } audience ? audience : DefaultAudience;
        Display.Step(Tag, $"requesting an Entra ID token for {scope} (env -> managed identity -> Visual Studio -> az login -> ...)");
        var token = await Credential.GetTokenAsync(new TokenRequestContext(new[] { scope }), CancellationToken.None);
        Display.Step(Tag, $"token acquired, expires {token.ExpiresOn:HH:mm:ss}Z");
        return token;
    }
}

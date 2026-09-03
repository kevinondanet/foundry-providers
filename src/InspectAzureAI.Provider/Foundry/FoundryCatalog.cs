using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Azure.Core;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Provider.Foundry;

/// <summary>An Azure AI Foundry (Cognitive Services) account as Azure Resource Manager describes it.</summary>
public sealed record FoundryResource(
    string Id,
    string Name,
    string ResourceGroup,
    string SubscriptionId,
    string Kind,
    string Location,
    string? InferenceEndpoint,
    IReadOnlyList<string> EndpointHosts)
{
    /// <summary>True when <paramref name="host"/> is one of the account's endpoint hosts or is a custom domain whose first label is the account name.</summary>
    public bool Serves(string host) =>
        EndpointHosts.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase))
        || host.StartsWith(Name + ".", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A model deployment on a Foundry account.</summary>
public sealed record FoundryDeployment(
    string Name,
    string Model,
    string Format,
    string? Version,
    string State,
    string? Sku,
    int? Capacity,
    IReadOnlyDictionary<string, string> Capabilities)
{
    /// <summary>Provisioning finished successfully.</summary>
    public bool IsSucceeded => string.Equals(State, "Succeeded", StringComparison.OrdinalIgnoreCase);

    /// <summary>The deployment advertises chat completions (a missing capability map is taken as yes).</summary>
    public bool SupportsChat =>
        !Capabilities.TryGetValue("chatCompletion", out var chat) || string.Equals(chat, "true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Discovers the Foundry resource behind an inference endpoint and lists its model deployments through
/// Azure Resource Manager, using the same Entra identity as the provider (scope
/// <see cref="ManagementScope"/>). No Python counterpart: Inspect takes the model name from the CLI. The
/// identity needs read access on the resource; Cognitive Services User includes
/// <c>Microsoft.CognitiveServices/*/read</c> and subscription read, so a caller of the model can also
/// discover it. The HTTP handler is injectable for offline tests.
/// </summary>
public sealed class FoundryCatalog : IDisposable
{
    /// <summary>Token scope for Azure Resource Manager.</summary>
    public const string ManagementScope = "https://management.azure.com/.default";

    /// <summary>Env var naming the resource id directly (skips subscription and account enumeration).</summary>
    public const string ResourceIdVar = "AZUREAI_RESOURCE_ID";

    /// <summary>Env var narrowing enumeration to one subscription.</summary>
    public const string SubscriptionIdVar = "AZURE_SUBSCRIPTION_ID";

    private const string AccountsApiVersion = "2024-10-01";
    private const string SubscriptionsApiVersion = "2022-12-01";

    private readonly TokenCredential _credential;
    private readonly HttpClient _http;

    public FoundryCatalog(TokenCredential credential, HttpMessageHandler? handler = null, Uri? managementEndpoint = null)
    {
        _credential = credential;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = managementEndpoint ?? new Uri("https://management.azure.com/");
    }

    /// <summary>Finds the resource serving <paramref name="endpointUrl"/> and lists its deployments.</summary>
    /// <exception cref="PrerequisiteError">No readable resource serves the endpoint, or ARM refused the identity.</exception>
    public async Task<(FoundryResource Resource, IReadOnlyList<FoundryDeployment> Deployments)> DiscoverAsync(
        string endpointUrl, string? resourceId = null, string? subscriptionId = null, CancellationToken cancellationToken = default)
    {
        var resource = await FindResourceAsync(endpointUrl, resourceId, subscriptionId, cancellationToken).ConfigureAwait(false)
                       ?? throw new PrerequisiteError(
                           $"ERROR: No Azure AI Foundry resource serving {new Uri(endpointUrl).Host} was found in the subscriptions this identity can read. " +
                           $"Set {ResourceIdVar} to the resource id, or {SubscriptionIdVar} to narrow the search.");
        var deployments = await ListDeploymentsAsync(resource, cancellationToken).ConfigureAwait(false);
        return (resource, deployments);
    }

    /// <summary>
    /// Resolves the account: <paramref name="resourceId"/> (or <c>AZUREAI_RESOURCE_ID</c>) when given,
    /// otherwise every Cognitive Services account in <paramref name="subscriptionId"/> (or
    /// <c>AZURE_SUBSCRIPTION_ID</c>, or every readable subscription) whose endpoints include the host of
    /// <paramref name="endpointUrl"/>.
    /// </summary>
    public async Task<FoundryResource?> FindResourceAsync(
        string endpointUrl, string? resourceId = null, string? subscriptionId = null, CancellationToken cancellationToken = default)
    {
        resourceId ??= Environment.GetEnvironmentVariable(ResourceIdVar);
        if (!string.IsNullOrEmpty(resourceId))
        {
            var node = await GetAsync($"{resourceId.TrimStart('/')}?api-version={AccountsApiVersion}", cancellationToken).ConfigureAwait(false);
            return ParseAccount(node);
        }

        var host = new Uri(endpointUrl).Host;
        subscriptionId ??= Environment.GetEnvironmentVariable(SubscriptionIdVar);
        var subscriptions = string.IsNullOrEmpty(subscriptionId)
            ? await ListSubscriptionsAsync(cancellationToken).ConfigureAwait(false)
            : [subscriptionId];

        foreach (var subscription in subscriptions)
        {
            foreach (var account in await ListAccountsAsync(subscription, cancellationToken).ConfigureAwait(false))
            {
                if (account.Serves(host))
                {
                    return account;
                }
            }
        }

        return null;
    }

    /// <summary>Ids of every subscription the identity can read.</summary>
    public async Task<IReadOnlyList<string>> ListSubscriptionsAsync(CancellationToken cancellationToken = default)
    {
        var items = await ListAsync($"subscriptions?api-version={SubscriptionsApiVersion}", cancellationToken).ConfigureAwait(false);
        return items.Select(s => s?["subscriptionId"]?.ToString()).Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).ToList();
    }

    /// <summary>Every Cognitive Services account (Foundry, Azure OpenAI, AI Services) in a subscription.</summary>
    public async Task<IReadOnlyList<FoundryResource>> ListAccountsAsync(string subscriptionId, CancellationToken cancellationToken = default)
    {
        var items = await ListAsync(
            $"subscriptions/{subscriptionId}/providers/Microsoft.CognitiveServices/accounts?api-version={AccountsApiVersion}",
            cancellationToken).ConfigureAwait(false);
        return items.Select(ParseAccount).ToList();
    }

    /// <summary>Every model deployment on the account, in ARM order.</summary>
    public async Task<IReadOnlyList<FoundryDeployment>> ListDeploymentsAsync(FoundryResource resource, CancellationToken cancellationToken = default)
    {
        var items = await ListAsync($"{resource.Id.TrimStart('/')}/deployments?api-version={AccountsApiVersion}", cancellationToken).ConfigureAwait(false);
        return items.Select(ParseDeployment).ToList();
    }

    /// <summary>Parses an ARM account document.</summary>
    public static FoundryResource ParseAccount(JsonNode? node)
    {
        var id = node?["id"]?.ToString() ?? "";
        var properties = node?["properties"] as JsonObject;
        var endpoints = new List<string>();
        string? inference = null;
        if (properties?["endpoint"]?.ToString() is { Length: > 0 } primary)
        {
            endpoints.Add(HostOf(primary));
        }

        if (properties?["endpoints"] is JsonObject map)
        {
            foreach (var (key, value) in map)
            {
                if (value?.ToString() is { Length: > 0 } url)
                {
                    endpoints.Add(HostOf(url));
                    if (key == "Azure AI Model Inference API")
                    {
                        inference = url.TrimEnd('/') + "/models";
                    }
                }
            }
        }

        return new FoundryResource(
            Id: id,
            Name: node?["name"]?.ToString() ?? "",
            ResourceGroup: SegmentAfter(id, "resourceGroups"),
            SubscriptionId: SegmentAfter(id, "subscriptions"),
            Kind: node?["kind"]?.ToString() ?? "",
            Location: node?["location"]?.ToString() ?? "",
            InferenceEndpoint: inference,
            EndpointHosts: endpoints.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Parses an ARM deployment document.</summary>
    public static FoundryDeployment ParseDeployment(JsonNode? node)
    {
        var properties = node?["properties"] as JsonObject;
        var model = properties?["model"] as JsonObject;
        var capabilities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (properties?["capabilities"] is JsonObject caps)
        {
            foreach (var (key, value) in caps)
            {
                capabilities[key] = value?.ToString() ?? "";
            }
        }

        var capacity = node?["sku"]?["capacity"];
        return new FoundryDeployment(
            Name: node?["name"]?.ToString() ?? "",
            Model: model?["name"]?.ToString() ?? "",
            Format: model?["format"]?.ToString() ?? "",
            Version: model?["version"]?.ToString(),
            State: properties?["provisioningState"]?.ToString() ?? "",
            Sku: node?["sku"]?["name"]?.ToString(),
            Capacity: capacity is JsonValue v && v.TryGetValue<int>(out var n) ? n : null,
            Capabilities: capabilities);
    }

    private async Task<List<JsonNode?>> ListAsync(string path, CancellationToken cancellationToken)
    {
        var items = new List<JsonNode?>();
        string? next = path;
        while (next is not null)
        {
            var page = await GetAsync(next, cancellationToken).ConfigureAwait(false);
            if (page?["value"] is JsonArray value)
            {
                items.AddRange(value);
            }

            next = page?["nextLink"]?.ToString() is { Length: > 0 } link ? link : null;
        }

        return items;
    }

    private async Task<JsonNode?> GetAsync(string pathOrUrl, CancellationToken cancellationToken)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext([ManagementScope]), cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, pathOrUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new PrerequisiteError(
                $"ERROR: Azure Resource Manager returned {(int)response.StatusCode} for {pathOrUrl}. The identity needs read access on the " +
                "Foundry resource (Reader, or Cognitive Services User which includes Microsoft.CognitiveServices/*/read).");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Azure Resource Manager returned {(int)response.StatusCode} for {pathOrUrl}: {body[..Math.Min(body.Length, 300)]}");
        }

        return JsonNode.Parse(body);
    }

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    private static string SegmentAfter(string id, string segment)
    {
        var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < parts.Length; i++)
        {
            if (string.Equals(parts[i], segment, StringComparison.OrdinalIgnoreCase))
            {
                return parts[i + 1];
            }
        }

        return "";
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}

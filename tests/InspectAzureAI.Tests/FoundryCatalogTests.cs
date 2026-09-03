using System.Net;
using System.Text;
using System.Text.Json;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Foundry;

namespace InspectAzureAI.Tests;

/// <summary>Deployment discovery through Azure Resource Manager (no Python counterpart), against canned ARM JSON.</summary>
public class FoundryCatalogTests
{
    private const string Sub = "9bb52a90-0000-0000-0000-000000000001";
    private const string AccountId = $"/subscriptions/{Sub}/resourceGroups/rg-mfa-foundry/providers/Microsoft.CognitiveServices/accounts/myfoundry0406";

    private static object Account(string name, string id, string inferenceHost) => new
    {
        id, name, kind = "AIServices", location = "eastus2",
        properties = new
        {
            endpoint = $"https://{name}.cognitiveservices.azure.com/",
            endpoints = new Dictionary<string, string>
            {
                ["Azure AI Model Inference API"] = $"https://{inferenceHost}/",
                ["OpenAI Language Model Instance API"] = $"https://{name}.openai.azure.com/",
            },
        },
    };

    private static object Deployment(string name, string format, string state, string chat = "true", int capacity = 500) => new
    {
        name,
        sku = new { name = "GlobalStandard", capacity },
        properties = new
        {
            model = new { format, name, version = "2026-01-01" },
            provisioningState = state,
            capabilities = new Dictionary<string, string> { ["chatCompletion"] = chat, ["area"] = "US" },
        },
    };

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static FakeArmHandler ArmHandler() => new(request =>
    {
        var url = request.RequestUri!.ToString();
        string body;
        if (url.Contains("/subscriptions?"))
        {
            body = Json(new { value = new[] { new { subscriptionId = Sub, displayName = "Azure subscription 1" } } });
        }
        else if (url.Contains("/providers/Microsoft.CognitiveServices/accounts?"))
        {
            body = Json(new { value = new[]
            {
                Account("other", $"/subscriptions/{Sub}/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/other", "other.services.ai.azure.com"),
                Account("myfoundry0406", AccountId, "myfoundry0406.services.ai.azure.com"),
            } });
        }
        else if (url.Contains("/deployments?") && !url.Contains("skip"))
        {
            body = Json(new
            {
                value = new[] { Deployment("gpt-5.4-mini", "OpenAI", "Succeeded"), Deployment("DeepSeek-V4-Flash", "DeepSeek", "Succeeded", capacity: 20) },
                nextLink = $"https://management.azure.com{AccountId}/deployments?api-version=2024-10-01&skip=2",
            });
        }
        else if (url.Contains("skip=2"))
        {
            body = Json(new { value = new[] { Deployment("claude-opus-4-7", "Anthropic", "Failed"), Deployment("embed-3", "Cohere", "Succeeded", chat: "false") } });
        }
        else if (url.Contains(AccountId + "?"))
        {
            body = Json(Account("myfoundry0406", AccountId, "myfoundry0406.services.ai.azure.com"));
        }
        else
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    });

    [Fact]
    public async Task discover_matches_the_account_by_inference_host_and_pages_through_deployments()
    {
        using var env = EnvScope.Clean();
        var handler = ArmHandler();
        var credential = new FakeTokenCredential("arm-token");
        using var catalog = new FoundryCatalog(credential, handler);

        var (resource, deployments) = await catalog.DiscoverAsync("https://myfoundry0406.services.ai.azure.com/models");

        Assert.Equal("myfoundry0406", resource.Name);
        Assert.Equal("rg-mfa-foundry", resource.ResourceGroup);
        Assert.Equal(Sub, resource.SubscriptionId);
        Assert.Equal("AIServices", resource.Kind);
        Assert.Equal("https://myfoundry0406.services.ai.azure.com/models", resource.InferenceEndpoint);
        Assert.Equal(["gpt-5.4-mini", "DeepSeek-V4-Flash", "claude-opus-4-7", "embed-3"], deployments.Select(d => d.Name));
        Assert.Equal(4, handler.Requests.Count);                                  // subscriptions, accounts, deployments page 1, page 2
        Assert.All(handler.Requests, r => Assert.Equal("Bearer arm-token", r.Headers.Authorization!.ToString()));
        Assert.All(credential.Scopes, s => Assert.Equal([FoundryCatalog.ManagementScope], s));
    }

    [Fact]
    public async Task resource_id_env_var_skips_enumeration()
    {
        using var env = EnvScope.Clean().Set(FoundryCatalog.ResourceIdVar, AccountId);
        var handler = ArmHandler();
        using var catalog = new FoundryCatalog(new FakeTokenCredential("t"), handler);

        var resource = await catalog.FindResourceAsync("https://custom.example.com/models");

        Assert.Equal("myfoundry0406", resource!.Name);
        Assert.Single(handler.Requests);
        Assert.Contains(AccountId + "?api-version=", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task subscription_env_var_narrows_the_search_and_custom_domains_match_by_name()
    {
        using var env = EnvScope.Clean().Set(FoundryCatalog.SubscriptionIdVar, Sub);
        var handler = ArmHandler();
        using var catalog = new FoundryCatalog(new FakeTokenCredential("t"), handler);

        var resource = await catalog.FindResourceAsync("https://myfoundry0406.mycompany.example/models");   // not an advertised host

        Assert.Equal("myfoundry0406", resource!.Name);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.ToString().Contains("/subscriptions?"));
    }

    [Fact]
    public async Task no_matching_resource_is_a_prerequisite_error()
    {
        using var env = EnvScope.Clean();
        using var catalog = new FoundryCatalog(new FakeTokenCredential("t"), ArmHandler());

        var ex = await Assert.ThrowsAsync<PrerequisiteError>(() => catalog.DiscoverAsync("https://nowhere.services.ai.azure.com/models"));

        Assert.Contains("No Azure AI Foundry resource serving nowhere.services.ai.azure.com", ex.Message);
        Assert.Contains(FoundryCatalog.ResourceIdVar, ex.Message);
    }

    [Fact]
    public async Task arm_forbidden_is_a_prerequisite_error_naming_the_role()
    {
        using var env = EnvScope.Clean();
        var handler = new FakeArmHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":{\"code\":\"AuthorizationFailed\"}}") });
        using var catalog = new FoundryCatalog(new FakeTokenCredential("t"), handler);

        var ex = await Assert.ThrowsAsync<PrerequisiteError>(() => catalog.ListSubscriptionsAsync());

        Assert.Contains("returned 403", ex.Message);
        Assert.Contains("Cognitive Services User", ex.Message);
    }

    [Fact]
    public void deployment_flags_reflect_state_and_chat_capability()
    {
        var ok = FoundryCatalog.ParseDeployment(System.Text.Json.Nodes.JsonNode.Parse(Json(Deployment("gpt-5.4-mini", "OpenAI", "Succeeded"))));
        var failed = FoundryCatalog.ParseDeployment(System.Text.Json.Nodes.JsonNode.Parse(Json(Deployment("claude-opus-4-7", "Anthropic", "Failed"))));
        var embed = FoundryCatalog.ParseDeployment(System.Text.Json.Nodes.JsonNode.Parse(Json(Deployment("embed-3", "Cohere", "Succeeded", chat: "false"))));
        var bare = FoundryCatalog.ParseDeployment(System.Text.Json.Nodes.JsonNode.Parse("""{"name":"x","properties":{"model":{"name":"x","format":"Custom"},"provisioningState":"Succeeded"}}"""));

        Assert.True(ok.IsSucceeded); Assert.True(ok.SupportsChat); Assert.Equal(500, ok.Capacity); Assert.Equal("GlobalStandard", ok.Sku);
        Assert.False(failed.IsSucceeded); Assert.True(failed.SupportsChat);
        Assert.True(embed.IsSucceeded); Assert.False(embed.SupportsChat);
        Assert.True(bare.SupportsChat); Assert.Null(bare.Capacity); Assert.Null(bare.Version);
    }
}

/// <summary>An HttpMessageHandler that answers ARM requests from a function and records them.</summary>
internal sealed class FakeArmHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Request bodies captured at send time (the provider disposes the request afterwards).</summary>
    public List<string?> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
        return responder(request);
    }
}

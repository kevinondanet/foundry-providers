using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Testing;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Tests;

/// <summary>Entra ID sign-in: credential selection, audience pinning and token diagnostics (no Python counterpart).</summary>
public class EntraAuthTests
{
    [Fact]
    public void default_credential_is_default_azure_credential_so_az_login_works()
    {
        using var env = EnvScope.Clean();

        Assert.IsType<DefaultAzureCredential>(AzureHosting.CreateCredential());
        Assert.Equal("default", AzureHosting.ResolveCredentialSelector());
    }

    [Theory]
    [InlineData("default", typeof(DefaultAzureCredential))]
    [InlineData("cli", typeof(AzureCliCredential))]
    [InlineData(" CLI ", typeof(AzureCliCredential))]
    [InlineData("developer-cli", typeof(AzureDeveloperCliCredential))]
    [InlineData("managed-identity", typeof(ManagedIdentityCredential))]
    [InlineData("environment", typeof(EnvironmentCredential))]
    [InlineData("interactive", typeof(InteractiveBrowserCredential))]
    public void azureai_credential_selects_the_credential_type(string selector, Type expected)
    {
        using var env = EnvScope.Clean().Set(AzureHosting.AzureAICredential, selector);

        Assert.IsType(expected, AzureHosting.CreateCredential());
        Assert.IsType(expected, AzureHosting.CreateCredential(selector));
    }

    [Fact]
    public void unknown_credential_selector_is_a_prerequisite_error()
    {
        using var env = EnvScope.Clean().Set(AzureHosting.AzureAICredential, "kerberos");

        var ex = Assert.Throws<PrerequisiteError>(() => AzureHosting.CreateCredential());
        Assert.Contains("AZUREAI_CREDENTIAL='kerberos' is not a supported credential", ex.Message);
        Assert.Contains("default, cli, developer-cli, managed-identity, environment, interactive", ex.Message);

        var api = Assert.Throws<PrerequisiteError>(() => new AzureAIModelApi("m", Fixtures.BaseUrl));
        Assert.Contains("not a supported credential", api.Message);
    }

    [Fact]
    public void user_assigned_managed_identity_comes_from_azure_client_id()
    {
        using var env = EnvScope.Clean()
            .Set(AzureHosting.AzureAICredential, "managed-identity")
            .Set(AzureHosting.AzureClientId, "00000000-0000-0000-0000-000000000001");

        Assert.IsType<ManagedIdentityCredential>(AzureHosting.CreateCredential());
    }

    [Fact]
    public async Task audience_credential_ignores_the_scope_the_sdk_asks_for()
    {
        using var env = EnvScope.Clean();
        var inner = new FakeTokenCredential("t");
        var credential = new AudienceTokenCredential(inner, AzureHosting.DefaultAzureAudience);

        var token = await credential.GetTokenAsync(new TokenRequestContext(["https://ml.azure.com/.default"]), CancellationToken.None);

        Assert.Equal("t", token.Token);
        Assert.Equal([AzureHosting.DefaultAzureAudience], Assert.Single(inner.Scopes));
        Assert.Same(inner, credential.Inner);
    }

    [Fact]
    public async Task custom_audience_reaches_the_credential_and_the_request_is_bearer_only()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIAudienceVar, "https://custom.audience/.default");
        var transport = new CannedTransport { Responder = _ => CannedResponse.Json(200, Fixtures.Completion("ok")) };
        var inner = new FakeTokenCredential("entra-token");
        var api = new AzureAIModelApi("m", Fixtures.BaseUrl, settings: new AzureAIClientSettings
        {
            Transport = transport, TokenCredential = inner, ConfigureClientOptions = o => o.Retry.MaxRetries = 0,
        });

        Assert.Equal("https://custom.audience/.default", api.Credential!.Scope);
        var result = await api.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig());

        Assert.Equal("ok", result.OutputOrThrow().Completion);
        Assert.Equal("Bearer entra-token", transport.LastRequest!.Headers["Authorization"]);
        Assert.False(transport.LastRequest.Headers.ContainsKey("api-key"));
        Assert.All(inner.Scopes, scopes => Assert.Equal(["https://custom.audience/.default"], scopes));
    }

    [Fact]
    public async Task token_provider_shape_still_works_over_the_credential()
    {
        using var env = EnvScope.Clean();
        var api = new AzureAIModelApi("m", Fixtures.BaseUrl, settings: new AzureAIClientSettings { TokenCredential = new FakeTokenCredential("entra-token") });

        Assert.NotNull(api.Credential);
        Assert.Equal("entra-token", await api.TokenProvider!(CancellationToken.None));
    }

    [Fact]
    public void describe_names_az_login_for_the_default_and_cli_credentials()
    {
        using var env = EnvScope.Clean();

        Assert.Contains("az login", AzureHosting.Describe(new DefaultAzureCredential()));
        Assert.Contains("az login", AzureHosting.Describe(new AzureCliCredential()));
        var pinned = new AudienceTokenCredential(new AzureCliCredential(), AzureHosting.DefaultAzureAudience);
        Assert.Contains($"scope {AzureHosting.DefaultAzureAudience}", AzureHosting.Describe(pinned));
    }

    [Fact]
    public void entra_token_info_decodes_the_jwt_payload_without_validating_it()
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["aud"] = "https://cognitiveservices.azure.com",
            ["tid"] = "10f890ff-0000-0000-0000-000000000000",
            ["oid"] = "oid-1",
            ["upn"] = "dev@example.com",
            ["name"] = "Dev Person",
            ["appid"] = "04b07795-8ddb-461a-bbee-02f9e1bf7b46",
            ["scp"] = "user_impersonation openid",
            ["exp"] = 1_800_000_000,
        });
        static string B64Url(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var jwt = $"{B64Url("{\"alg\":\"RS256\"}")}.{B64Url(payload)}.signature";

        var info = EntraTokenInfo.TryParse(jwt);

        Assert.NotNull(info);
        Assert.Equal("https://cognitiveservices.azure.com", info.Audience);
        Assert.Equal("10f890ff-0000-0000-0000-000000000000", info.TenantId);
        Assert.Equal("dev@example.com", info.UserPrincipalName);
        Assert.Equal("Dev Person", info.Name);
        Assert.Equal("04b07795-8ddb-461a-bbee-02f9e1bf7b46", info.AppId);
        Assert.Equal(["user_impersonation", "openid"], info.Scopes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), info.ExpiresOn);
    }

    [Theory]
    [InlineData("opaque-token")]
    [InlineData("a.b")]
    [InlineData("a.!!!.c")]
    public void entra_token_info_is_null_for_non_jwt_tokens(string token) =>
        Assert.Null(EntraTokenInfo.TryParse(token));
}

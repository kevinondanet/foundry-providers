using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Tests;

/// <summary>Constructor env-var resolution (tests/model/providers/test_azureai.py plus suggested cases).</summary>
public class EnvPrecedenceTests
{
    [Fact]
    public void test_explicit_api_key_takes_precedence_over_environment()
    {
        using var env = EnvScope.Clean()
            .Set(AzureAIModelApi.AzureApiKeyVar, "legacy-env-key")
            .Set(AzureAIModelApi.AzureAIApiKeyVar, "env-key");

        var api = new AzureAIModelApi("test-model", Fixtures.BaseUrl, "explicit-key");

        Assert.Equal("explicit-key", api.ApiKey);
        Assert.Null(api.TokenProvider);
    }

    [Fact]
    public void test_azureai_api_key_is_offered_to_override_hook()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIApiKeyVar, "source-key");
        var seen = new List<(string, string)>();
        ModelApiHooks.OverrideApiKey = (name, value) =>
        {
            seen.Add((name, value));
            return "overridden-key";
        };
        try
        {
            var api = new AzureAIModelApi("test-model", Fixtures.BaseUrl);
            Assert.Equal([(AzureAIModelApi.AzureAIApiKeyVar, "source-key")], seen);
            Assert.Equal("overridden-key", api.ApiKey);
        }
        finally
        {
            ModelApiHooks.OverrideApiKey = null;
        }
    }

    [Fact]
    public void registered_hook_can_supply_a_key_when_none_exists()
    {
        using var env = EnvScope.Clean();
        ModelApiHooks.HasApiKeyOverride = true;
        ModelApiHooks.OverrideApiKey = (name, value) => name == AzureAIModelApi.AzureAIApiKeyVar && value == "" ? "vault-key" : null;
        try
        {
            var api = new AzureAIModelApi("test-model", Fixtures.BaseUrl);
            Assert.Equal("vault-key", api.ApiKey);
            Assert.Null(api.TokenProvider);
            Assert.Equal("None:test-model", api.ConnectionKey());
        }
        finally
        {
            ModelApiHooks.OverrideApiKey = null;
            ModelApiHooks.HasApiKeyOverride = false;
        }
    }

    [Fact]
    public void api_key_env_precedence_azure_over_azureai()
    {
        using var env = EnvScope.Clean()
            .Set(AzureAIModelApi.AzureApiKeyVar, "a")
            .Set(AzureAIModelApi.AzureAIApiKeyVar, "b");
        Assert.Equal("a", new AzureAIModelApi("m", Fixtures.BaseUrl).ApiKey);

        env.Set(AzureAIModelApi.AzureApiKeyVar, null);
        Assert.Equal("b", new AzureAIModelApi("m", Fixtures.BaseUrl).ApiKey);
    }

    [Fact]
    public void missing_base_url_error()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIApiKeyVar, "k");
        var ex = Assert.Throws<PrerequisiteError>(() => new AzureAIModelApi("m"));
        Assert.Equal(
            "ERROR: Unable to initialise AzureAI client\n\nNo [bold][blue]AZUREAI_BASE_URL[/blue][/bold] defined in the environment.",
            ex.Message);
    }

    [Fact]
    public void base_url_env_precedence_and_inspect_fallback()
    {
        using var env = EnvScope.Clean()
            .Set(AzureAIModelApi.AzureAIApiKeyVar, "k")
            .Set(AzureAIModelApi.AzureEndpointUrlVar, "https://one/")
            .Set(AzureAIModelApi.AzureAIEndpointUrlVar, "https://two")
            .Set(AzureAIModelApi.AzureAIBaseUrlVar, "https://three/models/")
            .Set("INSPECT_EVAL_MODEL_BASE_URL", "https://four");

        Assert.Equal("https://explicit", new AzureAIModelApi("m", "https://explicit").EndpointUrl);
        Assert.Equal("https://one/", new AzureAIModelApi("m").EndpointUrl);
        env.Set(AzureAIModelApi.AzureEndpointUrlVar, null);
        Assert.Equal("https://two", new AzureAIModelApi("m").EndpointUrl);
        env.Set(AzureAIModelApi.AzureAIEndpointUrlVar, null);
        Assert.Equal("https://three/models/", new AzureAIModelApi("m").EndpointUrl);
        env.Set(AzureAIModelApi.AzureAIBaseUrlVar, null);
        Assert.Equal("https://four", new AzureAIModelApi("m").EndpointUrl);
    }

    [Fact]
    public void environment_prerequisite_error_formats_lists_like_python()
    {
        Assert.Equal(
            "ERROR: Unable to initialise X client\n\nNo [bold][blue]A[/blue][/bold] or [bold][blue]B[/blue][/bold] defined in the environment.",
            ProviderUtil.EnvironmentPrerequisiteError("X", ["A", "B"]).Message);
        Assert.Equal(
            "ERROR: Unable to initialise AzureAI client\n\nNo [bold][blue]AZURE_API_KEY[/blue][/bold], [bold][blue]AZUREAI_API_KEY[/blue][/bold], or [bold][blue]or managed identity (Entra ID)[/blue][/bold] defined in the environment.",
            ProviderUtil.EnvironmentPrerequisiteError("AzureAI", ["AZURE_API_KEY", "AZUREAI_API_KEY", "or managed identity (Entra ID)"]).Message);
    }

    [Fact]
    public void managed_identity_is_used_when_no_api_key()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIAudienceVar, "https://custom.audience/.default");
        var credential = new FakeTokenCredential("entra-token");
        var api = new AzureAIModelApi("m", Fixtures.BaseUrl, settings: new AzureAIClientSettings { TokenCredential = credential });

        Assert.Null(api.ApiKey);
        Assert.NotNull(api.TokenProvider);
        Assert.Equal("https://custom.audience/.default", AzureAIModelApi.TokenAudience);
        Assert.Equal("https://cognitiveservices.azure.com/.default", AzureHosting.DefaultAzureAudience);
    }

    [Fact]
    public void model_args_pop_emulate_tools_and_forward_the_rest()
    {
        var api = Fixtures.Api("gpt-4o", modelArgs: new Dictionary<string, object?> { ["emulate_tools"] = false, ["azure"] = true });
        Assert.False(api.EmulateTools);
        Assert.Equal(["azure"], api.ModelArgs.Keys);

        Assert.True(Fixtures.Api("gpt-4o", modelArgs: new Dictionary<string, object?> { ["emulate_tools"] = "false" }).EmulateTools);
        Assert.Null(Fixtures.Api("gpt-4o").EmulateTools);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData("auto", null)]
    [InlineData(" True ", true)]
    [InlineData("false", false)]
    public void normalize_stream_arg(object? value, bool? expected) =>
        Assert.Equal(expected, ProviderUtil.NormalizeStreamArg(value, "streaming"));

    [Fact]
    public void normalize_stream_arg_rejects_typos()
    {
        var ex = Assert.Throws<ArgumentException>(() => Fixtures.Api(streaming: "always"));
        Assert.Contains("streaming", ex.Message);
        Assert.Equal("Unrecognized value for the streaming model arg: 'always' (expected true, false, or \"auto\")", ex.Message);
    }
}

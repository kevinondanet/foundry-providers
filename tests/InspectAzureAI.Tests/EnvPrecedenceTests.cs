using Azure.Identity;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Tests;

/// <summary>Constructor env-var resolution (tests/model/providers/test_azureai.py plus suggested cases).</summary>
public class EnvPrecedenceTests
{
    [Fact]
    public void missing_base_url_error()
    {
        using var env = EnvScope.Clean();
        var ex = Assert.Throws<PrerequisiteError>(() => new AzureAIModelApi("m", settings: Fixtures.Entra()));
        Assert.Equal(
            "ERROR: Unable to initialise AzureAI client\n\nNo [bold][blue]AZUREAI_BASE_URL[/blue][/bold] defined in the environment.",
            ex.Message);
    }

    [Fact]
    public void base_url_env_precedence_and_inspect_fallback()
    {
        using var env = EnvScope.Clean()
            .Set(AzureAIModelApi.AzureEndpointUrlVar, "https://one/")
            .Set(AzureAIModelApi.AzureAIEndpointUrlVar, "https://two")
            .Set(AzureAIModelApi.AzureAIBaseUrlVar, "https://three/models/")
            .Set("INSPECT_EVAL_MODEL_BASE_URL", "https://four");

        Assert.Equal("https://explicit", new AzureAIModelApi("m", "https://explicit", settings: Fixtures.Entra()).EndpointUrl);
        Assert.Equal("https://one/", new AzureAIModelApi("m", settings: Fixtures.Entra()).EndpointUrl);
        env.Set(AzureAIModelApi.AzureEndpointUrlVar, null);
        Assert.Equal("https://two", new AzureAIModelApi("m", settings: Fixtures.Entra()).EndpointUrl);
        env.Set(AzureAIModelApi.AzureAIEndpointUrlVar, null);
        Assert.Equal("https://three/models/", new AzureAIModelApi("m", settings: Fixtures.Entra()).EndpointUrl);
        env.Set(AzureAIModelApi.AzureAIBaseUrlVar, null);
        Assert.Equal("https://four", new AzureAIModelApi("m", settings: Fixtures.Entra()).EndpointUrl);
    }

    [Fact]
    public void environment_prerequisite_error_formats_lists_like_python()
    {
        Assert.Equal(
            "ERROR: Unable to initialise X client\n\nNo [bold][blue]A[/blue][/bold] or [bold][blue]B[/blue][/bold] defined in the environment.",
            ProviderUtil.EnvironmentPrerequisiteError("X", ["A", "B"]).Message);
        Assert.Equal(
            "ERROR: Unable to initialise X client\n\nNo [bold][blue]A[/blue][/bold], [bold][blue]B[/blue][/bold], or [bold][blue]C[/blue][/bold] defined in the environment.",
            ProviderUtil.EnvironmentPrerequisiteError("X", ["A", "B", "C"]).Message);
    }

    [Fact]
    public void entra_credential_is_always_used_and_pinned_to_the_audience()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIAudienceVar, "https://custom.audience/.default");
        var credential = new FakeTokenCredential("entra-token");
        var api = new AzureAIModelApi("m", Fixtures.BaseUrl, settings: new AzureAIClientSettings { TokenCredential = credential });

        Assert.NotNull(api.Credential);
        Assert.Same(credential, api.Credential.Inner);
        Assert.Equal("https://custom.audience/.default", api.Credential.Scope);
        Assert.Equal("https://custom.audience/.default", AzureAIModelApi.TokenAudience);
        Assert.Equal("https://cognitiveservices.azure.com/.default", AzureHosting.DefaultAzureAudience);
    }

    [Fact]
    public void default_azure_credential_is_used_when_the_host_supplies_none()
    {
        using var env = EnvScope.Clean();
        var api = new AzureAIModelApi("m", Fixtures.BaseUrl);

        Assert.IsType<DefaultAzureCredential>(api.Credential.Inner);
        Assert.Equal(AzureHosting.DefaultAzureAudience, api.Credential.Scope);
    }

    [Fact]
    public void model_args_pop_max_completion_tokens_and_forward_the_rest()
    {
        var api = Fixtures.Api("gpt-4o", modelArgs: new Dictionary<string, object?> { ["max_completion_tokens"] = true, ["azure"] = true });
        Assert.True(api.ForceMaxCompletionTokens);
        Assert.Equal(["azure"], api.ModelArgs.Keys);

        var plain = Fixtures.Api("gpt-4o");
        Assert.False(plain.ForceMaxCompletionTokens);
        Assert.Empty(plain.ModelArgs);
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

    [Fact]
    public void strip_rich_markup_for_console_output()
    {
        var message = ProviderUtil.EnvironmentPrerequisiteError("X", ["A", "B"]).Message;
        Assert.Equal("ERROR: Unable to initialise X client\n\nNo A or B defined in the environment.", ProviderUtil.StripRichMarkup(message));
        Assert.Equal("keep [0], [Fact] and [] as they are", ProviderUtil.StripRichMarkup("keep [0], [Fact] and [] as they are"));
    }
}

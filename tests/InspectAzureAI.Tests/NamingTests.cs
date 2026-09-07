namespace InspectAzureAI.Tests;

/// <summary>Port of <c>TestAzureAICanonicalName</c> (tests/model/test_canonical_names.py).</summary>
public class NamingTests
{
    [Fact]
    public void test_openai_model() => Assert.Equal("openai/gpt-4o", Fixtures.Api("gpt-4o").CanonicalName());

    [Fact]
    public void test_openai_o_series() => Assert.Equal("openai/o1-preview", Fixtures.Api("o1-preview").CanonicalName());

    [Fact]
    public void test_mistral_model() => Assert.Equal("mistral/Mistral-large-2411", Fixtures.Api("Mistral-large-2411").CanonicalName());

    [Fact]
    public void test_unknown_model() => Assert.Equal("some-unknown-model", Fixtures.Api("some-unknown-model").CanonicalName());

    [Fact]
    public void test_explicit_org_prefix()
    {
        var api = Fixtures.Api("moonshotai/kimi-k2.5");
        Assert.Equal("moonshotai", api.OrgPrefix);
        Assert.Equal("moonshotai/kimi-k2.5", api.ModelName);
        Assert.Equal("moonshotai/kimi-k2.5", api.CanonicalName());
    }

    [Fact]
    public void test_explicit_org_overrides_auto_detection()
    {
        var api = Fixtures.Api("my-custom-org/gpt-4o");
        Assert.Equal("my-custom-org", api.OrgPrefix);
        Assert.Equal("my-custom-org/gpt-4o", api.ModelName);
        Assert.Equal("my-custom-org/gpt-4o", api.CanonicalName());
    }

    [Fact]
    public void test_no_org_prefix_uses_auto_detection()
    {
        var api = Fixtures.Api("gpt-4o");
        Assert.Null(api.OrgPrefix);
        Assert.Equal("gpt-4o", api.ModelName);
        Assert.Equal("openai/gpt-4o", api.CanonicalName());
    }

    [Fact]
    public void test_service_model_name_strips_org_prefix() =>
        Assert.Equal("kimi-k2.5", Fixtures.Api("moonshotai/kimi-k2.5").ServiceModelName());

    [Fact]
    public void test_service_model_name_unchanged_without_prefix() =>
        Assert.Equal("gpt-4o", Fixtures.Api("gpt-4o").ServiceModelName());

    [Fact]
    public void test_detection_uses_service_model_name()
    {
        var api = Fixtures.Api("custom-org/Mistral-large-2411");
        Assert.True(api.IsMistral());
        Assert.Equal("Mistral-large-2411", api.ServiceModelName());
        Assert.Equal("custom-org/Mistral-large-2411", api.CanonicalName());
    }

    [Theory]
    [InlineData("some-unknown-model", 2048)]
    [InlineData("Mistral-large-2411", null)]
    [InlineData("gpt-4o", 2048)]
    public void max_tokens_defaults(string model, int? expected) => Assert.Equal(expected, Fixtures.Api(model).MaxTokens());

    [Fact]
    public void connection_key_is_the_full_model_name()
    {
        Assert.Equal("moonshotai/kimi-k2.5", Fixtures.Api("moonshotai/kimi-k2.5").ConnectionKey());
        Assert.Equal("gpt-4o", Fixtures.Api("gpt-4o").ConnectionKey());
    }

    [Fact]
    public void other_hooks_keep_base_defaults()
    {
        var api = Fixtures.Api();
        Assert.True(api.CollapseUserMessages());
        Assert.Equal(10, api.MaxConnections());
    }
}

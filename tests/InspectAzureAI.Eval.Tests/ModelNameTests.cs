using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary><c>ModelName</c> (<c>model/_model.py</c>): api/name parsing and the pattern matching tasks use to condition on models.</summary>
public class ModelNameTests
{
    [Fact]
    public void parses_api_and_name_from_a_string()
    {
        var name = new ModelName("openai/gpt-4");

        Assert.Equal("openai", name.Api);
        Assert.Equal("gpt-4", name.Name);
        Assert.Equal("openai/gpt-4", name.ToString());
    }

    [Fact]
    public void only_the_first_slash_separates_the_api()
    {
        var name = new ModelName("azureai/openai/gpt-4");

        Assert.Equal("azureai", name.Api);
        Assert.Equal("openai/gpt-4", name.Name);
    }

    [Fact]
    public void a_string_without_an_api_is_an_error()
    {
        var ex = Assert.Throws<ArgumentException>(() => new ModelName("gpt-4"));

        Assert.Equal("API not specified for model name", ex.Message);
    }

    [Fact]
    public void reads_the_provider_and_name_of_a_model()
    {
        var name = new ModelName(new Model(new ScriptedModelApi([], "gpt-5.4-mini")));

        Assert.Equal("scripted", name.Api);
        Assert.Equal("gpt-5.4-mini", name.Name);
        Assert.Equal("scripted/gpt-5.4-mini", name.ToString());
    }

    [Fact]
    public void provider_name_follows_the_api_type()
    {
        var scripted = new ScriptedModelApi([], "m");

        Assert.Equal("scripted", ModelName.ProviderName(scripted));
        Assert.Equal("scripted", ModelName.ProviderName(new FallbackModelApi(scripted, [new VllmModelApi()])));
        Assert.Equal("vllm", ModelName.ProviderName(new VllmModelApi()));
        Assert.Equal("vllm", new ModelName(new Model(new VllmModelApi())).Api);
        using var responses = new InspectAzureAI.Provider.OpenAI.OpenAIResponsesModelApi(
            "gpt-5.6-sol", "https://example.com/models", settings: new InspectAzureAI.Provider.AzureAIClientSettings { TokenCredential = new FakeTokenCredential("t") });
        Assert.Equal("openai", ModelName.ProviderName(responses));
        Assert.Equal("openai/azure/gpt-5.6-sol", new ModelName(new Model(responses)).ToString());
    }

    [Theory]
    [InlineData("openai/gpt-4", true)]
    [InlineData("gpt-4", true)]
    [InlineData("gpt", true)]
    [InlineData("openai/gpt", true)]
    [InlineData("open/gpt", true)]
    [InlineData("claude", false)]
    [InlineData("openai/claude", false)]
    [InlineData("anthropic/gpt-4", true)]
    [InlineData("GPT-4", false)]
    public void matches_full_partial_and_substring_specifications(string pattern, bool expected)
    {
        // Python: (api in self.api and name in self.name) or name in self.name — so a wrong api still matches by name
        var name = new ModelName("openai/gpt-4");

        Assert.Equal(expected, name.Matches(pattern));
        Assert.Equal(expected, name == pattern);
        Assert.Equal(expected, pattern == name);
        Assert.Equal(!expected, name != pattern);
        Assert.Equal(expected, name.Equals(pattern));
    }

    [Fact]
    public void equality_with_null_and_other_model_names()
    {
        var name = new ModelName("openai/gpt-4");
        ModelName? none = null;

        Assert.False(name == null);
        Assert.True(name != null);
        Assert.True(none == null);
        Assert.Equal(new ModelName("openai", "gpt-4"), name);
        Assert.NotEqual(new ModelName("anthropic/gpt-4"), name);
        Assert.Equal(new ModelName("openai/gpt-4").GetHashCode(), name.GetHashCode());
        Assert.False(name.Equals(42));
    }

    private sealed class VllmModelApi : IModelApi
    {
        public string ModelName => "m";

        public int? MaxTokens() => null;

        public Task<GenerateResult> GenerateAsync(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, ToolChoice toolChoice, GenerateConfig config, StreamHandler? onStream, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Model.Cost;
using InspectAzureAI.Eval.Runner.EvalSet;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.OpenAI;

namespace InspectAzureAI.Eval.Tests;

public class ProviderIdentityCharacterizationTests
{
    [Fact]
    public void foundry_names_endpoints_cache_and_cost_identity_are_stable()
    {
        using var responses = new OpenAIResponsesModelApi("gpt-5.6-sol", "https://baseline/models");
        using var messages = new AnthropicFoundryModelApi("claude-sonnet-4-6", "https://baseline/models");
        Assert.Equal("gpt-5.6-sol", responses.ModelName);
        Assert.Equal("claude-sonnet-4-6", messages.ModelName);
        Assert.Equal("openai/gpt-5.6-sol", new ModelName(new Model.Model(responses)).ToString());
        Assert.Equal(3, TaskIdentifier.Version);
        CacheEntry Entry(string endpoint) => new(endpoint, new GenerateConfig(), [new ChatMessageUser("hello")], responses.ModelName, CachePolicy.Default, ToolChoice.Auto, []);
        Assert.Equal(Entry(responses.BaseUrl).Key, Entry(responses.BaseUrl).Key);
        Assert.NotEqual(Entry(responses.BaseUrl).Key, Entry("https://other/openai/v1").Key);
        Assert.Equal("gpt-5.6-sol", Entry(responses.BaseUrl).Model);
        Assert.Equal(ModelInfoLookup.GetModelInfo("openai/gpt-4o"), ModelInfoLookup.GetModelInfo("openai/azure/gpt-4o"));
    }
}

using InspectAzureAI.Cli.Models;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.OpenAI;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace InspectAzureAI.Cli.Tests;

/// <summary>Baseline before the independently reversible direct-provider prefix migration.</summary>
public class ProviderRoutingCharacterizationTests
{
    [Fact]
    public void direct_credentials_do_not_change_legacy_routing()
    {
        var variables = new Dictionary<string, string?>
        {
            ["OPENAI_API_KEY"] = "test-openai", ["ANTHROPIC_API_KEY"] = "test-anthropic",
            ["OPENAI_BASE_URL"] = "https://api.openai.com/v1", ["ANTHROPIC_BASE_URL"] = "https://api.anthropic.com",
            ["AZUREAI_BASE_URL"] = "https://baseline.services.ai.azure.com/models",
        };
        var saved = variables.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var pair in variables) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            existing_prefixes_resolve_to_foundry("openai/gpt-5.6-sol", "gpt-5.6-sol", "responses");
            existing_prefixes_resolve_to_foundry("anthropic/claude-sonnet-4-6", "claude-sonnet-4-6", "anthropic");
        }
        finally { foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
    }

    [Theory]
    [InlineData("openai/gpt-5.6-sol", "gpt-5.6-sol", "responses")]
    [InlineData("openai/azure/gpt-5.6-sol", "gpt-5.6-sol", "responses")]
    [InlineData("anthropic/claude-sonnet-4-6", "claude-sonnet-4-6", "anthropic")]
    [InlineData("anthropic/azure/claude-sonnet-4-6", "claude-sonnet-4-6", "anthropic")]
    [InlineData("gpt-5.6-sol", "gpt-5.6-sol", "responses")]
    [InlineData("gpt-5.4-mini", "gpt-5.4-mini", "models")]
    public void existing_prefixes_resolve_to_foundry(string name, string deployment, string route)
    {
        var api = ModelProviders.Resolve(name, baseUrl: "https://baseline.services.ai.azure.com/models").Api;
        try
        {
            switch (route)
            {
                case "responses":
                    var responses = Assert.IsType<OpenAIResponsesModelApi>(api);
                    Assert.Equal(deployment, responses.DeploymentName);
                    Assert.Equal("https://baseline.services.ai.azure.com/openai/v1/responses", responses.ResponsesUrl);
                    break;
                case "anthropic":
                    var messages = Assert.IsType<AnthropicFoundryModelApi>(api);
                    Assert.Equal(deployment, messages.DeploymentName);
                    Assert.Equal("https://baseline.services.ai.azure.com/anthropic/v1/messages", messages.MessagesUrl);
                    break;
                default:
                    Assert.Equal(deployment, Assert.IsType<AzureAIModelApi>(api).ModelName);
                    break;
            }
        }
        finally { (api as IDisposable)?.Dispose(); }
    }
}

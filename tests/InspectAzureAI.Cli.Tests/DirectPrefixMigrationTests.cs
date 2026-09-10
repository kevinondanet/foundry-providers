using InspectAzureAI.Cli.Models;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.OpenAI;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Cli.Tests;

public class DirectPrefixMigrationTests
{
    [Theory]
    [InlineData("openai", "gpt-5.6-sol", "https://api.openai.com/v1")]
    [InlineData("anthropic", "claude-opus-5", "https://api.anthropic.com")]
    public void migration_requires_explicit_destination_even_when_both_credentials_exist(string provider, string model, string endpoint)
    {
        var names = new[] { "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "OPENAI_BASE_URL", "ANTHROPIC_BASE_URL", "AZUREAI_BASE_URL" };
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-openai");
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "test-claude");
            Environment.SetEnvironmentVariable("OPENAI_BASE_URL", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", null);
            Environment.SetEnvironmentVariable("AZUREAI_BASE_URL", "https://resource.services.ai.azure.com/models");
            var error = Assert.Throws<PrerequisiteError>(() => ModelProviders.Resolve(provider + "/" + model));
            Assert.Contains(provider + "/azure/", error.Message);
            Assert.Contains("explicitly configure", error.Message);
            var selected = ModelProviders.Resolve(provider + "/" + model, baseUrl: endpoint);
            using var api = Assert.IsAssignableFrom<DirectModelApi>(selected.Api);
            Assert.Equal(provider + "/" + model, selected.Name);
            Assert.Equal(endpoint, api.BaseUrl);
            Assert.Equal(model, api.ModelName);
            Environment.SetEnvironmentVariable(provider.ToUpperInvariant() + "_BASE_URL", endpoint);
            using var envApi = Assert.IsAssignableFrom<DirectModelApi>(ModelProviders.Resolve(provider + "/" + model).Api);
            Assert.Equal(endpoint, envApi.BaseUrl);
            Environment.SetEnvironmentVariable(provider.ToUpperInvariant() + "_API_KEY", null);
            var missing = Assert.Throws<PrerequisiteError>(() => ModelProviders.Resolve(provider + "/" + model));
            Assert.Contains(provider + "/azure/", missing.Message);
            Assert.Throws<PrerequisiteError>(() => ModelProviders.Resolve(provider + "/" + model, baseUrl: "https://resource.openai.azure.com"));
            var foundryApi = ModelProviders.Resolve(provider + "/azure/" + model, baseUrl: "https://resource.services.ai.azure.com/models").Api;
            using var foundry = Assert.IsAssignableFrom<IDisposable>(foundryApi);
            Assert.Equal(model, foundryApi.ModelName);
            Assert.Equal(provider + "/azure/" + model, foundryApi.QualifiedModelName);
        }
        finally { foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
    }
    [Fact]
    public void model_arg_base_url_is_an_explicit_destination_and_constructor_args_are_consumed()
    {
        var prior = Environment.GetEnvironmentVariable("AZUREAI_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("AZUREAI_BASE_URL", "https://resource.services.ai.azure.com/models");
            using var api = Assert.IsType<OpenAIModelApi>(ModelProviders.Resolve("openai/gpt-5.6-sol", modelArgs: new Dictionary<string, object?> { ["api_key"] = "test", ["base_url"] = "https://direct.invalid/v1", ["responses_api"] = true }).Api);
            var body = api.BuildRequest([], [], ToolChoice.Auto, new(), false);
            Assert.Equal("https://direct.invalid/v1", api.BaseUrl);
            Assert.DoesNotContain("api_key", body.ToJsonString());
            Assert.DoesNotContain("responses_api", body.ToJsonString());
        }
        finally { Environment.SetEnvironmentVariable("AZUREAI_BASE_URL", prior); }
    }
}

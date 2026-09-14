using System.Text.Json.Nodes;
using Azure.Core;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.OpenAI;
using InspectAzureAI.Swe.CodexCli;

namespace InspectAzureAI.Swe.Tests;

/// <summary>
/// The served-model adapter over <see cref="CodexCliModelCatalog.ResolveSlug"/>: which APIs count as OpenAI and as
/// latest (port of the adapter cases of <c>tests/test_codex_model_catalog.py</c>, deviation D-X9).
/// </summary>
public class CodexCliModelAlignmentTests
{
    private static readonly JsonObject Catalog = JsonNode.Parse("""
        {"models": [
          {"slug": "gpt-5.4", "priority": 10, "supports_search_tool": true, "apply_patch_tool_type": "freeform"},
          {"slug": "gpt-5.2", "priority": 20, "supports_search_tool": true},
          {"slug": "gpt-6-astra", "priority": 1, "supports_search_tool": true, "apply_patch_tool_type": "freeform"},
          {"slug": "gpt-5.4-mini", "priority": 23, "supports_search_tool": true}
        ]}
        """)!.AsObject();

    private sealed class StaticCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => new("token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => new(GetToken(requestContext, cancellationToken));
    }

    private static OpenAIModelApi OpenAI(string name) => new(name, baseUrl: "http://127.0.0.1:9/v1", apiKey: "test-key");

    private static OpenAIResponsesModelApi Responses(string name) => new(name, "https://baseline/models");

    private static AzureAIModelApi AzureAI(string name) => new(name, "https://example.invalid", settings: new AzureAIClientSettings { TokenCredential = new StaticCredential() });

    [Theory]
    [InlineData("gpt-5.4", "gpt-5.4")]
    [InlineData("gpt-5.2", CodexCliModelCatalog.GenericFallbackSlug)]
    [InlineData("gpt-5", "gpt-5")]
    [InlineData("otter", "gpt-6-astra")]
    public void openai_model_api_is_openai_and_latest_comes_from_the_provider(string name, string slug)
    {
        using var api = OpenAI(name);

        Assert.Equal(name == "otter", api.IsLatest);
        Assert.Equal(slug, CodexCliModelAlignment.Resolve(api, Catalog, null).Slug);
    }

    [Theory]
    [InlineData("gpt-5.4", "gpt-5.4")]
    [InlineData("gpt-5.2", CodexCliModelCatalog.GenericFallbackSlug)]
    [InlineData("zephyr", "gpt-6-astra")]
    public void responses_model_api_is_openai_and_latest_comes_from_the_name(string name, string slug)
    {
        using var api = Responses(name);

        var resolution = CodexCliModelAlignment.Resolve(api, Catalog, null);

        Assert.Equal(slug, resolution.Slug);
        Assert.StartsWith($"openai '{name}'", resolution.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gpt-5.4")]
    [InlineData("otter")]
    public void other_apis_pass_the_deployment_name_verbatim(string name)
    {
        var azure = AzureAI(name);
        var scripted = new ScriptedModelApi([], name);

        var azureResolution = CodexCliModelAlignment.Resolve(azure, Catalog, null);

        Assert.Equal(name, azureResolution.Slug);
        Assert.StartsWith("non-openai model", azureResolution.Reason, StringComparison.Ordinal);
        Assert.Equal(name, CodexCliModelAlignment.Resolve(scripted, Catalog, null).Slug);
    }

    [Fact]
    public void an_explicit_model_config_wins_for_every_api()
    {
        using var api = OpenAI("otter");

        var resolution = CodexCliModelAlignment.Resolve(api, Catalog, "gpt-5.5");

        Assert.Equal(new CodexModelResolution("gpt-5.5", "explicit model_config override 'gpt-5.5'"), resolution);
        Assert.Equal("gpt-5.5", CodexCliModelAlignment.Resolve(new ScriptedModelApi(), Catalog, "gpt-5.5").Slug);
    }

    [Fact]
    public void without_a_catalog_an_openai_name_is_kept()
    {
        using var api = OpenAI("otter");

        Assert.Equal("otter", CodexCliModelAlignment.Resolve(api, null, null).Slug);
    }
}

using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.OpenAI;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Util;
namespace InspectAzureAI.Tests;

public class ProviderContractTests
{
    [Fact]
    public void network_contracts_supply_endpoint_auth_and_identity()
    {
        IModelApi azure = Fixtures.Api("gpt-4o");
        using var responses = new OpenAIResponsesModelApi("gpt-5.6-sol", Fixtures.BaseUrl, settings: Fixtures.Entra());
        using var anthropic = new AnthropicFoundryModelApi("claude-opus-5", Fixtures.BaseUrl, settings: Fixtures.Entra());
        foreach (var api in new IModelApi[] { azure, responses, anthropic })
        {
            Assert.NotNull(api.BaseUrl);
            Assert.True(api.IsFoundry);
            Assert.Contains(api.BaseUrl!, api.ConnectionKey());
            Assert.True(api.IsAuthFailure(new Azure.RequestFailedException(401, "expired")));
        }
        Assert.True(((IModelApi)anthropic).CollapseUserMessages());
        Assert.True(((IModelApi)responses).ApplyRedactedReasoningTokensToInput());
    }
    [Fact]
    public void http_error_preserves_retry_headers_and_structured_error()
    {
        var error = new ProviderHttpException(429, new Dictionary<string,string> { ["Retry-After"] = "12" }, "{\"error\":{\"message\":\"slow down\",\"param\":\"stream\"}}");
        Assert.Equal(429, HttpRetryUtil.StatusCodeOf(error));
        Assert.Equal(12, HttpRetryUtil.ParseRetryAfterFromException(error));
        Assert.Equal("stream", error.Error!["param"]!.ToString());
        Assert.True(HttpRetryUtil.RetryDecisionFor(error).Retry);
    }
    [Fact]
    public void recorded_arguments_remove_credentials_without_mutating_inputs()
    {
        var input = new Dictionary<string, object?> { ["api_key"] = "secret", ["aws_secret"] = "secret", ["responses_api"] = true,
            ["default_headers"] = JsonNode.Parse("{\"Authorization\":\"secret\",\"x-api-key\":\"secret\",\"X-Request-Id\":\"keep\"}") };
        var result = System.Text.Json.JsonSerializer.Serialize(ModelArgumentSanitizer.ForLog(input));
        Assert.DoesNotContain("secret", result);
        Assert.Contains("keep", result);
        Assert.Equal("secret", input["api_key"]);
    }
}

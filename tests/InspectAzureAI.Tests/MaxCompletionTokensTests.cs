using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Testing;

namespace InspectAzureAI.Tests;

/// <summary>Port-only <c>max_completion_tokens=true</c> model arg (README fidelity note 18).</summary>
public class MaxCompletionTokensTests
{
    [Fact]
    public void boolean_model_arg_forces_max_completion_tokens_for_any_family()
    {
        using var env = EnvScope.Clean();
        var api = Fixtures.Api("MAI-Thinking-1", modelArgs: new Dictionary<string, object?> { ["max_completion_tokens"] = true });

        var parameters = api.CompletionParams(new GenerateConfig { MaxTokens = 100 });

        Assert.True(api.ForceMaxCompletionTokens);
        Assert.Equal(100, parameters["max_completion_tokens"]!.GetValue<int>());
        Assert.False(parameters.ContainsKey("max_tokens"));
        Assert.Empty(api.ModelArgs);                       // popped, not forwarded as a body field
    }

    [Fact]
    public void microsoft_family_sends_max_completion_tokens_without_the_arg()
    {
        using var env = EnvScope.Clean();
        var byName = Fixtures.Api("MAI-Thinking-1");
        var byFormat = Fixtures.Api("thinking-1", modelArgs: new Dictionary<string, object?> { ["model_format"] = "Microsoft" });

        foreach (var api in new[] { byName, byFormat })
        {
            var parameters = api.CompletionParams(new GenerateConfig { MaxTokens = 100 });

            Assert.False(api.ForceMaxCompletionTokens);
            Assert.True(api.SendsMaxCompletionTokens);
            Assert.Equal(100, parameters["max_completion_tokens"]!.GetValue<int>());
            Assert.False(parameters.ContainsKey("max_tokens"));
        }

        Assert.False(Fixtures.Api("gpt-4o").SendsMaxCompletionTokens);
        Assert.False(Fixtures.Api("DeepSeek-V4-Flash").SendsMaxCompletionTokens);
    }

    [Fact]
    public void false_model_arg_keeps_the_python_name_rule()
    {
        using var env = EnvScope.Clean();
        var api = Fixtures.Api("MAI-Thinking-1", modelArgs: new Dictionary<string, object?> { ["max_completion_tokens"] = false });

        Assert.False(api.ForceMaxCompletionTokens);
        Assert.True(api.CompletionParams(new GenerateConfig { MaxTokens = 100 }).ContainsKey("max_tokens"));
        Assert.True(Fixtures.Api("gpt-5.4-mini").CompletionParams(new GenerateConfig { MaxTokens = 5 }).ContainsKey("max_completion_tokens"));
    }

    [Fact]
    public async Task non_boolean_value_is_a_pass_through_body_field()
    {
        using var env = EnvScope.Clean();
        var transport = new CannedTransport { Responder = _ => CannedResponse.Json(200, Fixtures.Completion("ok")) };
        var api = Fixtures.Api("MAI-Thinking-1", modelArgs: new Dictionary<string, object?> { ["max_completion_tokens"] = 400 }, transport: transport);

        Assert.False(api.ForceMaxCompletionTokens);
        Assert.Equal(400, api.ModelArgs["max_completion_tokens"]);
        await api.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig());

        var body = transport.LastRequest!.BodyJson;
        Assert.Equal(400, body["max_completion_tokens"]!.GetValue<int>());
        Assert.False(body.ContainsKey("max_tokens"));
        Assert.Equal("pass-through", transport.LastRequest.Headers["extra-parameters"]);
    }
}

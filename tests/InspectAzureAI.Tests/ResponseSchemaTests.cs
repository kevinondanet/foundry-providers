using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Testing;
using InspectAzureAI.Provider.Tools;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Tests;

/// <summary>Structured output (<c>GenerateConfig.ResponseSchema</c>) on the wire for both Foundry routes.</summary>
public class ResponseSchemaTests
{
    private sealed record Answer(string City, int Population, List<string>? Districts = null);

    private static ResponseSchema Schema(bool? strict = true, string? description = null) =>
        new("answer", JsonSchemaGenerator.JsonSchemaOf<Answer>() with
        {
            Properties = new Dictionary<string, JsonSchema>
            {
                ["City"] = new() { Type = ["string"], Pattern = "^[A-Z]", MinLength = 1 },
                ["Population"] = new() { Type = ["integer"], Minimum = 0 },
            },
            Required = ["City", "Population"],
        })
        {
            Strict = strict,
            Description = description,
        };

    [Fact]
    public async Task azure_route_sends_response_format_json_schema_with_extended_fields_stripped()
    {
        var transport = new CannedTransport { Responder = _ => CannedResponse.Json(200, Fixtures.Completion("{\"City\":\"Oslo\",\"Population\":700000}")) };
        var api = Fixtures.Api("gpt-4o", transport: transport);
        var config = new GenerateConfig { MaxTokens = 50, ResponseSchema = Schema(description: "A city answer") };

        var result = await api.GenerateAsync([new ChatMessageUser("Largest city in Norway?")], [], ToolChoice.Auto, config);

        var expected = """{"type":"json_schema","json_schema":{"name":"answer","schema":{"type":"object","properties":{"City":{"type":"string"},"Population":{"type":"integer"}},"additionalProperties":false,"required":["City","Population"]},"description":"A city answer","strict":true}}""";
        Assert.Equal(expected, transport.LastRequest!.BodyJson["response_format"]!.ToJsonString());
        Assert.Equal(expected, result.Call.Request["response_format"]!.ToJsonString());
        Assert.Equal("pass-through", transport.LastRequest.Headers["extra-parameters"]);
        Assert.Equal("{\"City\":\"Oslo\",\"Population\":700000}", result.OutputOrThrow().Completion);
    }

    [Fact]
    public void azure_completion_params_omit_unset_description_and_strict()
    {
        var api = Fixtures.Api("gpt-4o");

        var parameters = api.CompletionParams(new GenerateConfig { ResponseSchema = Schema(strict: null) });

        var jsonSchema = parameters["response_format"]!["json_schema"]!.AsObject();
        Assert.Equal(["name", "schema"], jsonSchema.Select(p => p.Key));
        Assert.Null(api.CompletionParams(new GenerateConfig())["response_format"]);
    }

    [Fact]
    public void anthropic_route_sends_output_format_with_the_structured_outputs_beta()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, "https://res.services.ai.azure.com/models");
        var api = new AnthropicFoundryModelApi("claude-sonnet-4-6", settings: Fixtures.Entra());
        var config = new GenerateConfig { ResponseSchema = Schema() };

        var request = api.BuildRequest([new ChatMessageUser("hi")], [], ToolChoice.Auto, config, streaming: false);

        var expected = """{"type":"json_schema","schema":{"type":"object","properties":{"City":{"type":"string","additionalProperties":false},"Population":{"type":"integer","additionalProperties":false}},"additionalProperties":false,"required":["City","Population"]}}""";
        Assert.Equal(expected, request["output_format"]!.ToJsonString());
        Assert.Equal(ResponseFormat.AnthropicStructuredOutputsBeta, api.BetaHeader(config));
        Assert.Null(api.BetaHeader(new GenerateConfig()));
        Assert.Null(api.BuildRequest([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig(), streaming: false)["output_format"]);
    }

    [Fact]
    public async Task anthropic_route_combines_the_beta_with_the_anthropic_beta_model_arg_on_the_wire()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, "https://res.services.ai.azure.com/models");
        var handler = new FakeArmHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-6\",\"content\":[{\"type\":\"text\",\"text\":\"{}\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}",
                Encoding.UTF8, "application/json"),
        });
        var api = new AnthropicFoundryModelApi("claude-sonnet-4-6", settings: Fixtures.Entra(), handler: handler,
            modelArgs: new Dictionary<string, object?> { ["anthropic_beta"] = "interleaved-thinking-2025-05-14" });

        await api.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig { ResponseSchema = Schema() });

        var request = handler.Requests.Single();
        Assert.Equal("interleaved-thinking-2025-05-14,structured-outputs-2025-11-13", request.Headers.GetValues("anthropic-beta").Single());
    }

    [Fact]
    public void anthropic_route_ignores_fallback_models_with_a_one_time_warning()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, "https://res.services.ai.azure.com/models");
        ProviderLogger.Reset();
        var api = new AnthropicFoundryModelApi("claude-sonnet-4-6", settings: Fixtures.Entra());
        var config = new GenerateConfig { FallbackModels = ["claude-opus-4-8"] };

        var request = api.BuildRequest([new ChatMessageUser("hi")], [], ToolChoice.Auto, config, streaming: false);
        api.BuildRequest([new ChatMessageUser("hi")], [], ToolChoice.Auto, config, streaming: false);

        Assert.Null(request["fallbacks"]);
        Assert.Equal([AnthropicFoundryModelApi.FallbackModelsIgnoredWarning], ProviderLogger.Warnings);
    }

    [Fact]
    public void response_format_helpers_have_the_python_shapes()
    {
        var schema = new ResponseSchema("s", new JsonSchema { Type = ["object"], Properties = new Dictionary<string, JsonSchema> { ["a"] = new() { Type = ["string"], Examples = [JsonValue.Create("x")] } } });

        var chat = ResponseFormat.JsonSchemaResponseFormat(schema, JsonSchemaDump.JsonSchemaExtendedFields);
        var anthropic = ResponseFormat.AnthropicOutputFormat(schema);

        Assert.Equal("""{"type":"json_schema","json_schema":{"name":"s","schema":{"type":"object","properties":{"a":{"type":"string"}}}}}""", chat.ToJsonString());
        Assert.Equal("""{"type":"json_schema","schema":{"type":"object","properties":{"a":{"type":"string","additionalProperties":false}},"additionalProperties":false}}""", anthropic.ToJsonString());
        Assert.Throws<ArgumentException>(() => new ResponseSchema("", new JsonSchema()));
    }
}

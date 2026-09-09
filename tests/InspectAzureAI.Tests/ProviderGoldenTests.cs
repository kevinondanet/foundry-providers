using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.OpenAI;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Core;
namespace InspectAzureAI.Tests;

public class ProviderGoldenTests
{
    private static GenerateConfig Config(JsonNode node) => JsonSerializer.Deserialize<GenerateConfig>(node.ToJsonString(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;
    [Fact]
    public void anthropic_requests_match_python_golden_bodies_and_betas()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "anthropic-requests.json")))!;
        Assert.Equal(40, fixture["python_revision"]!.ToString().Length);
        foreach (var entry in fixture["cases"]!.AsArray())
        {
            using var api = new AnthropicModelApi(entry!["model"]!.ToString(), apiKey: "fixture-key");
            var config = Config(entry["config"]!);
            var body = api.BuildRequest([new ChatMessageSystem("Be brief."), new ChatMessageUser("hi")], [], ToolChoice.Auto, config, false);
            Assert.True(JsonNode.DeepEquals(entry["body"], body), $"{api.ModelName}\nExpected: {entry["body"]}\nActual: {body}");
            Assert.Equal(entry["headers"]?["anthropic-beta"]?.ToString() ?? "", api.BetaHeader(config));
        }
    }
    [Fact]
    public async Task openai_responses_match_python_golden_bodies_and_headers()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "openai-responses-requests.json")))!;
        foreach (var entry in fixture["cases"]!.AsArray())
        {
            var handler = new DirectTestHandler(_ => DirectTestHandler.Json(DirectOpenAITests.Reply));
            using var api = new OpenAIModelApi(entry!["model"]!.ToString(), apiKey: "fixture-key", streaming: false,
                modelArgs: entry["model_args"]!.Deserialize<Dictionary<string, object?>>(), settings: new() { Handler = handler });
            await api.GenerateAsync([new ChatMessageSystem("Be brief."), new ChatMessageUser("hi")], [], ToolChoice.Auto, Config(entry["config"]!));
            var expected = entry["body"]!.DeepClone().AsObject();
            if (!expected.ContainsKey("store")) expected["store"] = true; // Explicit equivalent of Python SDK default.
            Assert.True(JsonNode.DeepEquals(expected, handler.Calls[0].Body), $"{api.ModelName}\nExpected: {expected}\nActual: {handler.Calls[0].Body}");
            foreach (var header in entry["headers"]!.AsObject()) Assert.Equal(header.Value!.ToString(), handler.Calls[0].Headers[header.Key]);
        }
    }
}

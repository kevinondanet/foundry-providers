using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Anthropic;
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
}

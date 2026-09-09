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
            var (input, tools) = Inputs(entry);
            var body = api.BuildRequest(input, tools, ToolChoice.Auto, config, false);
            Assert.True(JsonNode.DeepEquals(entry["body"], body), $"{api.ModelName}\nExpected: {entry["body"]}\nActual: {body}");
            Assert.Equal(entry["headers"]?["anthropic-beta"]?.ToString() ?? "", api.BetaHeader(config));
        }
    }
    [Theory]
    [InlineData("openai-responses-requests.json")]
    [InlineData("openai-chat-requests.json")]
    public async Task openai_requests_match_python_golden_bodies_and_headers(string fixtureFile)
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureFile)))!;
        foreach (var entry in fixture["cases"]!.AsArray())
        {
            var handler = new DirectTestHandler(_ => DirectTestHandler.Json(DirectOpenAITests.Reply));
            using var api = new OpenAIModelApi(entry!["model"]!.ToString(), apiKey: "fixture-key", streaming: false,
                modelArgs: entry["model_args"]!.Deserialize<Dictionary<string, object?>>(), settings: new() { Handler = handler });
            var (input, tools) = Inputs(entry);
            await api.GenerateAsync(input, tools, ToolChoice.Auto, Config(entry["config"]!));
            var expected = entry["body"]!.DeepClone().AsObject();
            if (api.UsesResponses(Config(entry["config"]!)) && !expected.ContainsKey("store")) expected["store"] = true; // Explicit equivalent of Python SDK default.
            Assert.True(JsonNode.DeepEquals(expected, handler.Calls[0].Body), $"{api.ModelName}\nExpected: {expected}\nActual: {handler.Calls[0].Body}");
            foreach (var header in entry["headers"]!.AsObject()) Assert.Equal(header.Value!.ToString(), handler.Calls[0].Headers[header.Key]);
        }
    }
    private static (IReadOnlyList<ChatMessage> Input, IReadOnlyList<ToolInfo> Tools) Inputs(JsonNode entry) => entry["scenario"]?.ToString() == "tools_images" ?
        ([new ChatMessageSystem("Be brief."), new ChatMessageUser(MessageContent.FromItems([new ContentText("hi"), new ContentImage("data:image/png;base64,aGk=")])),
          new ChatMessageAssistant("Checking", [new ToolCall("call1", "f", new JsonObject { ["x"] = "v" })]), new ChatMessageTool("done", "call1", "f")],
         [new ToolInfo("f", "Test function") { Parameters = new ToolParams { Properties = new Dictionary<string, ToolParam> { ["x"] = ToolParam.Of("string") }, Required = ["x"] } }]) :
        ([new ChatMessageSystem("Be brief."), new ChatMessageUser("hi")], []);
    [PythonFact]
    public void committed_provider_fixtures_are_reproducible_from_python()
    {
        var directory = Path.Combine(Path.GetTempPath(), "provider-goldens-" + Guid.NewGuid().ToString("N"));
        try
        {
            PythonReference.GenerateProviderFixtures(directory);
            foreach (var file in Directory.GetFiles(directory, "*-requests.json"))
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(file)), JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", Path.GetFileName(file))))), Path.GetFileName(file));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

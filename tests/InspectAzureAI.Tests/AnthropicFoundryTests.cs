using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Azure;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Tests;

/// <summary>The Anthropic Messages companion for Claude deployments on Foundry, against canned responses.</summary>
public class AnthropicFoundryTests
{
    private const string Inference = "https://res.services.ai.azure.com/models";

    private static string MessageJson(string stopReason = "end_turn", string? extraBlocks = null, string usage = "\"input_tokens\":10,\"output_tokens\":5,\"cache_creation_input_tokens\":3,\"cache_read_input_tokens\":2") =>
        "{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-6\",\"content\":[{\"type\":\"text\",\"text\":\"hello from claude\"}"
        + (extraBlocks is null ? "" : "," + extraBlocks) + "],\"stop_reason\":\"" + stopReason + "\",\"stop_sequence\":null,\"usage\":{" + usage + "}}";

    private static (AnthropicFoundryModelApi Api, FakeArmHandler Handler, FakeTokenCredential Credential) Build(
        string body, HttpStatusCode status = HttpStatusCode.OK, string contentType = "application/json")
    {
        var handler = new FakeArmHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) });
        var credential = new FakeTokenCredential("entra-token");
        var api = new AnthropicFoundryModelApi("claude-sonnet-4-6",
            settings: new AzureAIClientSettings { TokenCredential = credential }, handler: handler);
        return (api, handler, credential);
    }

    [Fact]
    public async Task entra_request_has_bearer_only_the_version_header_and_the_messages_shape()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (api, handler, credential) = Build(MessageJson());
        Assert.Equal("https://res.services.ai.azure.com/anthropic/v1/messages", api.MessagesUrl);

        var input = new List<ChatMessage>
        {
            new ChatMessageSystem("Be terse."),
            new ChatMessageUser("Weather in Oslo?"),
            new ChatMessageAssistant("Let me check.", [new ToolCall("toolu_1", "get_weather", new JsonObject { ["city"] = "Oslo" })]),
            new ChatMessageTool("21C", "toolu_1", "get_weather"),
            new ChatMessageUser("Thanks, and in Bergen?"),
        };
        var result = await api.GenerateAsync(input, [Fixtures.WeatherTool], ToolChoice.Auto, new GenerateConfig { Temperature = 0.5 });

        Assert.Equal("hello from claude", result.OutputOrThrow().Completion);
        var request = handler.Requests.Single();
        Assert.Equal("Bearer entra-token", request.Headers.Authorization!.ToString());
        Assert.False(request.Headers.Contains("x-api-key"));
        Assert.Equal(AnthropicFoundryModelApi.AnthropicVersion, request.Headers.GetValues("anthropic-version").Single());
        Assert.Equal([AzureHosting.DefaultAzureAudience], Assert.Single(credential.Scopes));

        var body = result.Call.Request;
        Assert.Equal("claude-sonnet-4-6", body["model"]!.ToString());
        Assert.Equal(32000, body["max_tokens"]!.GetValue<int>());
        Assert.Equal("Be terse.", body["system"]!.ToString());
        Assert.Equal(0.5, body["temperature"]!.GetValue<double>());
        var messages = body["messages"]!.AsArray();
        Assert.Equal(["user", "assistant", "user"], messages.Select(m => m!["role"]!.ToString()));
        var assistantBlocks = messages[1]!["content"]!.AsArray();
        Assert.Equal("tool_use", assistantBlocks[1]!["type"]!.ToString());
        Assert.Equal("Oslo", assistantBlocks[1]!["input"]!["city"]!.ToString());
        var lastUser = messages[2]!["content"]!.AsArray();                       // tool_result and the next user text merged into one turn
        Assert.Equal("tool_result", lastUser[0]!["type"]!.ToString());
        Assert.Equal("toolu_1", lastUser[0]!["tool_use_id"]!.ToString());
        Assert.Equal("text", lastUser[1]!["type"]!.ToString());
        Assert.Equal("get_weather", body["tools"]![0]!["name"]!.ToString());
        Assert.NotNull(body["tools"]![0]!["input_schema"]!["properties"]);
        Assert.Equal("auto", body["tool_choice"]!["type"]!.ToString());
        Assert.False(body.ContainsKey("stream"));
    }

    [Fact]
    public void env_precedence_and_base_url_derivation()
    {
        using var env = EnvScope.Clean().Set("AZUREAI_ANTHROPIC_BASE_URL", "https://res.services.ai.azure.com/models");
        var api = new AnthropicFoundryModelApi("azure/claude-3-5-sonnet", settings: Fixtures.Entra());

        Assert.NotNull(api.Credential);
        Assert.Equal("claude-3-5-sonnet", api.DeploymentName);
        Assert.Equal("https://res.services.ai.azure.com/anthropic", api.BaseUrl);
        Assert.Equal(4096, api.MaxTokens());
        Assert.Equal(32000, new AnthropicFoundryModelApi("claude-3-7-sonnet", "https://x/anthropic", settings: Fixtures.Entra()).MaxTokens());
        Assert.Equal("https://x/anthropic", AnthropicFoundryModelApi.DeriveBaseUrl("https://x/anthropic/"));
        Assert.Equal("https://x/anthropic", AnthropicFoundryModelApi.DeriveBaseUrl("https://x"));
        Assert.Equal("https://x/anthropic", AnthropicFoundryModelApi.DeriveBaseUrl("https://x/models"));

        env.Set("AZUREAI_ANTHROPIC_BASE_URL", null);
        var missing = Assert.Throws<PrerequisiteError>(() => new AnthropicFoundryModelApi("claude-sonnet-4-6", settings: new AzureAIClientSettings { TokenCredential = new FakeTokenCredential("t") }));
        Assert.Equal(
            "ERROR: Unable to initialise Anthropic on Azure client\n\nNo [bold][blue]AZUREAI_ANTHROPIC_BASE_URL[/blue][/bold], [bold][blue]AZURE_ANTHROPIC_BASE_URL[/blue][/bold], [bold][blue]AZURE_ENDPOINT_URL[/blue][/bold], [bold][blue]AZUREAI_ENDPOINT_URL[/blue][/bold], or [bold][blue]AZUREAI_BASE_URL[/blue][/bold] defined in the environment.",
            missing.Message);
    }

    [Fact]
    public async Task tool_use_blocks_stop_reasons_and_usage_are_mapped()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (api, _, _) = Build(MessageJson("tool_use", "{\"type\":\"tool_use\",\"id\":\"toolu_9\",\"name\":\"get_weather\",\"input\":{\"city\":\"Oslo\"}}"));

        var output = (await api.GenerateAsync([new ChatMessageUser("hi")], [Fixtures.WeatherTool], ToolChoice.Auto, new GenerateConfig())).OutputOrThrow();

        Assert.Equal(StopReason.ToolCalls, output.StopReason);
        Assert.Equal("hello from claude", output.Completion);
        var call = Assert.Single(output.Message.ToolCalls!);
        Assert.Equal(("toolu_9", "get_weather", "Oslo"), (call.Id, call.Function, call.Arguments["city"]!.ToString()));
        Assert.Null(call.ParseError);
        Assert.Equal(new ModelUsage(10, 5, 20) { InputTokensCacheWrite = 3, InputTokensCacheRead = 2 }, output.Usage);

        var refused = Build(MessageJson("refusal", usage: "\"input_tokens\":1,\"output_tokens\":1")).Api;
        var refusedOutput = (await refused.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig())).OutputOrThrow();
        Assert.Equal(StopReason.ContentFilter, refusedOutput.StopReason);
        Assert.Equal("refusal", refusedOutput.Choices[0].StopDetails!.Type);
        Assert.Equal(new ModelUsage(1, 1, 2), refusedOutput.Usage);
    }

    [Fact]
    public async Task streaming_events_are_folded_and_delivered()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var sse = string.Join("\n\n",
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_s\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-6\",\"content\":[],\"usage\":{\"input_tokens\":7,\"output_tokens\":1}}}",
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello \"}}",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"world\"}}",
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}",
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_s\",\"name\":\"get_weather\",\"input\":{}}}",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"city\\\":\"}}",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\\\"Oslo\\\"}\"}}",
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}",
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":12}}",
            "event: message_stop\ndata: {\"type\":\"message_stop\"}") + "\n\n";
        var (api, handler, _) = Build(sse, contentType: "text/event-stream");
        var text = new StringBuilder();
        var toolDeltas = new List<string>();

        var result = await api.GenerateAsync([new ChatMessageUser("hi")], [Fixtures.WeatherTool], ToolChoice.Auto, new GenerateConfig(), e =>
        {
            if (e is StreamTextEvent t) text.Append(t.Text);
            if (e is StreamToolCallEvent c) toolDeltas.Add(c.Arguments);
            return Task.CompletedTask;
        });

        Assert.Contains("\"stream\":true", handler.Bodies.Single());
        Assert.Equal("Hello world", text.ToString());
        Assert.Equal("{\"city\":\"Oslo\"}", string.Concat(toolDeltas));
        var output = result.OutputOrThrow();
        Assert.Equal("Hello world", output.Completion);
        Assert.Equal(StopReason.ToolCalls, output.StopReason);
        Assert.Equal("Oslo", Assert.Single(output.Message.ToolCalls!).Arguments["city"]!.ToString());
        Assert.Equal(new ModelUsage(7, 12, 19), output.Usage);
    }

    [Fact]
    public async Task http_400_is_returned_and_429_is_thrown_with_retry_classification()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (bad, _, _) = Build("{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"max_tokens: must be positive\"}}", HttpStatusCode.BadRequest);
        var result = await bad.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig());
        Assert.Null(result.Output);
        Assert.Equal(400, Assert.IsType<RequestFailedException>(result.Error).Status);
        Assert.Equal("max_tokens: must be positive", result.Error!.Message);
        Assert.True(result.Call.Error);

        var (limited, _, _) = Build("{\"error\":{\"message\":\"slow down\"}}", HttpStatusCode.TooManyRequests);
        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => limited.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig()));
        Assert.Equal(429, ex.Status);
        Assert.Equal(RetryKind.RateLimit, limited.ShouldRetry(ex).Kind);
        Assert.False(limited.ShouldRetry(result.Error!).Retry);
        Assert.True(limited.IsAuthFailure(new RequestFailedException(401, "nope")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task tool_result_images_reach_claude_and_are_redacted_in_the_call(bool withCaption)
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (api, handler, _) = Build(MessageJson());
        var pixels = Convert.ToBase64String(new byte[200]);
        var content = new List<Content>();
        if (withCaption)
        {
            content.Add(new ContentText("Screenshot") { Citations = [AnthropicWebSearch.ToInspectCitation(JsonNode.Parse("""{"type":"web_search_result_location","url":"https://example.com","encrypted_index":"IDX"}""")!.AsObject())] });
        }

        content.Add(new ContentImage($"data:image/png;base64,{pixels}"));
        content.Add(new ContentImage("https://example.com/image.png"));
        var result = await api.GenerateAsync([
            new ChatMessageUser("Take a screenshot"),
            new ChatMessageAssistant("", [new ToolCall("tool1", "screenshot", new JsonObject())]),
            new ChatMessageTool(MessageContent.FromItems(content), "tool1", "screenshot"),
        ], [], ToolChoice.Auto, new GenerateConfig());

        var toolResult = JsonNode.Parse(handler.Bodies.Single()!)!["messages"]![2]!["content"]![0]!;
        Assert.Equal("tool1", toolResult["tool_use_id"]!.ToString());
        Assert.False(toolResult["is_error"]!.GetValue<bool>());
        var blocks = toolResult["content"]!.AsArray();
        Assert.Equal(withCaption ? 3 : 2, blocks.Count);
        if (withCaption)
        {
            Assert.Equal("Screenshot", blocks[0]!["text"]!.ToString());
            Assert.Null(blocks[0]!["citations"]);
        }

        var imageIndex = withCaption ? 1 : 0;
        Assert.Equal("image", blocks[imageIndex]!["type"]!.ToString());
        Assert.Equal("base64", blocks[imageIndex]!["source"]!["type"]!.ToString());
        Assert.Equal("image/png", blocks[imageIndex]!["source"]!["media_type"]!.ToString());
        Assert.Equal(pixels, blocks[imageIndex]!["source"]!["data"]!.ToString());
        Assert.Equal("https://example.com/image.png", blocks[imageIndex + 1]!["source"]!["url"]!.ToString());
        Assert.Equal(OpenAIUtil.Base64DataRemoved, result.Call.Request["messages"]![2]!["content"]![0]!["content"]![imageIndex]!["source"]!["data"]!.ToString());
    }

    [Fact]
    public async Task images_become_base64_sources_and_are_redacted_in_the_model_call()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (api, handler, _) = Build(MessageJson());
        var pixels = Convert.ToBase64String(new byte[200]);

        var result = await api.GenerateAsync([new ChatMessageUser(new Content[]
        {
            new ContentText("What is this?"),
            new ContentImage($"data:image/png;base64,{pixels}"),
            new ContentImage("https://example.com/a.png"),
        })], [], ToolChoice.Auto, new GenerateConfig());

        var sent = JsonNode.Parse(handler.Bodies.Single()!)!;
        var blocks = sent["messages"]![0]!["content"]!.AsArray();
        Assert.Equal("base64", blocks[1]!["source"]!["type"]!.ToString());
        Assert.Equal("image/png", blocks[1]!["source"]!["media_type"]!.ToString());
        Assert.Equal(pixels, blocks[1]!["source"]!["data"]!.ToString());
        Assert.Equal("url", blocks[2]!["source"]!["type"]!.ToString());
        Assert.Equal(OpenAIUtil.Base64DataRemoved, result.Call.Request["messages"]![0]!["content"]![1]!["source"]!["data"]!.ToString());
    }
}

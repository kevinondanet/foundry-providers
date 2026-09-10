using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Tests;

public class DirectAnthropicTests
{
    internal const string Reply = "{\"id\":\"msg_1\",\"type\":\"message\",\"model\":\"claude-opus-5\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"hello\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":4,\"output_tokens\":2}}";
    [Fact]
    public async Task direct_request_uses_api_key_and_reloads_override_each_attempt()
    {
        var key = "first";
        var handler = new DirectTestHandler(_ => DirectTestHandler.Json(Reply));
        using var api = new AnthropicModelApi("claude-opus-5", "https://api.anthropic.com", "unused", streaming: false,
            settings: new() { Handler = handler, ApiKeyOverride = (_, _) => key });
        var result = await api.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new());
        Assert.Equal("hello", result.OutputOrThrow().Completion);
        Assert.Equal("https://api.anthropic.com/v1/messages", handler.Calls[0].Url);
        Assert.Equal("first", handler.Calls[0].Headers["x-api-key"]);
        Assert.Equal("2023-06-01", handler.Calls[0].Headers["anthropic-version"]);
        Assert.DoesNotContain("Authorization", handler.Calls[0].Headers.Keys);
        key = "second";
        await api.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new());
        Assert.Equal("second", handler.Calls[1].Headers["x-api-key"]);
        Assert.DoesNotContain("unused", result.Call.Request.ToJsonString());
    }
    [Theory]
    [InlineData("claude-opus-5")]
    [InlineData("claude-sonnet-5")]
    [InlineData("claude-opus-4-7")]
    [InlineData("claude-opus-4-8")]
    public async Task adaptive_only_models_reject_budgets_before_http(string model)
    {
        var handler = new DirectTestHandler(_ => throw new Exception("must not send"));
        using var api = new AnthropicModelApi(model, baseUrl: "https://api.anthropic.com", apiKey: "test", settings: new() { Handler = handler });
        var ex = await Assert.ThrowsAsync<PrerequisiteError>(() => api.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new() { ReasoningTokens = 1024 }));
        Assert.Contains("reasoning_effort", ex.Message);
        Assert.Empty(handler.Calls);
    }
    [Theory]
    [InlineData("claude-3-5-sonnet-latest", "high", null, 4096)]
    [InlineData("claude-3-7-sonnet-latest", "high", "enabled", 48000)]
    [InlineData("claude-sonnet-4-5", "high", "enabled", 48000)]
    [InlineData("claude-sonnet-4-6", "high", "adaptive", 48000)]
    [InlineData("claude-opus-5", "max", "adaptive", 64000)]
    public void thinking_is_chosen_per_generation(string model, string effort, string? thinking, int tokens)
    {
        using var api = new AnthropicModelApi(model, baseUrl: "https://api.anthropic.com", apiKey: "test");
        var config = new GenerateConfig { ReasoningEffort = effort };
        var body = api.BuildRequest([new ChatMessageUser("hi")], [], ToolChoice.Auto, config, false);
        Assert.Equal(thinking, body["thinking"]?["type"]?.ToString());
        Assert.Equal(tokens, body["max_tokens"]!.GetValue<int>());
        if (thinking is not null) Assert.Equal("summarized", body["thinking"]!["display"]!.ToString());
        var none = api.BuildRequest([new ChatMessageUser("hi")], [], ToolChoice.Auto, new() { ReasoningEffort = "none" }, false);
        Assert.NotEqual("adaptive", none["thinking"]?["type"]?.ToString());
    }
    [Fact]
    public void betas_are_merged_per_request_and_full_thinking_removes_display()
    {
        using var api = new AnthropicModelApi("claude-opus-5", baseUrl: "https://api.anthropic.com", apiKey: "test", modelArgs: new Dictionary<string, object?> { ["betas"] = new[] { "custom", "custom" } });
        var config = new GenerateConfig { ReasoningEffort = "high", ExtraHeaders = new Dictionary<string,string> { ["Anthropic-Beta"] = "dev-full-thinking-2025-05-14,custom" } };
        Assert.Equal("custom,dev-full-thinking-2025-05-14,output-128k-2025-02-19,interleaved-thinking-2025-05-14", api.BetaHeader(config));
        Assert.Null(api.BuildRequest([new ChatMessageUser("hi")], [], ToolChoice.Auto, config, false)["thinking"]!["display"]);
        Assert.DoesNotContain("dev-full-thinking", api.BetaHeader(new()));
    }
    [Fact]
    public async Task signed_thinking_and_tool_blocks_replay_in_original_order()
    {
        var response = JsonNode.Parse(Reply)!.AsObject();
        response["content"] = JsonNode.Parse("[{\"type\":\"thinking\",\"thinking\":\"first\",\"signature\":\"sig1\"},{\"type\":\"tool_use\",\"id\":\"call1\",\"name\":\"f\",\"input\":{}},{\"type\":\"thinking\",\"thinking\":\"second\",\"signature\":\"sig2\"}]");
        response["stop_reason"] = "tool_use";
        var handler = new DirectTestHandler(_ => DirectTestHandler.Json(response.ToJsonString()));
        using var api = new AnthropicModelApi("claude-opus-5", baseUrl: "https://api.anthropic.com", apiKey: "test", streaming: false, settings: new() { Handler = handler });
        var result = await api.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new());
        var body = api.BuildRequest([new ChatMessageUser("hi"), result.OutputOrThrow().Choices[0].Message, new ChatMessageTool("ok", "call1", "f")], [], ToolChoice.Auto, new(), false);
        Assert.Equal(new[] { "thinking", "tool_use", "thinking" }, body["messages"]![1]!["content"]!.AsArray().Select(b => b!["type"]!.ToString()));
        Assert.Equal("sig2", body["messages"]![1]!["content"]![2]!["signature"]!.ToString());
    }
    [Fact]
    public async Task truncated_stream_is_retryable_and_error_events_retain_status()
    {
        var handler = new DirectTestHandler(_ => DirectTestHandler.Sse("data: {\"type\":\"message_start\",\"message\":{\"usage\":{}}}\n\n"));
        using var api = new AnthropicModelApi("claude-opus-5", baseUrl: "https://api.anthropic.com", apiKey: "test", streaming: true, settings: new() { Handler = handler });
        var error = await Assert.ThrowsAsync<ServiceResponseException>(() => api.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new()));
        Assert.True(api.ShouldRetry(error).Retry);
        var overloaded = new DirectTestHandler(_ => DirectTestHandler.Sse("data: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"busy\"}}\n\n"));
        using var busy = new AnthropicModelApi("claude-opus-5", baseUrl: "https://api.anthropic.com", apiKey: "test", settings: new() { Handler = overloaded });
        var http = await Assert.ThrowsAsync<ProviderHttpException>(() => busy.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new()));
        Assert.Equal(529, http.Status);
    }
    [Fact]
    public void unknown_options_and_credential_body_fields_fail_without_leaking_values()
    {
        var error = Assert.Throws<PrerequisiteError>(() => new AnthropicModelApi("claude-opus-5", baseUrl: "https://api.anthropic.com", apiKey: "test", modelArgs: new Dictionary<string,object?> { ["auth_token"] = "secret" }));
        Assert.DoesNotContain("secret", error.Message);
        using var api = new AnthropicModelApi("claude-opus-5", baseUrl: "https://api.anthropic.com", modelArgs: new Dictionary<string,object?> { ["api_key"] = "secret" });
        Assert.Empty(api.ModelArgsForLog);
        Assert.Throws<PrerequisiteError>(() => api.BuildRequest([], [], ToolChoice.Auto, new() { ExtraBody = new JsonObject { ["api_key"] = "secret" } }, false));
    }
}

internal sealed record DirectTestCall(string Url, string Method, IReadOnlyDictionary<string,string> Headers, JsonObject? Body);
internal sealed class DirectTestHandler(Func<DirectTestCall, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<DirectTestCall> Calls { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))?.AsObject();
        var call = new DirectTestCall(request.RequestUri!.ToString(), request.Method.Method, request.Headers.ToDictionary(p => p.Key, p => string.Join(',',p.Value), StringComparer.OrdinalIgnoreCase), body);
        Calls.Add(call);
        return respond(call);
    }
    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    public static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
}

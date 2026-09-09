using System.Net;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.OpenAI;
namespace InspectAzureAI.Tests;

public class DirectOpenAITests
{
    internal const string Reply = "{\"id\":\"resp_1\",\"status\":\"completed\",\"model\":\"gpt-5.6-sol\",\"output\":[{\"type\":\"message\",\"phase\":\"final_answer\",\"content\":[{\"type\":\"output_text\",\"text\":\"hello\"}]}],\"usage\":{\"input_tokens\":10,\"output_tokens\":2,\"total_tokens\":12,\"input_tokens_details\":{\"cached_tokens\":3}}}";
    [Theory]
    [InlineData("gpt-5.6-sol", null, true)]
    [InlineData("gpt-5.4-mini", null, true)]
    [InlineData("gpt-6", null, true)]
    [InlineData("gpt-4", null, false)]
    [InlineData("gpt-5.6-sol", 1, false)]
    [InlineData("o3", null, true)]
    [InlineData("codex", null, true)]
    public void selection_uses_direct_families_and_any_explicit_num_choices(string model, int? n, bool responses)
    {
        using var api = new OpenAIModelApi(model, apiKey: "test");
        Assert.Equal(responses, api.UsesResponses(new() { NumChoices = n }));
    }
    [Fact]
    public async Task responses_use_direct_endpoint_headers_and_preserve_phase_and_usage()
    {
        var handler = new DirectTestHandler(_ => DirectTestHandler.Json(Reply));
        using var api = new OpenAIModelApi("gpt-5.6-sol", "https://custom.invalid/v1", "secret", settings: new() { Handler = handler }, modelArgs: new Dictionary<string,object?> { ["organization"] = "org1", ["project"] = "proj1" });
        var result = await api.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new());
        Assert.Equal("https://custom.invalid/v1/responses", handler.Calls[0].Url);
        Assert.Equal("Bearer secret", handler.Calls[0].Headers["Authorization"]);
        Assert.Equal("org1", handler.Calls[0].Headers["OpenAI-Organization"]);
        Assert.Equal(7, result.OutputOrThrow().Usage!.InputTokens);
        var body = api.BuildRequest([result.OutputOrThrow().Message], [], ToolChoice.Auto, new(), false);
        Assert.Equal("final_answer", body["input"]![0]!["phase"]!.ToString());
        Assert.Null(body["reasoning"]);
        Assert.DoesNotContain("secret", result.Call.Request.ToJsonString());
    }
    [Fact]
    public async Task background_polls_transient_failures_without_resubmission()
    {
        var polls = 0;
        var handler = new DirectTestHandler(call => call.Method == "POST" ? DirectTestHandler.Json("{\"id\":\"resp_1\",\"status\":\"queued\"}") :
            ++polls == 1 ? DirectTestHandler.Json("{\"error\":{\"code\":\"server_error\"}}", HttpStatusCode.ServiceUnavailable) : DirectTestHandler.Json(Reply));
        using var api = new OpenAIModelApi("gpt-5.4-pro", apiKey: "test", settings: new() { Handler = handler, Delay = (_, _) => Task.CompletedTask });
        var result = await api.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new());
        Assert.Equal("hello", result.OutputOrThrow().Completion);
        Assert.Equal(new[] { "POST", "GET", "GET" }, handler.Calls.Select(c => c.Method));
        Assert.True(handler.Calls[0].Body!["background"]!.GetValue<bool>());
        Assert.Null(handler.Calls[0].Body!["stream"]);
        Assert.True(api.Background(new() { ReasoningMode = "pro" }));
    }
    [Fact]
    public async Task cancellation_cancels_existing_background_work()
    {
        using var cancel = new CancellationTokenSource();
        var handler = new DirectTestHandler(call => DirectTestHandler.Json(call.Url.EndsWith("/cancel", StringComparison.Ordinal) ? "{}" : "{\"id\":\"resp_1\",\"status\":\"in_progress\"}"));
        using var api = new OpenAIModelApi("gpt-5.4-pro", apiKey: "test", settings: new() { Handler = handler, Delay = (_, ct) => { cancel.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; } });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new(), cancel.Token));
        Assert.Equal("https://api.openai.com/v1/responses/resp_1/cancel", handler.Calls[1].Url);
    }
    [Theory]
    [InlineData(null, "stream", 2)]
    [InlineData(true, "stream", 1)]
    [InlineData(null, "temperature", 1)]
    public async Task only_automatic_stream_rejection_falls_back(bool? streaming, string parameter, int calls)
    {
        var handler = new DirectTestHandler(call => call.Body?["stream"]?.GetValue<bool>() == true ? DirectTestHandler.Json(new JsonObject { ["error"] = new JsonObject { ["param"] = parameter, ["message"] = "unsupported" } }.ToJsonString(), HttpStatusCode.BadRequest) : DirectTestHandler.Json(Reply));
        using var api = new OpenAIModelApi("gpt-5.6-sol", apiKey: "test", streaming: streaming, settings: new() { Handler = handler });
        var task = api.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new(), _ => Task.CompletedTask);
        if (calls == 1) await Assert.ThrowsAsync<ProviderHttpException>(() => task); else Assert.Equal("hello", (await task).OutputOrThrow().Completion);
        Assert.Equal(calls, handler.Calls.Count);
    }
    [Fact]
    public async Task failed_responses_preserve_classification_and_do_not_return_empty_success()
    {
        var handler = new DirectTestHandler(_ => DirectTestHandler.Json("{\"status\":\"failed\",\"error\":{\"code\":\"server_error\",\"message\":\"busy\"}}"));
        using var api = new OpenAIModelApi("gpt-5.6-sol", apiKey: "test", settings: new() { Handler = handler });
        var ex = await Assert.ThrowsAsync<ProviderHttpException>(() => api.GenerateAsync([], [], ToolChoice.Auto, new()));
        Assert.Equal(500, ex.Status);
        Assert.True(api.ShouldRetry(ex).Retry);
    }
}

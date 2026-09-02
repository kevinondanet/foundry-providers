using System.Text.Json.Nodes;
using Azure;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Testing;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Tests;

/// <summary>End-to-end generate() behaviour against a canned transport.</summary>
public class GenerateTests
{
    private static readonly GenerateConfig Config = new() { MaxTokens = 5, Temperature = 0 };

    private static async Task<(GenerateResult Result, CannedTransport Transport)> Generate(
        AzureAIModelApi api, CannedTransport transport, IReadOnlyList<ChatMessage>? input = null, IReadOnlyList<ToolInfo>? tools = null,
        ToolChoice? toolChoice = null, GenerateConfig? config = null)
    {
        var result = await api.GenerateAsync(
            input ?? [new ChatMessageUser("hi")], tools ?? [], toolChoice ?? ToolChoice.Auto, config ?? Config);
        return (result, transport);
    }

    private static CannedTransport Transport(string json) =>
        new() { Responder = _ => CannedResponse.Json(200, json) };

    [Fact]
    public async Task emulate_tools_auto_enables_for_llama_and_omits_native_tools()
    {
        var transport = Transport(Fixtures.Completion("<tool_call>{\"name\": \"get_weather\", \"arguments\": {\"city\": \"Paris\"}}</tool_call>"));
        var api = Fixtures.Api("Llama-3.3-70B-Instruct", transport: transport);
        Assert.Null(api.EmulateTools);

        var (result, _) = await Generate(api, transport, tools: [Fixtures.WeatherTool]);

        Assert.True(api.EmulateTools);
        var body = transport.LastRequest!.BodyJson;
        Assert.False(body.ContainsKey("tools"));
        Assert.False(body.ContainsKey("tool_choice"));
        Assert.Equal("system", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Contains("<tools>", body["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("Llama-3.3-70B-Instruct", body["model"]!.GetValue<string>());

        // the recorded request omits tools too (tools: null) and never carries the model
        Assert.True(result.Call.Request.ContainsKey("tools"));
        Assert.Null(result.Call.Request["tools"]);
        Assert.False(result.Call.Request.ContainsKey("tool_choice"));
        Assert.False(result.Call.Request.ContainsKey("model"));

        var output = result.OutputOrThrow();
        var call = Assert.Single(output.Message.ToolCalls!);
        Assert.Equal("get_weather", call.Function);
        Assert.Equal("""{"city":"Paris"}""", call.Arguments.ToJsonString());
        Assert.Equal("Llama-3.3-70B-Instruct", output.Message.Model);
        Assert.Equal(StopReason.Stop, output.StopReason);
    }

    [Fact]
    public async Task native_tools_sent_for_non_llama_models()
    {
        var transport = Transport(Fixtures.ToolCallCompletion("call_1", "get_weather", "{\"city\": \"Paris\"}"));
        var api = Fixtures.Api("gpt-4o", transport: transport);

        var (result, _) = await Generate(api, transport, tools: [Fixtures.WeatherTool], toolChoice: ToolChoice.Any);

        Assert.Null(api.EmulateTools);
        var body = transport.LastRequest!.BodyJson;
        Assert.Equal("get_weather", body["tools"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("required", body["tool_choice"]!.GetValue<string>());
        Assert.Equal("user", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("required", result.Call.Request["tool_choice"]!.GetValue<string>());
        Assert.Equal("get_weather", result.Call.Request["tools"]![0]!["function"]!["name"]!.GetValue<string>());

        var output = result.OutputOrThrow();
        Assert.Equal(StopReason.ToolCalls, output.StopReason);
        var call = Assert.Single(output.Message.ToolCalls!);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("""{"city":"Paris"}""", call.Arguments.ToJsonString());
        Assert.Equal("test-model", output.Message.Model);
        Assert.Equal(new ModelUsage(5, 9, 14), output.Usage);
    }

    [Fact]
    public async Task emulate_tools_opt_in_and_opt_out()
    {
        var transport = Transport(Fixtures.Completion("ok"));
        var forced = Fixtures.Api("gpt-4o", transport: transport, modelArgs: new Dictionary<string, object?> { ["emulate_tools"] = true });
        await Generate(forced, transport, tools: [Fixtures.WeatherTool]);
        Assert.False(transport.LastRequest!.BodyJson.ContainsKey("tools"));
        Assert.Equal("system", transport.LastRequest.BodyJson["messages"]![0]!["role"]!.GetValue<string>());

        var disabled = Fixtures.Api("Llama-3.3-70B-Instruct", transport: transport, modelArgs: new Dictionary<string, object?> { ["emulate_tools"] = false });
        await Generate(disabled, transport, tools: [Fixtures.WeatherTool]);
        Assert.False(disabled.EmulateTools);
        Assert.True(transport.LastRequest!.BodyJson.ContainsKey("tools"));
        Assert.Equal("user", transport.LastRequest.BodyJson["messages"]![0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task request_carries_completion_params_extras_and_auth_headers()
    {
        var transport = Transport(Fixtures.Completion("ok"));
        var api = Fixtures.Api("gpt-4o", transport: transport, modelArgs: new Dictionary<string, object?> { ["azure"] = true });
        var config = new GenerateConfig
        {
            FrequencyPenalty = 0.0, PresencePenalty = 0.0, MaxTokens = 2, Temperature = 0.0, TopP = 1.0, StopSeqs = ["END"], Seed = 7, NumChoices = 3,
        };

        var (result, _) = await Generate(api, transport, config: config);

        var request = transport.LastRequest!;
        Assert.Equal("https://example.com/models/chat/completions?api-version=2024-05-01-preview", request.Uri.ToString());
        Assert.Equal("test", request.Headers["api-key"]);
        Assert.Equal("Bearer test", request.Headers["Authorization"]);
        Assert.Equal("pass-through", request.Headers["extra-parameters"]);
        var body = request.BodyJson;
        Assert.True(body["azure"]!.GetValue<bool>());
        Assert.Equal(2, body["max_tokens"]!.GetValue<int>());
        Assert.Equal(7, body["seed"]!.GetValue<int>());
        Assert.Equal("END", body["stop"]![0]!.GetValue<string>());
        Assert.False(body.ContainsKey("n"));
        Assert.False(body.ContainsKey("stream"));
        Assert.Equal(
            """{"messages":[{"role":"user","content":"hi"}],"frequency_penalty":0,"presence_penalty":0,"temperature":0,"top_p":1,"max_tokens":2,"stop":["END"],"seed":7,"tools":null}""",
            result.Call.Request.ToJsonString());
        Assert.Equal("ok", result.OutputOrThrow().Completion);
        Assert.Equal("cmpl-1", result.Call.Response!["id"]!.GetValue<string>());
        Assert.Null(result.Call.Error);
    }

    [Fact]
    public async Task max_completion_tokens_is_sent_as_pass_through_extra_for_o_series()
    {
        var transport = Transport(Fixtures.Completion("ok"));
        var api = Fixtures.Api("o3-mini", transport: transport);
        await Generate(api, transport, config: new GenerateConfig { MaxTokens = 9 });
        var body = transport.LastRequest!.BodyJson;
        Assert.Equal(9, body["max_completion_tokens"]!.GetValue<int>());
        Assert.False(body.ContainsKey("max_tokens"));
        Assert.Equal("pass-through", transport.LastRequest.Headers["extra-parameters"]);
    }

    [Fact]
    public async Task entra_token_is_sent_in_both_headers()
    {
        using var env = EnvScope.Clean();
        var transport = Transport(Fixtures.Completion("ok"));
        var credential = new FakeTokenCredential("entra-token");
        var api = new AzureAIModelApi("gpt-4o", Fixtures.BaseUrl, settings: new AzureAIClientSettings { Transport = transport, TokenCredential = credential, ConfigureClientOptions = o => o.Retry.MaxRetries = 0 });

        await Generate(api, transport);

        Assert.Equal("entra-token", transport.LastRequest!.Headers["api-key"]);
        Assert.Equal("Bearer entra-token", transport.LastRequest.Headers["Authorization"]);
        Assert.Equal(["https://cognitiveservices.azure.com/.default"], Assert.Single(credential.Scopes));
    }

    [Fact]
    public async Task model_call_redacts_image_data_urls()
    {
        var transport = Transport(Fixtures.Completion("ok"));
        var api = Fixtures.Api("gpt-4o", transport: transport);
        var input = new ChatMessage[]
        {
            new ChatMessageUser(new Content[] { new ContentText("look"), new ContentImage("data:image/png;base64,AAAA", "low") }),
        };

        var (result, _) = await Generate(api, transport, input: input);

        var body = transport.LastRequest!.BodyJson;
        Assert.Equal("data:image/png;base64,AAAA", body["messages"]![0]!["content"]![1]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal("low", body["messages"]![0]!["content"]![1]!["image_url"]!["detail"]!.GetValue<string>());
        Assert.Equal("<base64-data-removed>", result.Call.Request["messages"]![0]!["content"]![1]!["image_url"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task content_filter_stop_details_from_non_streamed_response()
    {
        var json = Fixtures.Completion("par", "content_filter",
            """
            "content_filter_results":{"hate":{"filtered":false,"severity":"safe"},"jailbreak":{"filtered":false,"detected":true},"violence":{"filtered":true,"severity":"high"}}
            """.Trim());
        var transport = Transport(json);
        var (result, _) = await Generate(Fixtures.Api("gpt-4o", transport: transport), transport);

        var choice = result.OutputOrThrow().Choices[0];
        Assert.Equal(StopReason.ContentFilter, choice.StopReason);
        Assert.Equal("content_filter", choice.StopDetails!.Type);
        var category = Assert.Single(choice.StopDetails.Categories);
        Assert.Equal(new StopCategory("violence", "high"), category);
        Assert.Equal("Content filtered: violence (high)", choice.StopDetails.Explanation);
        Assert.Equal(new ModelUsage(3, 7, 10), result.Output!.Usage);
    }

    [Fact]
    public async Task choices_are_sorted_by_index_and_stop_reasons_mapped()
    {
        const string json = """
                            {"id":"1","created":1,"model":"m","choices":[
                            {"index":2,"finish_reason":"weird","message":{"role":"assistant","content":"c"}},
                            {"index":0,"finish_reason":"length","message":{"role":"assistant","content":"a"}},
                            {"index":1,"finish_reason":null,"message":{"role":"assistant","content":null}}]}
                            """;
        var transport = Transport(json);
        var (result, _) = await Generate(Fixtures.Api("gpt-4o", transport: transport), transport);
        var output = result.OutputOrThrow();
        Assert.Equal(["a", "", "c"], output.Choices.Select(c => c.Message.Text));
        Assert.Equal([StopReason.MaxTokens, StopReason.Unknown, StopReason.Unknown], output.Choices.Select(c => c.StopReason));
        Assert.Null(output.Usage);
        Assert.Equal("m", output.Model);
        Assert.Equal("a", output.Completion);
    }

    [Fact]
    public async Task maximum_context_length_error_becomes_model_length_output()
    {
        var transport = new CannedTransport { Responder = _ => CannedResponse.Error(400, "This model's maximum context length is 4096 tokens.") };
        var api = Fixtures.Api("my-org/gpt-4o", transport: transport);
        var (result, _) = await Generate(api, transport);

        var output = result.OutputOrThrow();
        Assert.Equal(StopReason.ModelLength, output.StopReason);
        Assert.Equal("This model's maximum context length is 4096 tokens.", output.Completion);
        Assert.Equal("my-org/gpt-4o", output.Model);
        Assert.True(result.Call.Error);
        Assert.Equal("This model's maximum context length is 4096 tokens.", result.Call.Response!["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task http_400_is_returned_as_terminal_error()
    {
        var transport = new CannedTransport { Responder = _ => CannedResponse.Error(400, "Bad request body") };
        var api = Fixtures.Api("gpt-4o", transport: transport);
        var (result, _) = await Generate(api, transport);

        Assert.Null(result.Output);
        var error = Assert.IsType<RequestFailedException>(result.Error);
        Assert.Equal(400, error.Status);
        Assert.True(result.Call.Error);
        Assert.False(api.ShouldRetry(error).Retry);
        Assert.Throws<RequestFailedException>(() => result.OutputOrThrow());
    }

    [Theory]
    [InlineData(500)]
    [InlineData(429)]
    [InlineData(401)]
    [InlineData(404)]
    public async Task other_http_errors_are_thrown(int status)
    {
        var transport = new CannedTransport { Responder = _ => CannedResponse.Error(status, "nope") };
        var api = Fixtures.Api("gpt-4o", transport: transport);
        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => Generate(api, transport));
        Assert.Equal(status, ex.Status);
        Assert.Equal(status is 500 or 429, api.ShouldRetry(ex).Retry);
        Assert.Equal(status == 401, api.IsAuthFailure(ex));
    }

    [Fact]
    public async Task transport_failure_is_not_retried()
    {
        var transport = new CannedTransport { Responder = _ => throw new RequestFailedException("connection refused") };
        var api = Fixtures.Api("gpt-4o", transport: transport);
        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => Generate(api, transport));
        Assert.Equal(0, ex.Status);
        Assert.False(api.ShouldRetry(ex).Retry);
    }

    [Fact]
    public async Task streaming_generate_assembles_output_reports_events_and_records_stream_flag()
    {
        var transport = new CannedTransport
        {
            Responder = _ => CannedResponse.Sse(
            [
                """{"id":"cmpl-1","created":123,"model":"test-model","choices":[{"index":0,"delta":{"role":"assistant","content":"hel"},"finish_reason":null}]}""",
                """{"id":"cmpl-1","created":123,"model":"test-model","choices":[{"index":0,"delta":{"content":"lo"},"finish_reason":"stop"}]}""",
                """{"id":"cmpl-1","created":123,"model":"test-model","choices":[],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}""",
            ]),
        };
        var api = Fixtures.Api("gpt-4o", transport: transport);
        var collector = new StreamCollector();

        var result = await api.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, Config, collector.Collect);

        Assert.True(transport.LastRequest!.BodyJson["stream"]!.GetValue<bool>());
        Assert.True(result.Call.Request["stream"]!.GetValue<bool>());
        var output = result.OutputOrThrow();
        Assert.Equal("hello", output.Completion);
        Assert.Equal(StopReason.Stop, output.StopReason);
        Assert.Equal(new ModelUsage(3, 2, 5), output.Usage);
        Assert.Equal("hello", string.Concat(collector.Events.OfType<StreamTextEvent>().Select(e => e.Text)));
        Assert.Equal("chat.completion", result.Call.Response!["object"]!.GetValue<string>());
    }

    [Fact]
    public async Task streamed_response_without_usage_warns_once()
    {
        ProviderLogger.Reset();
        var transport = new CannedTransport
        {
            Responder = _ => CannedResponse.Sse(
                ["""{"id":"cmpl-1","created":123,"model":"test-model","choices":[{"index":0,"delta":{"content":"x"},"finish_reason":"stop"}]}"""]),
        };
        var api = Fixtures.Api("gpt-4o", streaming: true, transport: transport);

        var first = await Generate(api, transport);
        var second = await Generate(api, transport);

        Assert.Null(first.Result.OutputOrThrow().Usage);
        Assert.Null(second.Result.OutputOrThrow().Usage);
        Assert.Equal(
            "azureai model 'gpt-4o' reported no token usage for a streamed response; pass -M streaming=false if you require usage reporting.",
            Assert.Single(ProviderLogger.Warnings));
    }

    [Fact]
    public async Task streaming_explicit_false_wins_over_on_stream()
    {
        var transport = Transport(Fixtures.Completion("ok"));
        var api = Fixtures.Api("gpt-4o", streaming: false, transport: transport);
        var collector = new StreamCollector();
        var result = await api.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, Config, collector.Collect);
        Assert.False(transport.LastRequest!.BodyJson.ContainsKey("stream"));
        Assert.Empty(collector.Events);
        Assert.Equal("ok", result.OutputOrThrow().Completion);
    }

    [Fact]
    public async Task empty_stream_raises_runtime_error()
    {
        var transport = new CannedTransport { Responder = _ => new CannedResponse(200, "data: [DONE]\n\n", "text/event-stream") };
        var api = Fixtures.Api("gpt-4o", streaming: true, transport: transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Generate(api, transport));
        Assert.Equal("Streaming response ended without delivering any chunks.", ex.Message);
        Assert.False(api.ShouldRetry(ex).Retry);
    }

    [Fact]
    public async Task mistral_requests_fold_user_messages_into_tool_messages()
    {
        var transport = Transport(Fixtures.Completion("ok"));
        var api = Fixtures.Api("Mistral-large-2411", transport: transport);
        var input = new ChatMessage[]
        {
            new ChatMessageUser("q"),
            new ChatMessageAssistant("", [new ToolCall("c1", "get_weather", new() { ["city"] = "Paris" })]),
            new ChatMessageTool("sunny", "c1"),
            new ChatMessageUser("thanks"),
        };
        await Generate(api, transport, input: input, tools: [Fixtures.WeatherTool]);
        var messages = transport.LastRequest!.BodyJson["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        Assert.Equal("sunnythanks", messages[2]!["content"]!.GetValue<string>());
        Assert.True(transport.LastRequest.BodyJson.ContainsKey("tools"));
        Assert.Null(api.MaxTokens());
    }

    [Fact]
    public async Task tool_history_round_trips_under_emulation()
    {
        var transport = Transport(Fixtures.Completion("It is sunny."));
        var api = Fixtures.Api("Llama-3.3-70B-Instruct", transport: transport);
        var input = new ChatMessage[]
        {
            new ChatMessageUser("q"),
            new ChatMessageAssistant("Checking", [new ToolCall("c1", "get_weather", new() { ["city"] = "Paris" })]),
            new ChatMessageTool("sunny", "c1", "get_weather"),
        };
        await Generate(api, transport, input: input, tools: [Fixtures.WeatherTool]);
        var messages = transport.LastRequest!.BodyJson["messages"]!.AsArray();
        Assert.Equal("Checking\n\n<tool_call>{\"name\": \"get_weather\", \"arguments\": {\"city\": \"Paris\"} }</tool_call>", messages[2]!["content"]!.GetValue<string>());
        Assert.Equal("tool", messages[3]!["role"]!.GetValue<string>());
        Assert.Equal("c1", messages[3]!["tool_call_id"]!.GetValue<string>());
    }

    [Fact]
    public void request_snapshot_helper_matches_python_shape()
    {
        var options = new Azure.AI.Inference.ChatCompletionsOptions();
        options.Messages.Add(new Azure.AI.Inference.ChatRequestUserMessage("hi"));
        options.Model = "m";
        options.AdditionalProperties["azure"] = BinaryData.FromString("true");
        var snapshot = AzureAIModelApi.RequestSnapshot(options, new JsonObject { ["max_tokens"] = 5 }, streaming: true, sendTools: false);
        Assert.Equal("""{"messages":[{"role":"user","content":"hi"}],"max_tokens":5,"stream":true,"tools":null}""", snapshot.ToJsonString());
    }
}

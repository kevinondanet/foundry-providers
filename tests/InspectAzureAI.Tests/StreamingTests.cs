using System.Text.Json.Nodes;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Tests;

/// <summary>Port of the streaming tests in tests/model/providers/test_azureai.py.</summary>
public class StreamingTests
{
    [Fact]
    public void test_azureai_resolve_streaming_honors_on_stream()
    {
        var collector = new StreamCollector();

        var api = Fixtures.Api();
        Assert.Null(api.Streaming);
        Assert.False(api.ResolveStreaming());
        using (ModelStreamObserver.Install(new ModelStreamObserver("test", collector.Collect)))
        {
            Assert.True(api.ResolveStreaming());
            Assert.False(Fixtures.Api(streaming: false).ResolveStreaming());
        }

        Assert.True(Fixtures.Api(streaming: true).ResolveStreaming());
        Assert.Null(Fixtures.Api(streaming: "auto").Streaming);
        var ex = Assert.Throws<ArgumentException>(() => Fixtures.Api(streaming: "always"));
        Assert.Contains("streaming", ex.Message);
    }

    [Fact]
    public async Task test_azureai_completion_from_stream()
    {
        var updates = new[]
        {
            Fixtures.Update("""{"choices":[{"index":0,"delta":{"role":"assistant","content":"hel"},"finish_reason":null}]}"""),
            Fixtures.Update("""{"choices":[{"index":0,"delta":{"tool_calls":[{"id":"call_1","function":{"name":"bash","arguments":"{"}}]},"finish_reason":null}]}"""),
            Fixtures.Update("""{"choices":[{"index":0,"delta":{"tool_calls":[{"function":{"arguments":"\"cmd\": \"ls\"}"}}]},"finish_reason":"tool_calls","content_filter_results":{"hate":{"filtered":false,"severity":"safe"}}}]}"""),
            Fixtures.Update("""{"choices":[],"usage":{"prompt_tokens":3,"completion_tokens":7,"total_tokens":10}}"""),
        };

        var collector = new StreamCollector();
        AzureChatCompletions response;
        using (ModelStreamObserver.Install(new ModelStreamObserver("test", collector.Collect)))
        {
            response = await AzureAIStreamAccumulator.CompletionFromStreamAsync(Fixtures.Updates(updates));
        }

        var choice = response.Choices[0];
        Assert.Equal("hel", choice.Message.Content);
        Assert.Equal("tool_calls", choice.FinishReason);
        var toolCalls = choice.Message.ToolCalls;
        Assert.NotNull(toolCalls);
        Assert.Equal("call_1", toolCalls[0].Id);
        Assert.Equal("bash", toolCalls[0].Name);
        Assert.Equal("{\"cmd\": \"ls\"}", toolCalls[0].Arguments);
        Assert.Equal(10, response.Usage!.TotalTokens);
        Assert.Equal("""{"hate":{"filtered":false,"severity":"safe"}}""", choice.ContentFilterResults!.ToJsonString());
        Assert.Equal("cmpl-1", response.Id);
        Assert.Equal("chat.completion", response.Raw["object"]!.GetValue<string>());

        Assert.Equal([typeof(StreamTextEvent), typeof(StreamToolCallEvent), typeof(StreamToolCallEvent)], collector.Events.Select(e => e.GetType()));
        Assert.Equal("hel", ((StreamTextEvent)collector.Events[0]).Text);
        Assert.Equal("call_1", ((StreamToolCallEvent)collector.Events[1]).Id);
        Assert.Equal("call_1", ((StreamToolCallEvent)collector.Events[2]).Id);
        Assert.Equal("bash", ((StreamToolCallEvent)collector.Events[2]).Function);
        Assert.Equal("\"cmd\": \"ls\"}", ((StreamToolCallEvent)collector.Events[2]).Arguments);
    }

    [Fact]
    public async Task test_azureai_stream_gated_without_on_stream()
    {
        ModelStreamObserver.DeltaInterceptor = _ => throw new InvalidOperationException("delta reported without an on_stream consumer");
        try
        {
            var updates = new[]
            {
                Fixtures.Update("""{"choices":[{"index":0,"delta":{"role":"assistant","content":"hel"},"finish_reason":null}]}"""),
                Fixtures.Update("""{"choices":[{"index":0,"delta":{"content":"lo"},"finish_reason":"stop"}]}"""),
                Fixtures.Update("""{"choices":[],"usage":{"prompt_tokens":3,"completion_tokens":7,"total_tokens":10}}"""),
            };

            var observer = new ModelStreamObserver("test", null);
            AzureChatCompletions response;
            using (ModelStreamObserver.Install(observer))
            {
                response = await AzureAIStreamAccumulator.CompletionFromStreamAsync(Fixtures.Updates(updates));
            }

            Assert.Equal("hello", response.Choices[0].Message.Content);
            Assert.Equal(7, observer.TokensCurrent);
            Assert.True(observer.Started);
        }
        finally
        {
            ModelStreamObserver.DeltaInterceptor = null;
        }
    }

    [Fact]
    public async Task test_azureai_stream_parallel_tool_calls_by_index()
    {
        var updates = new[]
        {
            Fixtures.Update("""{"choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_a","function":{"name":"bash","arguments":"{\"a\""}},{"index":1,"id":"call_b","function":{"name":"python","arguments":"{\"b\""}}]},"finish_reason":null}]}"""),
            Fixtures.Update("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":": 1}"}},{"index":1,"function":{"arguments":": 2}"}}]},"finish_reason":"tool_calls"}]}"""),
        };

        var response = await AzureAIStreamAccumulator.CompletionFromStreamAsync(Fixtures.Updates(updates));
        var toolCalls = response.Choices[0].Message.ToolCalls;
        Assert.NotNull(toolCalls);
        Assert.Equal(2, toolCalls.Count);
        Assert.Equal("call_a", toolCalls[0].Id);
        Assert.Equal("{\"a\": 1}", toolCalls[0].Arguments);
        Assert.Equal("call_b", toolCalls[1].Id);
        Assert.Equal("{\"b\": 2}", toolCalls[1].Arguments);
    }

    [Fact]
    public async Task test_azureai_streamed_content_filter_stop_details()
    {
        var updates = new[]
        {
            Fixtures.Update("""{"choices":[{"index":0,"delta":{"role":"assistant","content":"par"},"finish_reason":null}]}"""),
            Fixtures.Update("""{"choices":[{"index":0,"delta":{},"finish_reason":"content_filter","content_filter_results":{"violence":{"filtered":true,"severity":"high"}}}]}"""),
        };

        var response = await AzureAIStreamAccumulator.CompletionFromStreamAsync(Fixtures.Updates(updates));
        var choice = AzureAIModelApi.ChatCompletionChoice("test-model", response.Choices[0]);
        Assert.Equal(StopReason.ContentFilter, choice.StopReason);
        Assert.NotNull(choice.StopDetails);
        Assert.Equal("content_filter", choice.StopDetails.Type);
        Assert.Equal("violence", choice.StopDetails.Categories[0].Category);
        Assert.Equal("high", choice.StopDetails.Categories[0].Level);
        Assert.Equal("violence", choice.StopDetails.Category);
        Assert.Equal("Content filtered: violence (high)", choice.StopDetails.Explanation);
        Assert.Equal("par", choice.Message.Text);
    }

    [Fact]
    public async Task test_azureai_completion_from_stream_empty()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => AzureAIStreamAccumulator.CompletionFromStreamAsync(Fixtures.Updates([])));
        Assert.Contains("without delivering any chunks", ex.Message);
    }

    [Fact]
    public async Task stream_accumulator_synthesises_ids_and_keeps_last_content_filter_results()
    {
        var updates = new[]
        {
            Fixtures.Update("""{"choices":[{"index":0,"delta":{"tool_calls":[{"function":{"arguments":"{\"a\""}}]},"content_filter_results":{"hate":{"filtered":false}}}]}"""),
            Fixtures.Update("""{"choices":[{"index":0,"delta":{"tool_calls":[{"function":{"arguments":": 1}"}}]},"content_filter_results":{"violence":{"filtered":true,"severity":"medium"}}}]}"""),
            Fixtures.Update("""{"choices":[{"index":0,"delta":{"tool_calls":[{"id":"call_x","function":{"arguments":"{}"}}]},"finish_reason":"tool_calls","content_filter_results":{}}]}"""),
        };

        var response = await AzureAIStreamAccumulator.CompletionFromStreamAsync(Fixtures.Updates(updates));
        var toolCalls = response.Choices[0].Message.ToolCalls!;
        Assert.Equal(2, toolCalls.Count);
        Assert.Equal("tool_call_0_0", toolCalls[0].Id);
        Assert.Equal("", toolCalls[0].Name);
        Assert.Equal("{\"a\": 1}", toolCalls[0].Arguments);
        Assert.Equal("call_x", toolCalls[1].Id);
        Assert.Equal("""{"violence":{"filtered":true,"severity":"medium"}}""", response.Choices[0].ContentFilterResults!.ToJsonString());
    }

    [Fact]
    public async Task stream_accumulator_reports_progress_heartbeats_and_sorts_choices()
    {
        var updates = new[]
        {
            Fixtures.Update("""{"choices":[{"index":1,"delta":{"content":"second"}}]}"""),
            Fixtures.Update("""{"choices":[{"index":0,"delta":{"content":"first"}}]}"""),
            Fixtures.Update("""{"choices":[]}"""),
        };
        var collector = new StreamCollector();
        var observer = new ModelStreamObserver("test", collector.Collect);
        AzureChatCompletions response;
        using (ModelStreamObserver.Install(observer))
        {
            response = await AzureAIStreamAccumulator.CompletionFromStreamAsync(Fixtures.Updates(updates));
        }

        Assert.Equal(["first", "second"], response.Choices.Select(c => c.Message.Content));
        // only choice 0 reports deltas; the other updates fall back to a bare heartbeat
        Assert.Equal("first", ((StreamTextEvent)Assert.Single(collector.Events)).Text);
        Assert.Equal(3, observer.ProgressReports);
        Assert.Null(response.Usage);
    }

    [Fact]
    public async Task on_stream_handler_exception_detaches_the_callback()
    {
        ProviderLogger.Reset();
        var calls = 0;
        var observer = new ModelStreamObserver("test", _ =>
        {
            calls++;
            throw new InvalidOperationException("boom");
        });
        var updates = new[]
        {
            Fixtures.Update("""{"choices":[{"index":0,"delta":{"content":"a"}}]}"""),
            Fixtures.Update("""{"choices":[{"index":0,"delta":{"content":"b"},"finish_reason":"stop"}]}"""),
        };
        AzureChatCompletions response;
        using (ModelStreamObserver.Install(observer))
        {
            response = await AzureAIStreamAccumulator.CompletionFromStreamAsync(Fixtures.Updates(updates));
            Assert.False(ModelStreamObserver.ModelStreamRequested());
        }

        Assert.Equal(1, calls);
        Assert.Null(observer.OnStream);
        Assert.Equal("ab", response.Choices[0].Message.Content);
        var warning = Assert.Single(ProviderLogger.Warnings);
        Assert.StartsWith("on_stream handler raised an exception; streaming callbacks are disabled for the remainder of this test generate call", warning);
        Assert.Contains("boom", warning);
    }

    [Fact]
    public async Task sse_parser_yields_json_updates_until_done()
    {
        var body = "data: {\"id\":\"1\"}\n\n: comment\ndata: {\"id\":\"2\",\ndata: \"x\":1}\n\ndata: [DONE]\n\ndata: {\"id\":\"ignored\"}\n\n";
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
        var updates = new List<JsonObject>();
        await foreach (var update in SseParser.ReadUpdatesAsync(stream))
        {
            updates.Add(update);
        }

        Assert.Equal(["""{"id":"1"}""", """{"id":"2","x":1}"""], updates.Select(u => u.ToJsonString()));
    }
}

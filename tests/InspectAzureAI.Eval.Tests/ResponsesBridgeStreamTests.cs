using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.OpenAI;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tests;

/// <summary>The synthesized Responses event stream (port of the proxy's Responses SSE, with deviations D-R5 and D-R6).</summary>
public class ResponsesBridgeStreamTests
{
    private const string CustomToolRequest =
        """{"model": "m", "input": "hi", "tools": [{"type": "custom", "name": "apply_patch", "format": {"type": "grammar", "syntax": "lark", "definition": "start: x"}}]}""";

    private static ResponsesBridgeRequest Parsed(string json = CustomToolRequest) =>
        ResponsesBridgeApi.ParseRequest(JsonNode.Parse(json)!.AsObject());

    private static JsonObject MixedResponse(StopReason stopReason = StopReason.ToolCalls)
    {
        var message = new ChatMessageAssistant(
            new Content[]
            {
                new ContentText("Hello"),
                new ContentToolUse("web_search", "ws_1", "web_search", """{"type": "search", "query": "cats"}""", "[]"),
            },
            toolCalls:
            [
                new ToolCall("call_1", "shell", new JsonObject { ["command"] = "ls" }),
                new ToolCall("call_2", "apply_patch", new JsonObject { ["input"] = "*** Begin Patch" }) { Type = "custom" },
            ]);
        var output = new ModelOutput
        {
            Model = "served",
            Choices = [new ChatCompletionChoice(message, stopReason)],
            Usage = new ModelUsage(100, 20, 120) { InputTokensCacheRead = 40 },
        };
        return ResponsesBridgeApi.ResponseFromOutput(output, "served", Parsed());
    }

    [Fact]
    public void events_follow_the_codex_sequence_for_every_item_type()
    {
        var events = ResponsesBridgeApi.StreamEvents(MixedResponse());

        Assert.Equal(
            [
                "response.created", "response.in_progress",
                "response.output_item.added", "response.content_part.added", "response.output_text.delta", "response.output_text.done", "response.content_part.done", "response.output_item.done",
                "response.output_item.added", "response.web_search_call.in_progress", "response.web_search_call.searching", "response.web_search_call.completed", "response.output_item.done",
                "response.output_item.added", "response.function_call_arguments.delta", "response.function_call_arguments.done", "response.output_item.done",
                "response.output_item.added", "response.custom_tool_call_input.delta", "response.custom_tool_call_input.done", "response.output_item.done",
                "response.completed",
            ],
            events.Select(e => e.Event!).ToArray());
        Assert.All(events, e => Assert.Equal(e.Event, e.Data["type"]!.GetValue<string>()));
        Assert.Equal(Enumerable.Range(1, events.Count).ToArray(), events.Select(e => e.Data["sequence_number"]!.GetValue<int>()).ToArray());
    }

    [Fact]
    public void item_ids_match_the_output_item_at_each_index()
    {
        var response = MixedResponse();
        var output = response["output"]!.AsArray();

        var events = ResponsesBridgeApi.StreamEvents(response);

        var withItemId = events.Select(e => e.Data.AsObject()).Where(d => d.ContainsKey("item_id")).ToList();
        Assert.NotEmpty(withItemId);
        Assert.All(withItemId, d => Assert.Equal(output[d["output_index"]!.GetValue<int>()]!["id"]!.GetValue<string>(), d["item_id"]!.GetValue<string>()));
        var withItem = events.Select(e => e.Data.AsObject()).Where(d => d.ContainsKey("item")).ToList();
        Assert.Equal(8, withItem.Count);
        Assert.All(withItem, d => Assert.Equal(output[d["output_index"]!.GetValue<int>()]!["id"]!.GetValue<string>(), d["item"]!["id"]!.GetValue<string>()));
    }

    [Fact]
    public void events_carry_the_fields_codex_requires()
    {
        var events = ResponsesBridgeApi.StreamEvents(MixedResponse());
        JsonNode Payload(string type) => events.Single(e => e.Event == type).Data;

        Assert.Equal("Hello", Payload("response.output_text.delta")["delta"]!.GetValue<string>());
        var customDelta = Payload("response.custom_tool_call_input.delta");
        Assert.Equal(("*** Begin Patch", "call_2"), (customDelta["delta"]!.GetValue<string>(), customDelta["call_id"]!.GetValue<string>()));
        Assert.StartsWith("ctc_", customDelta["item_id"]!.GetValue<string>());
        var argumentsDone = Payload("response.function_call_arguments.done");
        Assert.Equal(("shell", """{"command": "ls"}"""), (argumentsDone["name"]!.GetValue<string>(), argumentsDone["arguments"]!.GetValue<string>()));

        var completed = Payload("response.completed")["response"]!;
        Assert.False(string.IsNullOrEmpty(completed["id"]!.GetValue<string>()));
        var usage = completed["usage"]!;
        Assert.Equal((140, 20, 120), (usage["input_tokens"]!.GetValue<int>(), usage["output_tokens"]!.GetValue<int>(), usage["total_tokens"]!.GetValue<int>()));
    }

    [Fact]
    public void created_in_progress_and_added_items_are_empty_and_in_progress()
    {
        var events = ResponsesBridgeApi.StreamEvents(MixedResponse());

        foreach (var type in new[] { "response.created", "response.in_progress" })
        {
            var response = events.Single(e => e.Event == type).Data["response"]!;
            Assert.Equal("in_progress", response["status"]!.GetValue<string>());
            Assert.Empty(response["output"]!.AsArray());
            Assert.Null(response["usage"]);
        }

        var added = events.Where(e => e.Event == "response.output_item.added").Select(e => e.Data["item"]!).ToList();
        Assert.All(added, item => Assert.Equal("in_progress", item["status"]!.GetValue<string>()));
        Assert.Empty(added[0]["content"]!.AsArray());
        Assert.Equal("", added[2]["arguments"]!.GetValue<string>());
        Assert.Equal("", added[3]["input"]!.GetValue<string>());
        var done = events.Where(e => e.Event == "response.output_item.done").Select(e => e.Data["item"]!).ToList();
        Assert.All(done, item => Assert.Equal("completed", item["status"]!.GetValue<string>()));
        Assert.Equal("Hello", done[0]["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void completed_is_last_and_forced_to_completed_for_incomplete_responses()
    {
        var response = MixedResponse(StopReason.MaxTokens);
        Assert.Equal("incomplete", response["status"]!.GetValue<string>());

        var events = ResponsesBridgeApi.StreamEvents(response);

        var last = events[^1];
        Assert.Equal("response.completed", last.Event);
        Assert.Equal("completed", last.Data["response"]!["status"]!.GetValue<string>());
        Assert.Equal("max_output_tokens", last.Data["response"]!["incomplete_details"]!["reason"]!.GetValue<string>());
        Assert.DoesNotContain(events, e => e.Event == "response.incomplete");
        Assert.Equal("incomplete", response["status"]!.GetValue<string>());
    }

    [Fact]
    public void an_empty_text_part_has_no_delta()
    {
        var output = ModelOutput.FromContent("served", MessageContent.FromString(""), StopReason.Stop);

        var events = ResponsesBridgeApi.StreamEvents(ResponsesBridgeApi.ResponseFromOutput(output, "served", Parsed()));

        Assert.DoesNotContain(events, e => e.Event == "response.output_text.delta");
        Assert.Equal("", events.Single(e => e.Event == "response.output_text.done").Data["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task streamed_events_round_trip_through_the_provider_accumulator()
    {
        var message = new ChatMessageAssistant("Hello world", toolCalls:
        [
            new ToolCall("call_1", "get_weather", new JsonObject { ["city"] = "Oslo" }),
            new ToolCall("call_2", "bash", new JsonObject { ["cmd"] = "ls", ["timeout"] = 30 }),
        ]);
        var source = new ModelOutput
        {
            Model = "served",
            Choices = [new ChatCompletionChoice(message, StopReason.ToolCalls)],
            Usage = new ModelUsage(100, 20, 120) { InputTokensCacheRead = 40, ReasoningTokens = 7 },
        };
        var events = ResponsesBridgeApi.StreamEvents(ResponsesBridgeApi.ResponseFromOutput(source, "served", Parsed("""{"model": "m", "input": "hi"}""")));

        var accumulated = await ResponsesStreamAccumulator.AccumulateAsync(ToAsync(events.Select(e => e.Data.AsObject())));
        var parsed = ResponsesOutput.Parse(accumulated, "served");

        Assert.Equal("response.completed", events[^1].Event);
        Assert.Equal(source.Message.Text, parsed.Message.Text);
        Assert.Equal(StopReason.ToolCalls, parsed.StopReason);
        Assert.Equal(
            source.Message.ToolCalls!.Select(c => (c.Id, c.Function, PythonJson.Dumps(c.Arguments))).ToArray(),
            parsed.Message.ToolCalls!.Select(c => (c.Id, c.Function, PythonJson.Dumps(c.Arguments))).ToArray());
        Assert.Equal(source.Usage, parsed.Usage);
    }

    [Fact]
    public void frames_are_named_events()
    {
        var frame = ResponsesBridgeApi.StreamEvents(MixedResponse())[0].Format();

        Assert.StartsWith("event: response.created\ndata: {\"type\": \"response.created\", \"sequence_number\": 1, \"response\": {", frame);
        Assert.EndsWith("}\n\n", frame);
    }

    [Theory]
    [InlineData(500, "server_error")]
    [InlineData(400, "invalid_request_error")]
    public void failed_event_has_the_response_failed_shape(int status, string code)
    {
        var failed = ResponsesBridgeApi.FailedEvent("resp_1", 7, status, "boom");

        Assert.Equal("response.failed", failed.Event);
        Assert.Equal(
            """{"type": "response.failed", "sequence_number": 7, "response": {"id": "resp_1", "object": "response", "status": "failed", "output": [], "error": {"code": "CODE", "message": "boom"}}}""".Replace("CODE", code, StringComparison.Ordinal),
            PythonJson.Dumps(failed.Data));
    }

    private static async IAsyncEnumerable<JsonObject> ToAsync(IEnumerable<JsonObject> items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }
}

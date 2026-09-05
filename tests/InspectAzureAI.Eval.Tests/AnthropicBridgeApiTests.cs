using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

/// <summary>Port-level behaviour of <c>agent/_bridge/anthropic_api_impl.py</c>: request translation, response shape, stop reasons, usage and the synthesized event stream.</summary>
public class AnthropicBridgeApiTests
{
    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    private static JsonObject FullRequest() => Json("""
        {
          "model": "claude-sonnet-4-6",
          "max_tokens": 1024,
          "temperature": 0.3,
          "stop_sequences": ["END"],
          "thinking": {"type": "enabled", "budget_tokens": 512},
          "system": [
            {"type": "text", "text": "x-anthropic-billing-header: cc"},
            {"type": "text", "text": "You are Claude Code."},
            {"type": "text", "text": ""}
          ],
          "tools": [
            {"name": "bash", "description": "Run a command", "input_schema": {"type": "object", "properties": {"cmd": {"type": "string", "description": "The command"}}, "required": ["cmd"]}},
            {"type": "web_search_20250305", "name": "web_search"}
          ],
          "tool_choice": {"type": "tool", "name": "bash"},
          "messages": [
            {"role": "user", "content": [
              {"type": "text", "text": "Look at this"},
              {"type": "image", "source": {"type": "base64", "media_type": "image/png", "data": "iVBORw0KGgo="}}
            ]},
            {"role": "assistant", "content": [
              {"type": "thinking", "thinking": "Let me run it", "signature": "sig-1"},
              {"type": "redacted_thinking", "data": "opaque"},
              {"type": "text", "text": "Running <result>now</result>"},
              {"type": "tool_use", "id": "toolu_1", "name": "bash", "input": {"cmd": "ls"}},
              {"type": "tool_use", "id": "toolu_2", "name": "bash", "input": {"cmd": "pwd"}}
            ]},
            {"role": "user", "content": [
              {"type": "tool_result", "tool_use_id": "toolu_1", "content": "file.txt"},
              {"type": "tool_result", "tool_use_id": "toolu_2", "content": [{"type": "text", "text": "permission denied"}], "is_error": true},
              {"type": "text", "text": "continue"}
            ]}
          ]
        }
        """);

    [Fact]
    public void request_round_trips_system_blocks_text_images_thinking_tool_use_and_tool_results()
    {
        var parsed = AnthropicBridgeApi.ParseRequest(FullRequest());

        Assert.Equal("claude-sonnet-4-6", parsed.Model);
        Assert.False(parsed.Stream);
        var messages = parsed.Messages;
        Assert.Equal(7, messages.Count);

        Assert.Equal("x-anthropic-billing-header: cc", Assert.IsType<ChatMessageSystem>(messages[0]).Text);
        Assert.Equal("You are Claude Code.", Assert.IsType<ChatMessageSystem>(messages[1]).Text);

        var user = Assert.IsType<ChatMessageUser>(messages[2]);
        Assert.Equal("Look at this", Assert.IsType<ContentText>(user.ContentList[0]).Text);
        Assert.Equal("data:image/png;base64,iVBORw0KGgo=", Assert.IsType<ContentImage>(user.ContentList[1]).Image);

        var assistant = Assert.IsType<ChatMessageAssistant>(messages[3]);
        var thinking = Assert.IsType<ContentReasoning>(assistant.ContentList[0]);
        Assert.Equal(("Let me run it", "sig-1", false), (thinking.Reasoning, thinking.Signature, thinking.Redacted));
        var redacted = Assert.IsType<ContentReasoning>(assistant.ContentList[1]);
        Assert.Equal(("", "opaque", true), (redacted.Reasoning, redacted.Signature, redacted.Redacted));
        Assert.Equal("Running now", Assert.IsType<ContentText>(assistant.ContentList[2]).Text);
        Assert.Equal(["toolu_1", "toolu_2"], assistant.ToolCalls!.Select(c => c.Id).ToArray());
        Assert.Equal("ls", assistant.ToolCalls![0].Arguments["cmd"]!.GetValue<string>());

        var result1 = Assert.IsType<ChatMessageTool>(messages[4]);
        Assert.Equal(("toolu_1", "bash", "file.txt"), (result1.ToolCallId, result1.Function, result1.Text));
        Assert.Null(result1.Error);
        var result2 = Assert.IsType<ChatMessageTool>(messages[5]);
        Assert.Equal("permission denied", result2.Text);
        Assert.Equal(new ToolCallError("unknown", "permission denied"), result2.Error);
        Assert.Equal("continue", Assert.IsType<ChatMessageUser>(messages[6]).Text);

        var tool = Assert.Single(parsed.Tools);
        Assert.Equal("bash", tool.Name);
        Assert.Equal(["string"], tool.Parameters.Properties["cmd"].Type);
        Assert.Equal(["cmd"], tool.Parameters.Required);
        Assert.Equal("bash", Assert.IsType<ToolFunction>(parsed.ToolChoice).Name);

        Assert.Equal(1024, parsed.Config.MaxTokens);
        Assert.Equal(0.3, parsed.Config.Temperature);
        Assert.Equal(["END"], parsed.Config.StopSeqs);
        Assert.Equal(512, parsed.Config.ReasoningTokens);
    }

    [Fact]
    public void string_system_and_string_contents_are_accepted()
    {
        var parsed = AnthropicBridgeApi.ParseRequest(Json("""
            {"model": "m", "system": "Be brief.", "messages": [{"role": "user", "content": "hi"}, {"role": "assistant", "content": "hello"}, {"role": "user", "content": "bye"}]}
            """));

        Assert.Equal(["system", "user", "assistant", "user"], parsed.Messages.Select(m => m.Role).ToArray());
        Assert.Equal("Be brief.", parsed.Messages[0].Text);
        Assert.Equal("hello", parsed.Messages[2].Text);
        Assert.Same(ToolChoice.Auto, parsed.ToolChoice);
        Assert.Empty(parsed.Tools);
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("any", "any")]
    [InlineData("none", "none")]
    public void tool_choice_presets_map_to_inspect_presets(string type, string expected)
    {
        var choice = AnthropicBridgeApi.ToolChoiceFromAnthropicToolChoice(Json($$"""{"type": "{{type}}"}"""));
        Assert.Equal(expected, choice!.ToString());
    }

    [Fact]
    public void invalid_tool_choice_and_missing_model_are_client_errors()
    {
        Assert.Throws<BridgeRequestException>(() => AnthropicBridgeApi.ToolChoiceFromAnthropicToolChoice(Json("""{"type": "bogus"}""")));
        Assert.Throws<BridgeRequestException>(() => AnthropicBridgeApi.ToolChoiceFromAnthropicToolChoice(Json("""{"type": "tool"}""")));
        Assert.Throws<BridgeRequestException>(() => AnthropicBridgeApi.ParseRequest(Json("""{"messages": []}""")));
        Assert.Throws<BridgeRequestException>(() => AnthropicBridgeApi.ParseRequest(Json("""{"model": "m"}""")));
    }

    [Fact]
    public void disable_parallel_tool_use_and_effort_reach_the_config()
    {
        var config = AnthropicBridgeApi.GenerateConfigFromAnthropic(Json("""
            {"model": "m", "messages": [], "tool_choice": {"type": "auto", "disable_parallel_tool_use": true}, "thinking": {"type": "adaptive"}, "output_config": {"effort": "high"}}
            """));

        Assert.False(config.ParallelToolCalls);
        Assert.Equal("high", config.ReasoningEffort);
        Assert.Null(config.ReasoningTokens);
    }

    [Fact]
    public void response_carries_blocks_stop_reason_and_usage()
    {
        var message = new ChatMessageAssistant(
            new Content[] { new ContentReasoning("thinking hard", "sig"), new ContentReasoning("", "blob", Redacted: true), new ContentText("Done") },
            toolCalls: [new ToolCall("toolu_9", "bash", new JsonObject { ["cmd"] = "ls" })]);
        var output = new ModelOutput
        {
            Model = "served-model",
            Choices = [new ChatCompletionChoice(message, StopReason.ToolCalls)],
            Usage = new ModelUsage(100, 20, 120) { InputTokensCacheRead = 40, InputTokensCacheWrite = 5, ReasoningTokens = 7 },
        };

        var response = AnthropicBridgeApi.ResponseFromOutput(output, "claude-sonnet-4-6");

        Assert.StartsWith("msg_", response["id"]!.GetValue<string>());
        Assert.Equal("message", response["type"]!.GetValue<string>());
        Assert.Equal("assistant", response["role"]!.GetValue<string>());
        Assert.Equal("claude-sonnet-4-6", response["model"]!.GetValue<string>());
        Assert.Equal("tool_use", response["stop_reason"]!.GetValue<string>());
        Assert.Null(response["stop_sequence"]);
        Assert.Equal(
            """[{"type": "thinking", "thinking": "thinking hard", "signature": "sig"}, {"type": "redacted_thinking", "data": "blob"}, {"type": "text", "text": "Done"}, {"type": "tool_use", "id": "toolu_9", "name": "bash", "input": {"cmd": "ls"}}]""",
            InspectAzureAI.Provider.Util.PythonJson.Dumps(response["content"]));
        Assert.Equal(
            """{"input_tokens": 100, "output_tokens": 20, "cache_creation_input_tokens": 5, "cache_read_input_tokens": 40, "output_tokens_details": {"thinking_tokens": 7}}""",
            InspectAzureAI.Provider.Util.PythonJson.Dumps(response["usage"]));
    }

    [Fact]
    public void empty_text_becomes_the_no_content_placeholder_and_missing_usage_is_zero()
    {
        var response = AnthropicBridgeApi.ResponseFromOutput(ModelOutput.FromContent("m", ""), "m");

        Assert.Equal(AnthropicBridgeApi.NoContent, response["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(0, response["usage"]!["input_tokens"]!.GetValue<int>());
        Assert.Null(response["usage"]!["cache_read_input_tokens"]);
    }

    [Theory]
    [InlineData(StopReason.Stop, "end_turn")]
    [InlineData(StopReason.MaxTokens, "max_tokens")]
    [InlineData(StopReason.ModelLength, "max_tokens")]
    [InlineData(StopReason.ToolCalls, "tool_use")]
    [InlineData(StopReason.ContentFilter, "refusal")]
    [InlineData(StopReason.Unknown, "end_turn")]
    public void stop_reasons_map_to_anthropic_stop_reasons(StopReason stopReason, string expected) =>
        Assert.Equal(expected, AnthropicBridgeApi.AnthropicStopReason(stopReason));

    [Fact]
    public void stream_events_follow_the_messages_api_order_exactly()
    {
        var message = new ChatMessageAssistant(
            new Content[] { new ContentReasoning("plan", "sig"), new ContentText("Hello") },
            toolCalls: [new ToolCall("toolu_1", "bash", new JsonObject { ["cmd"] = "ls" })]);
        var output = new ModelOutput
        {
            Model = "m",
            Choices = [new ChatCompletionChoice(message, StopReason.ToolCalls)],
            Usage = new ModelUsage(10, 5, 15) { InputTokensCacheRead = 3 },
        };
        var response = AnthropicBridgeApi.ResponseFromOutput(output, "claude");

        var events = AnthropicBridgeApi.StreamEvents(response);

        Assert.Equal(
        [
            "message_start",
            "content_block_start", "content_block_delta", "content_block_delta", "content_block_stop",
            "content_block_start", "content_block_delta", "content_block_stop",
            "content_block_start", "content_block_delta", "content_block_stop",
            "message_delta",
            "message_stop",
        ], events.Select(e => e.Event!).ToArray());

        var start = events[0].Data["message"]!;
        Assert.Equal(response["id"]!.GetValue<string>(), start["id"]!.GetValue<string>());
        Assert.Empty(start["content"]!.AsArray());
        Assert.Equal(10, start["usage"]!["input_tokens"]!.GetValue<int>());
        Assert.Equal(0, start["usage"]!["output_tokens"]!.GetValue<int>());
        Assert.Equal(3, start["usage"]!["cache_read_input_tokens"]!.GetValue<int>());

        Assert.Equal("thinking", events[1].Data["content_block"]!["type"]!.GetValue<string>());
        Assert.Equal("plan", events[2].Data["delta"]!["thinking"]!.GetValue<string>());
        Assert.Equal("sig", events[3].Data["delta"]!["signature"]!.GetValue<string>());
        Assert.Equal(0, events[4].Data["index"]!.GetValue<int>());

        Assert.Equal("text", events[5].Data["content_block"]!["type"]!.GetValue<string>());
        Assert.Equal("Hello", events[6].Data["delta"]!["text"]!.GetValue<string>());
        Assert.Equal(1, events[7].Data["index"]!.GetValue<int>());

        Assert.Equal("toolu_1", events[8].Data["content_block"]!["id"]!.GetValue<string>());
        Assert.Equal("input_json_delta", events[9].Data["delta"]!["type"]!.GetValue<string>());
        Assert.Equal("""{"cmd": "ls"}""", events[9].Data["delta"]!["partial_json"]!.GetValue<string>());
        Assert.Equal(2, events[10].Data["index"]!.GetValue<int>());

        Assert.Equal("tool_use", events[11].Data["delta"]!["stop_reason"]!.GetValue<string>());
        Assert.Equal(5, events[11].Data["usage"]!["output_tokens"]!.GetValue<int>());
        Assert.Equal(10, events[11].Data["usage"]!["input_tokens"]!.GetValue<int>());
        Assert.Equal("event: message_stop\ndata: {\"type\": \"message_stop\"}\n\n", events[12].Format());
    }

    [Fact]
    public void error_bodies_use_the_anthropic_types()
    {
        Assert.Equal("""{"type": "error", "error": {"type": "not_found_error", "message": "nope"}}""", InspectAzureAI.Provider.Util.PythonJson.Dumps(AnthropicBridgeApi.ErrorBody(404, "nope")));
        Assert.Equal("api_error", AnthropicBridgeApi.ErrorBody(503, "x")["error"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void count_tokens_estimates_from_characters()
    {
        var count = AnthropicBridgeApi.CountTokens(Json("""{"model": "m", "system": "abcd", "messages": [{"role": "user", "content": "hello world"}]}"""));

        var tokens = count["input_tokens"]!.GetValue<long>();
        Assert.True(tokens > 5 && tokens < 30, tokens.ToString());
    }

    [Fact]
    public void unsigned_reasoning_degrades_to_text_blocks_like_the_anthropic_provider()
    {
        var message = new ChatMessageAssistant(
            new Content[] { new ContentReasoning("plan first"), new ContentReasoning("hidden", Redacted: true), new ContentReasoning("", "sig", Redacted: true), new ContentText("Done") });
        var output = new ModelOutput { Model = "gpt-5.4-mini", Choices = [new ChatCompletionChoice(message, StopReason.Stop)] };

        var content = AnthropicBridgeApi.ResponseFromOutput(output, "claude-sonnet-4-6")["content"];

        Assert.Equal(
            """[{"type": "text", "text": "<think>plan first</think>"}, {"type": "text", "text": "<think></think>"}, {"type": "redacted_thinking", "data": "sig"}, {"type": "text", "text": "Done"}]""",
            InspectAzureAI.Provider.Util.PythonJson.Dumps(content));
    }

    [Fact]
    public void an_error_tool_result_without_text_blocks_still_carries_a_message()
    {
        var parsed = AnthropicBridgeApi.ParseRequest(Json("""
            {"model": "m", "messages": [
              {"role": "assistant", "content": [{"type": "tool_use", "id": "toolu_1", "name": "shot", "input": {}}]},
              {"role": "user", "content": [{"type": "tool_result", "tool_use_id": "toolu_1", "is_error": true,
                 "content": [{"type": "image", "source": {"type": "url", "url": "http://x/y.png"}}]}]}
            ]}
            """));

        var result = Assert.IsType<ChatMessageTool>(parsed.Messages[1]);
        Assert.Equal("unknown", result.Error!.Type);
        Assert.Contains("\"type\": \"image\"", result.Error.Message);
    }
}

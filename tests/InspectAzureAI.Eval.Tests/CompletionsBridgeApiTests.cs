using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tests;

/// <summary>Port-level behaviour of <c>agent/_bridge/completions.py</c> (with <c>messages_from_openai</c> / <c>openai_chat_choices</c>) and the proxy's chat completions chunk stream.</summary>
public class CompletionsBridgeApiTests
{
    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void request_round_trips_messages_tools_tool_choice_and_config()
    {
        var parsed = CompletionsBridgeApi.ParseRequest(Json("""
            {
              "model": "gpt-5",
              "max_completion_tokens": 300,
              "temperature": 0.1,
              "stop": "END",
              "stream": true,
              "messages": [
                {"role": "developer", "content": "Be terse."},
                {"role": "user", "content": [{"type": "text", "text": "Look"}, {"type": "image_url", "image_url": {"url": "data:image/png;base64,AAAA", "detail": "low"}}]},
                {"role": "assistant", "content": "<think signature=\"s1\">plan</think>Running", "tool_calls": [{"id": "call_1", "type": "function", "function": {"name": "bash", "arguments": "{\"cmd\": \"ls\"}"}}]},
                {"role": "tool", "tool_call_id": "call_1", "content": "file.txt"}
              ],
              "tools": [{"type": "function", "function": {"name": "bash", "description": "Run", "parameters": {"type": "object", "properties": {"cmd": {"type": "string"}}, "required": ["cmd"]}}}],
              "tool_choice": {"type": "function", "function": {"name": "bash"}}
            }
            """));

        Assert.Equal("gpt-5", parsed.Model);
        Assert.True(parsed.Stream);
        Assert.Equal(["system", "user", "assistant", "tool"], parsed.Messages.Select(m => m.Role).ToArray());
        Assert.Equal("Be terse.", parsed.Messages[0].Text);
        var user = Assert.IsType<ChatMessageUser>(parsed.Messages[1]);
        Assert.Equal("Look", Assert.IsType<ContentText>(user.ContentList[0]).Text);
        Assert.Equal(("data:image/png;base64,AAAA", "low"), (Assert.IsType<ContentImage>(user.ContentList[1]).Image, Assert.IsType<ContentImage>(user.ContentList[1]).Detail));

        var assistant = Assert.IsType<ChatMessageAssistant>(parsed.Messages[2]);
        var reasoning = Assert.IsType<ContentReasoning>(assistant.ContentList[0]);
        Assert.Equal(("plan", "s1"), (reasoning.Reasoning, reasoning.Signature));
        Assert.Equal("Running", Assert.IsType<ContentText>(assistant.ContentList[1]).Text);
        var call = Assert.Single(assistant.ToolCalls!);
        Assert.Equal(("call_1", "bash", "ls"), (call.Id, call.Function, call.Arguments["cmd"]!.GetValue<string>()));
        Assert.Equal("gpt-5", assistant.Model);

        var tool = Assert.IsType<ChatMessageTool>(parsed.Messages[3]);
        Assert.Equal(("call_1", "bash", "file.txt"), (tool.ToolCallId, tool.Function, tool.Text));

        var toolInfo = Assert.Single(parsed.Tools);
        Assert.Equal(("bash", "Run"), (toolInfo.Name, toolInfo.Description));
        Assert.Equal(["cmd"], toolInfo.Parameters.Required);
        Assert.Equal("bash", Assert.IsType<ToolFunction>(parsed.ToolChoice).Name);

        Assert.Equal(300, parsed.Config.MaxTokens);
        Assert.Equal(0.1, parsed.Config.Temperature);
        Assert.Equal(["END"], parsed.Config.StopSeqs);
    }

    [Theory]
    [InlineData("\"auto\"", "auto")]
    [InlineData("\"none\"", "none")]
    [InlineData("\"required\"", "any")]
    public void tool_choice_strings_map_to_inspect_presets(string json, string expected) =>
        Assert.Equal(expected, CompletionsBridgeApi.ToolChoiceFromOpenAIToolChoice(JsonNode.Parse(json))!.ToString());

    [Fact]
    public void custom_tools_are_rejected()
    {
        Assert.Throws<BridgeRequestException>(() => CompletionsBridgeApi.ToolsFromOpenAITools(Json("""{"tools": [{"type": "custom", "custom": {"name": "x"}}]}""")["tools"]));
        Assert.Throws<BridgeRequestException>(() => CompletionsBridgeApi.ToolChoiceFromOpenAIToolChoice(Json("""{"type": "custom", "custom": {"name": "x"}}""")));
    }

    [Fact]
    public void split_assistant_turns_are_folded_back_together()
    {
        var messages = CompletionsBridgeApi.MessagesFromOpenAI(Json("""
            {"messages": [
              {"role": "user", "content": "hi"},
              {"role": "assistant", "content": null, "tool_calls": [{"id": "c1", "type": "function", "function": {"name": "f", "arguments": "{}"}}]},
              {"role": "tool", "tool_call_id": "c1", "content": "ok"},
              {"role": "assistant", "content": "after"}
            ]}
            """)["messages"]!.AsArray());

        Assert.Equal(["user", "assistant", "tool"], messages.Select(m => m.Role).ToArray());
        var assistant = Assert.IsType<ChatMessageAssistant>(messages[1]);
        Assert.Equal("after", assistant.Text);
        Assert.Single(assistant.ToolCalls!);
        Assert.Equal("f", Assert.IsType<ChatMessageTool>(messages[2]).Function);
    }

    [Fact]
    public void response_has_choices_tool_calls_finish_reason_and_usage()
    {
        var message = new ChatMessageAssistant(
            new Content[] { new ContentReasoning("plan", "s1"), new ContentText("Running") },
            toolCalls: [new ToolCall("call_1", "bash", new JsonObject { ["cmd"] = "ls" })]);
        var output = new ModelOutput
        {
            Model = "served",
            Choices = [new ChatCompletionChoice(message, StopReason.ToolCalls)],
            Usage = new ModelUsage(100, 20, 120) { InputTokensCacheRead = 40 },
        };

        var response = CompletionsBridgeApi.ResponseFromOutput(output, "served");

        Assert.Equal("chat.completion", response["object"]!.GetValue<string>());
        Assert.Equal("served", response["model"]!.GetValue<string>());
        var choice = Assert.Single(response["choices"]!.AsArray())!;
        Assert.Equal("tool_calls", choice["finish_reason"]!.GetValue<string>());
        Assert.Equal(0, choice["index"]!.GetValue<int>());
        Assert.Equal("\n<think signature=\"s1\">\nplan\n</think>\n\nRunning", choice["message"]!["content"]!.GetValue<string>());
        Assert.Equal(
            """[{"id": "call_1", "type": "function", "function": {"name": "bash", "arguments": "{\"cmd\": \"ls\"}"}}]""",
            PythonJson.Dumps(choice["message"]!["tool_calls"]));
        Assert.Equal(
            """{"completion_tokens": 20, "prompt_tokens": 140, "total_tokens": 120, "prompt_tokens_details": {"cached_tokens": 40, "cache_write_tokens": 0}}""",
            PythonJson.Dumps(response["usage"]));
    }

    [Theory]
    [InlineData(StopReason.Stop, "stop")]
    [InlineData(StopReason.ToolCalls, "tool_calls")]
    [InlineData(StopReason.ContentFilter, "content_filter")]
    [InlineData(StopReason.ModelLength, "length")]
    [InlineData(StopReason.MaxTokens, "stop")]
    [InlineData(StopReason.Unknown, "stop")]
    public void stop_reasons_map_to_finish_reasons(StopReason stopReason, string expected) =>
        Assert.Equal(expected, CompletionsBridgeApi.OpenAIFinishReason(stopReason));

    [Fact]
    public void stream_chunks_follow_the_proxy_rules()
    {
        var message = new ChatMessageAssistant("Running", toolCalls: [new ToolCall("call_1", "bash", new JsonObject { ["cmd"] = "ls" })]);
        var output = new ModelOutput
        {
            Model = "served",
            Choices = [new ChatCompletionChoice(message, StopReason.ToolCalls)],
            Usage = new ModelUsage(10, 5, 15),
        };
        var completion = CompletionsBridgeApi.ResponseFromOutput(output, "served");

        var chunks = CompletionsBridgeApi.StreamChunks(completion, includeUsage: true);

        Assert.Equal(6, chunks.Count);
        Assert.All(chunks, c => Assert.Equal("chat.completion.chunk", c["object"]!.GetValue<string>()));
        Assert.All(chunks, c => Assert.Equal(completion["id"]!.GetValue<string>(), c["id"]!.GetValue<string>()));
        Assert.Equal("assistant", chunks[0]["choices"]![0]!["delta"]!["role"]!.GetValue<string>());
        Assert.Equal("Running", chunks[1]["choices"]![0]!["delta"]!["content"]!.GetValue<string>());

        var nameDelta = chunks[2]["choices"]![0]!["delta"]!["tool_calls"]![0]!;
        Assert.Equal((0, "call_1", "function", "bash"), (nameDelta["index"]!.GetValue<int>(), nameDelta["id"]!.GetValue<string>(), nameDelta["type"]!.GetValue<string>(), nameDelta["function"]!["name"]!.GetValue<string>()));
        var argsDelta = chunks[3]["choices"]![0]!["delta"]!["tool_calls"]![0]!;
        Assert.Equal(("call_1", "function", """{"cmd": "ls"}"""), (argsDelta["id"]!.GetValue<string>(), argsDelta["type"]!.GetValue<string>(), argsDelta["function"]!["arguments"]!.GetValue<string>()));

        Assert.Empty(chunks[4]["choices"]![0]!["delta"]!.AsObject());
        Assert.Equal("tool_calls", chunks[4]["choices"]![0]!["finish_reason"]!.GetValue<string>());
        Assert.Empty(chunks[5]["choices"]!.AsArray());
        Assert.Equal(15, chunks[5]["usage"]!["total_tokens"]!.GetValue<int>());

        Assert.Equal(5, CompletionsBridgeApi.StreamChunks(completion, includeUsage: false).Count);
    }

    [Fact]
    public void error_bodies_use_the_openai_shape()
    {
        Assert.Equal("""{"error": {"message": "bad", "type": "invalid_request_error", "param": null, "code": null}}""", PythonJson.Dumps(CompletionsBridgeApi.ErrorBody(400, "bad")));
        Assert.Equal("api_error", CompletionsBridgeApi.ErrorBody(500, "x")["error"]!["type"]!.GetValue<string>());
    }
}

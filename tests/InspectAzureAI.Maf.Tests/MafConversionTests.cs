using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;
using Microsoft.Extensions.AI;
using ChatMessage = InspectAzureAI.Provider.Core.ChatMessage;
using MafChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace InspectAzureAI.Maf.Tests;

/// <summary>Both directions of the Microsoft.Extensions.AI ⇄ Inspect translation.</summary>
public class MafConversionTests
{
    private static JsonElement Element(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public void system_user_and_assistant_text_round_trips()
    {
        var maf = new List<MafChatMessage>
        {
            new(ChatRole.System, "Be terse."),
            new(ChatRole.User, "What is the capital of France?"),
            new(ChatRole.Assistant, "Paris."),
        };

        var inspect = MafConversion.ToInspectMessages(maf, instructions: null);

        Assert.Collection(
            inspect,
            message => Assert.Equal("Be terse.", Assert.IsType<ChatMessageSystem>(message).Text),
            message => Assert.Equal("What is the capital of France?", Assert.IsType<ChatMessageUser>(message).Text),
            message => Assert.Equal("Paris.", Assert.IsType<ChatMessageAssistant>(message).Text));
        var back = MafConversion.ToMafMessages(inspect);
        Assert.Equal(maf.Select(m => (m.Role, m.Text)), back.Select(m => (m.Role, m.Text)));
    }

    [Fact]
    public void instructions_lead_as_a_system_message()
    {
        var inspect = MafConversion.ToInspectMessages([new MafChatMessage(ChatRole.User, "hi")], "You are a pirate.");

        Assert.Equal(2, inspect.Count);
        Assert.Equal("You are a pirate.", Assert.IsType<ChatMessageSystem>(inspect[0]).Text);
        Assert.Empty(MafConversion.ToInspectMessages([], "   "));
    }

    [Fact]
    public void function_calls_become_tool_calls_and_results_become_tool_messages()
    {
        var call = new FunctionCallContent("call_1", "bash", new Dictionary<string, object?> { ["cmd"] = Element("\"ls -la\""), ["timeout"] = 30 });
        var maf = new List<MafChatMessage>
        {
            new(ChatRole.User, "list the files"),
            new(ChatRole.Assistant, [new TextContent("Listing."), call]),
            new(ChatRole.Tool, [new FunctionResultContent("call_1", "a.txt\nb.txt")]),
        };

        var inspect = MafConversion.ToInspectMessages(maf, null);

        var assistant = Assert.IsType<ChatMessageAssistant>(inspect[1]);
        Assert.Equal("Listing.", assistant.Text);
        var toolCall = Assert.Single(assistant.ToolCalls!);
        Assert.Equal("call_1", toolCall.Id);
        Assert.Equal("bash", toolCall.Function);
        Assert.Equal("ls -la", toolCall.Arguments["cmd"]!.GetValue<string>());
        Assert.Equal(30, toolCall.Arguments["timeout"]!.GetValue<int>());
        var result = Assert.IsType<ChatMessageTool>(inspect[2]);
        Assert.Equal("call_1", result.ToolCallId);
        Assert.Equal("bash", result.Function);
        Assert.Equal("a.txt\nb.txt", result.Text);
        Assert.Null(result.Error);

        var back = MafConversion.ToMafMessages(inspect);
        var backCall = Assert.Single(back[1].Contents.OfType<FunctionCallContent>());
        Assert.Equal("bash", backCall.Name);
        Assert.Equal("ls -la", Assert.IsType<JsonElement>(backCall.Arguments!["cmd"]).GetString());
        var backResult = Assert.Single(back[2].Contents.OfType<FunctionResultContent>());
        Assert.Equal("call_1", backResult.CallId);
        Assert.Equal("a.txt\nb.txt", backResult.Result);
    }

    [Fact]
    public void a_function_result_exception_becomes_a_tool_error()
    {
        var maf = new MafChatMessage(ChatRole.Tool, [new FunctionResultContent("call_1", "Error: Function failed.") { Exception = new InvalidOperationException("boom") }]);

        var result = Assert.IsType<ChatMessageTool>(Assert.Single(MafConversion.ToInspectMessages([maf], null)));

        Assert.Equal("boom", result.Error!.Message);
        Assert.Equal("Error: Function failed.", result.Text);
    }

    [Fact]
    public void structured_function_results_are_serialised_as_json()
    {
        Assert.Equal("", MafConversion.ResultText(null));
        Assert.Equal("plain", MafConversion.ResultText("plain"));
        Assert.Equal("text", MafConversion.ResultText(Element("\"text\"")));
        Assert.Equal("{\"a\":1}", MafConversion.ResultText(Element("{\"a\":1}")));
        Assert.Equal("[1,2]", MafConversion.ResultText(new[] { 1, 2 }));
    }

    [Fact]
    public void function_tools_carry_their_schema()
    {
        var function = AIFunctionFactory.Create((string cmd, int? timeout) => cmd, "bash", "Run a shell command.");

        var info = Assert.Single(MafConversion.ToToolInfos([function]));

        Assert.Equal("bash", info.Name);
        Assert.Equal("Run a shell command.", info.Description);
        Assert.Contains("cmd", info.Parameters.Properties.Keys);
        Assert.Contains("timeout", info.Parameters.Properties.Keys);
        Assert.Contains("string", info.Parameters.Properties["cmd"].Type!);
        Assert.Contains("cmd", info.Parameters.Required);
        Assert.Empty(MafConversion.ToToolInfos(null));
    }

    [Fact]
    public void a_non_function_tool_is_refused()
    {
        var error = Assert.Throws<NotSupportedException>(() => MafConversion.ToToolInfos([new OpaqueTool()]));

        Assert.Contains("opaque", error.Message);
    }

    [Fact]
    public void tool_modes_map_to_tool_choice()
    {
        Assert.Same(ToolChoice.Auto, MafConversion.ToToolChoice(null));
        Assert.Same(ToolChoice.Auto, MafConversion.ToToolChoice(ChatToolMode.Auto));
        Assert.Same(ToolChoice.None, MafConversion.ToToolChoice(ChatToolMode.None));
        Assert.Same(ToolChoice.Any, MafConversion.ToToolChoice(ChatToolMode.RequireAny));
        Assert.Equal("bash", Assert.IsType<ToolFunction>(MafConversion.ToToolChoice(ChatToolMode.RequireSpecific("bash"))).Name);
    }

    [Fact]
    public void chat_options_map_to_generate_config()
    {
        var config = MafConversion.ToGenerateConfig(new ChatOptions
        {
            Temperature = 0.5f,
            TopP = 0.9f,
            MaxOutputTokens = 256,
            StopSequences = ["END"],
            Seed = 42,
            AllowMultipleToolCalls = false,
        });

        Assert.Equal(0.5, config.Temperature!.Value, 6);
        Assert.Equal(0.9, config.TopP!.Value, 6);
        Assert.Equal(256, config.MaxTokens);
        Assert.Equal(["END"], config.StopSeqs);
        Assert.Equal(42, config.Seed);
        Assert.False(config.ParallelToolCalls);
        Assert.Null(MafConversion.ToGenerateConfig(null).Temperature);
    }

    [Fact]
    public void a_model_output_becomes_a_response_with_function_calls()
    {
        var arguments = new JsonObject { ["text"] = "hi", ["times"] = 2 };
        var message = new ChatMessageAssistant("Shouting.", [new ToolCall("call_9", "shout", arguments)]) { Id = "msg_1" };
        var output = new ModelOutput
        {
            Model = "scripted",
            Choices = [new ChatCompletionChoice(message, StopReason.ToolCalls)],
            Usage = new ModelUsage(10, 5, 15) { InputTokensCacheRead = 4 },
        };

        var response = MafConversion.ToChatResponse(output);

        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
        Assert.Equal("scripted", response.ModelId);
        Assert.Equal("msg_1", response.ResponseId);
        Assert.Equal(10, response.Usage!.InputTokenCount);
        Assert.Equal(5, response.Usage.OutputTokenCount);
        Assert.Equal(4, response.Usage.CachedInputTokenCount);
        Assert.Equal("Shouting.", response.Text);
        var call = Assert.Single(response.Messages[0].Contents.OfType<FunctionCallContent>());
        Assert.Equal("call_9", call.CallId);
        Assert.Equal("shout", call.Name);
        Assert.Equal("hi", Assert.IsType<JsonElement>(call.Arguments!["text"]).GetString());
        Assert.Equal(2, Assert.IsType<JsonElement>(call.Arguments["times"]).GetInt32());
        Assert.Same(output, response.RawRepresentation);
    }

    [Fact]
    public void an_output_without_choices_is_an_error()
    {
        var error = Assert.Throws<InvalidOperationException>(() => MafConversion.ToChatResponse(new ModelOutput { Model = "m", Choices = [], Error = "rate limited" }));

        Assert.Contains("rate limited", error.Message);
    }

    [Theory]
    [InlineData(StopReason.Stop, "stop")]
    [InlineData(StopReason.MaxTokens, "length")]
    [InlineData(StopReason.ModelLength, "length")]
    [InlineData(StopReason.ToolCalls, "tool_calls")]
    [InlineData(StopReason.ContentFilter, "content_filter")]
    public void stop_reasons_map_to_finish_reasons(StopReason stopReason, string expected)
    {
        Assert.Equal(expected, MafConversion.ToFinishReason(stopReason)!.Value.Value);
        Assert.Null(MafConversion.ToFinishReason(StopReason.Unknown));
    }

    [Fact]
    public void media_content_maps_both_ways()
    {
        const string image = "data:image/png;base64,iVBORw0KGgo=";
        var maf = new MafChatMessage(ChatRole.User, [new TextContent("What is this?"), new DataContent(image, "image/png")]);

        var user = Assert.IsType<ChatMessageUser>(Assert.Single(MafConversion.ToInspectMessages([maf], null)));

        Assert.Collection(
            user.Content.Items!,
            item => Assert.Equal("What is this?", Assert.IsType<ContentText>(item).Text),
            item => Assert.Equal(image, Assert.IsType<ContentImage>(item).Image));
        var back = Assert.Single(MafConversion.ToMafMessages([user]));
        var data = Assert.Single(back.Contents.OfType<DataContent>());
        Assert.Equal("image/png", data.MediaType);
        Assert.Equal(image, data.Uri);
        var remote = Assert.Single(MafConversion.ToMafMessages([new ChatMessageUser(new Content[] { new ContentImage("https://example.com/a.png") })]));
        Assert.Equal("https://example.com/a.png", Assert.Single(remote.Contents.OfType<UriContent>()).Uri.ToString());
    }

    [Fact]
    public void reasoning_signatures_and_redacted_blocks_round_trip()
    {
        var maf = new MafChatMessage(ChatRole.Assistant, [new TextReasoningContent("thinking") { ProtectedData = "sig" }, new TextReasoningContent("") { ProtectedData = "blob" }, new TextContent("Paris")]);

        var assistant = Assert.IsType<ChatMessageAssistant>(Assert.Single(MafConversion.ToInspectMessages([maf], null)));

        Assert.Collection(
            assistant.Content.Items!,
            item => Assert.Equal(("thinking", "sig", false), Reasoning(item)),
            item => Assert.Equal(("", "blob", true), Reasoning(item)),
            item => Assert.Equal("Paris", Assert.IsType<ContentText>(item).Text));
        var back = Assert.Single(MafConversion.ToMafMessages([assistant]));
        var reasoning = back.Contents.OfType<TextReasoningContent>().ToList();
        Assert.Equal(("thinking", "sig"), (reasoning[0].Text, reasoning[0].ProtectedData));
        Assert.Equal(("", "blob"), (reasoning[1].Text, reasoning[1].ProtectedData));

        static (string, string?, bool) Reasoning(Content item)
        {
            var reasoning = Assert.IsType<ContentReasoning>(item);
            return (reasoning.Reasoning, reasoning.Signature, reasoning.Redacted);
        }
    }

    [Fact]
    public void a_json_schema_response_format_becomes_a_response_schema_and_json_mode_is_refused()
    {
        var schema = Element("{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"]}");

        var config = MafConversion.ToGenerateConfig(new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema(schema, "answer", "The answer.") });

        Assert.Equal("answer", config.ResponseSchema!.Name);
        Assert.Equal("The answer.", config.ResponseSchema.Description);
        Assert.NotNull(config.ResponseSchema.JsonSchema);
        Assert.Null(MafConversion.ToGenerateConfig(new ChatOptions { ResponseFormat = ChatResponseFormat.Text }).ResponseSchema);
        Assert.Throws<NotSupportedException>(() => MafConversion.ToGenerateConfig(new ChatOptions { ResponseFormat = ChatResponseFormat.Json }));
        Assert.Throws<NotSupportedException>(() => MafConversion.ToGenerateConfig(new ChatOptions { Seed = long.MaxValue }));
    }

    [Fact]
    public void tool_errors_and_media_results_round_trip_as_the_tool_message()
    {
        var timedOut = new ChatMessageTool("partial", toolCallId: "call_1", function: "bash", error: new ToolCallError("timeout", "Command timed out before completing."));
        var shot = new ChatMessageTool(new Content[] { new ContentImage("data:image/png;base64,AA==") }, toolCallId: "call_2", function: "screenshot");
        var calls = new ChatMessageAssistant("", [new ToolCall("call_1", "bash", new JsonObject()), new ToolCall("call_2", "screenshot", new JsonObject())]);

        var back = MafConversion.ToInspectMessages(MafConversion.ToMafMessages([calls, timedOut, shot]), null);

        var error = Assert.IsType<ChatMessageTool>(back[1]);
        Assert.Equal(("call_1", "bash", "timeout", "partial"), (error.ToolCallId, error.Function, error.Error!.Type, error.Text));
        var media = Assert.IsType<ChatMessageTool>(back[2]);
        Assert.Equal("data:image/png;base64,AA==", Assert.IsType<ContentImage>(Assert.Single(media.Content.Items!)).Image);
        Assert.Equal("Error: Command timed out before completing.", MafConversion.ResultText(timedOut));
    }

    [Fact]
    public void unsupported_content_is_refused_rather_than_dropped()
    {
        var maf = new MafChatMessage(ChatRole.User, [new OpaqueContent()]);

        Assert.Throws<NotSupportedException>(() => MafConversion.ToInspectMessages([maf], null));
        Assert.Throws<NotSupportedException>(() => MafConversion.ToMafMessages([new ChatMessageUser(new Content[] { new ContentToolUse("web_search", "id", "search", "{}", "result") })]));
    }

    private sealed class OpaqueTool : AITool
    {
        public override string Name => "opaque";
    }

    private sealed class OpaqueContent : AIContent;
}

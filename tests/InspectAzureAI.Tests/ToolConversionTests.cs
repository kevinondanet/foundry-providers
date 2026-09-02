using System.ClientModel.Primitives;
using System.Text.Encodings.Web;
using System.Text.Json;
using Azure.AI.Inference;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Tools;

namespace InspectAzureAI.Tests;

public class ToolConversionTests
{
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    public void chat_tool_definition_strips_extended_fields_recursively()
    {
        var tool = new ToolInfo("f", "desc")
        {
            Parameters = new ToolParams
            {
                Properties = new Dictionary<string, ToolParam>
                {
                    ["city"] = new() { Type = ["string"], MinLength = 1, Pattern = "^.+$", Description = "City" },
                    ["tags"] = new() { Type = ["array"], Items = new ToolParam { Type = ["string"], MaxLength = 5 } },
                    ["choice"] = new() { AnyOf = [new ToolParam { Type = ["integer"], Minimum = 0 }, new ToolParam { Type = ["string"] }] },
                },
                Required = ["city"],
            },
        };

        var definition = AzureToolConversion.ChatToolDefinition(tool);
        Assert.Equal(
            """{"type":"function","function":{"name":"f","description":"desc","parameters":{"type":"object","properties":{"city":{"type":"string","description":"City"},"tags":{"type":"array","items":{"type":"string"}},"choice":{"anyOf":[{"type":"integer"},{"type":"string"}]}},"required":["city"],"additionalProperties":false}}}""",
            AzureMessageConversion.AsDict(definition).ToJsonString());
    }

    [Fact]
    public void chat_tool_choice_mapping()
    {
        Assert.Same(ChatCompletionsToolChoice.Auto, AzureToolConversion.ChatToolChoice(ToolChoice.Auto));
        Assert.Same(ChatCompletionsToolChoice.None, AzureToolConversion.ChatToolChoice(ToolChoice.None));
        Assert.Same(ChatCompletionsToolChoice.Required, AzureToolConversion.ChatToolChoice(ToolChoice.Any));

        var options = new ChatCompletionsOptions { ToolChoice = AzureToolConversion.ChatToolChoice(new ToolFunction("f")) };
        options.Messages.Add(new ChatRequestUserMessage("hi"));
        var body = ModelReaderWriter.Write(options).ToString();
        Assert.Contains("""
                        "tool_choice":{"type":"function","function":{"name":"f"}}
                        """.Trim(), body);
        options.ToolChoice = ChatCompletionsToolChoice.Required;
        Assert.Contains("\"tool_choice\":\"required\"", ModelReaderWriter.Write(options).ToString());
    }

    [Fact]
    public void chat_request_message_per_role()
    {
        static string Dict(ChatRequestMessage m) => AzureMessageConversion.AsDict(m).ToJsonString(Relaxed);

        Assert.Equal("""{"role":"system","content":"s"}""", Dict(AzureMessageConversion.ChatRequestMessage(new ChatMessageSystem("s"), null)));
        Assert.Equal(
            """{"role":"user","content":[{"type":"text","text":"t"},{"type":"image_url","image_url":{"url":"data:image/png;base64,AAAA","detail":"auto"}}]}""",
            Dict(AzureMessageConversion.ChatRequestMessage(
                new ChatMessageUser(new Content[] { new ContentText("t"), new ContentImage("data:image/png;base64,AAAA") }), null)));
        Assert.Equal(
            """{"role":"tool","content":"Error: boom","tool_call_id":"c1"}""",
            Dict(AzureMessageConversion.ChatRequestMessage(new ChatMessageTool("ignored", "c1", error: new ToolCallError("unknown", "boom")), null)));
        Assert.Equal(
            """{"role":"tool","content":"sunny","tool_call_id":"c1"}""",
            Dict(AzureMessageConversion.ChatRequestMessage(new ChatMessageTool("sunny", "c1"), null)));
        Assert.Equal(
            """{"role":"tool","content":"x","tool_call_id":"None"}""",
            Dict(AzureMessageConversion.ChatRequestMessage(new ChatMessageTool("x"), null)));

        var assistantWithCall = new ChatMessageAssistant("", [new ToolCall("c1", "f", new() { ["a"] = 1 })]);
        Assert.Equal(
            """{"role":"assistant","tool_calls":[{"id":"c1","type":"function","function":{"name":"f","arguments":"{\"a\": 1}"}}]}""",
            Dict(AzureMessageConversion.ChatRequestMessage(assistantWithCall, null)));
        Assert.Equal(
            """{"role":"assistant","content":"<tool_call>{\"name\": \"f\", \"arguments\": {\"a\": 1} }</tool_call>"}""",
            Dict(AzureMessageConversion.ChatRequestMessage(assistantWithCall, new Llama31Handler("m"))));
        Assert.Equal("""{"role":"assistant","content":"hi"}""", Dict(AzureMessageConversion.ChatRequestMessage(new ChatMessageAssistant("hi"), null)));
    }

    [Fact]
    public void chat_content_item_rejects_audio_and_video()
    {
        var audio = Assert.Throws<InvalidOperationException>(() => AzureMessageConversion.ChatContentItem(new ContentAudio("data:audio/wav;base64,AAAA", "wav")));
        Assert.Equal("Azure AI models do not support audio or video inputs.", audio.Message);
        var video = Assert.Throws<InvalidOperationException>(() => AzureMessageConversion.ChatContentItem(new ContentVideo("data:video/mp4;base64,AAAA", "mp4")));
        Assert.Equal("Azure AI models do not support audio or video inputs.", video.Message);
    }

    [Fact]
    public void chat_content_item_requires_inline_images()
    {
        Assert.Throws<UnresolvedMediaError>(() => AzureMessageConversion.ChatContentItem(new ContentImage("https://x/y.png")));
        Assert.Throws<UnresolvedMediaError>(() => AzureMessageConversion.ChatContentItem(new ContentImage("file.png")));
        var bad = Assert.Throws<ArgumentException>(() => AzureMessageConversion.ChatContentItem(new ContentImage("data:text/plain;base64,QUJD")));
        Assert.Equal("Inline image media has incompatible MIME type 'text/plain'.", bad.Message);

        // a data URI without a declared mime type is sniffed and re-prefixed
        var png = Convert.ToBase64String("\x89PNG\r\n\x1a\n0000"u8.ToArray());
        var item = AzureMessageConversion.ChatContentItem(new ContentImage($"data:;base64,{png}", "high"));
        Assert.Equal($"data:image/png;base64,{png}", AzureMessageConversion.ImageUrlOf((ChatMessageImageContentItem)item));

        // large payloads pass through the Uri unchanged
        var big = Convert.ToBase64String(new byte[200_000]);
        var bigItem = AzureMessageConversion.ChatContentItem(new ContentImage($"data:image/jpeg;base64,{big}"));
        Assert.Equal($"data:image/jpeg;base64,{big}", AzureMessageConversion.ImageUrlOf((ChatMessageImageContentItem)bigItem));
    }

    [Fact]
    public void mistral_reducer_folds_user_after_tool()
    {
        var messages = new ChatMessage[]
        {
            new ChatMessageTool("result", "c1"),
            new ChatMessageUser(new Content[] { new ContentText("more"), new ContentImage("data:image/png;base64,AAAA") }),
            new ChatMessageUser("again"),
            new ChatMessageAssistant("ok"),
            new ChatMessageUser("not folded"),
        };
        var folded = AzureMessageConversion.ChatRequestMessages(messages, null, isMistral: true);
        Assert.Equal(3, folded.Count);
        var tool = Assert.IsType<ChatRequestToolMessage>(folded[0]);
        Assert.Equal("resultmore[Image: data:image/png;base64,AAAA]again", tool.Content);
        Assert.Equal("c1", tool.ToolCallId);
        Assert.IsType<ChatRequestAssistantMessage>(folded[1]);
        Assert.IsType<ChatRequestUserMessage>(folded[2]);

        var notMistral = AzureMessageConversion.ChatRequestMessages(messages, null, isMistral: false);
        Assert.Equal(5, notMistral.Count);
    }

    [Fact]
    public void message_text_joins_text_items_and_drops_media()
    {
        var user = new ChatMessageUser(new Content[] { new ContentText("a"), new ContentImage("data:image/png;base64,AAAA"), new ContentText("b") });
        Assert.Equal("a\nb", user.Text);
        Assert.Equal("plain", new ChatMessageUser("plain").Text);
        Assert.NotNull(user.Id);
    }
}

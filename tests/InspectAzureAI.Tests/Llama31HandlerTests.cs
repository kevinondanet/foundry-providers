using System.Text.RegularExpressions;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Tools;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Tests;

public class Llama31HandlerTests
{
    private static readonly Llama31Handler Handler = new("m");
    private static readonly ToolInfo[] Tools = [Fixtures.WeatherTool];

    private const string ExpectedPromptWithTool =
        "\n            You are a knowledgable assistant. You can answer questions and perform tasks. You are provided with function signatures within <tools></tools> XML tags. You may call one or more functions to assist with the user query. Don't make assumptions about what values to plug into functions. For each function call return a json object with function name and arguments within <tool_call></tool_call> XML tags as follows:\n\n            <tool_call>\n            {\"name\": <function-name>,\"arguments\": <args-dict>}\n            </tool_call>\n\n            Here are the available tools defined in JSON Schema:\n\n            <tools>\n            {\n  \"name\": \"get_weather\",\n  \"description\": \"Get weather.\",\n  \"parameters\": {\n    \"type\": \"object\",\n    \"properties\": {\n      \"city\": {\n        \"type\": \"string\",\n        \"description\": \"City\",\n        \"minLength\": 1\n      }\n    },\n    \"required\": [\n      \"city\"\n    ],\n    \"additionalProperties\": false\n  }\n}\n            </tools>\n\n            Reminder:\n            - Function calls MUST follow the specified format, start with <tool_call> and end with </tool_call>.\n            - Please call only one function at a time.\n            - It's fine to include some reasoning about which function to call and why.\n            - Please ensure that </tool_call> is the last content in the message (there should be no text after it).\n            - Please be absolutely sure that the function name you have specified matches one of the functions described in <tools>.\n            - All function parameters MUST be specified.\n            - If there is no function call available, answer the question like normal with your current knowledge and do not tell the user about function calls\n";

    private const string ExpectedPromptNoTools =
        "\nYou are a knowledgable assistant. You can answer questions and perform tasks. You are provided with function signatures within <tools></tools> XML tags. You may call one or more functions to assist with the user query. Don't make assumptions about what values to plug into functions. For each function call return a json object with function name and arguments within <tool_call></tool_call> XML tags as follows:\n\n<tool_call>\n{\"name\": <function-name>,\"arguments\": <args-dict>}\n</tool_call>\n\nHere are the available tools defined in JSON Schema:\n\n<tools>\n\n</tools>\n\nReminder:\n- Function calls MUST follow the specified format, start with <tool_call> and end with </tool_call>.\n- Please call only one function at a time.\n- It's fine to include some reasoning about which function to call and why.\n- Please ensure that </tool_call> is the last content in the message (there should be no text after it).\n- Please be absolutely sure that the function name you have specified matches one of the functions described in <tools>.\n- All function parameters MUST be specified.\n- If there is no function call available, answer the question like normal with your current knowledge and do not tell the user about function calls\n";

    [Fact]
    public void input_with_tools_prepends_verbatim_system_prompt()
    {
        var user = new ChatMessageUser("hi");
        var result = Handler.InputWithTools([user], Tools);
        Assert.Equal(2, result.Count);
        var system = Assert.IsType<ChatMessageSystem>(result[0]);
        Assert.Equal(ExpectedPromptWithTool, system.Text);
        Assert.Same(user, result[1]);
    }

    [Fact]
    public void input_with_tools_dedents_fully_without_tools()
    {
        var result = Handler.InputWithTools([], []);
        Assert.Equal(ExpectedPromptNoTools, Assert.Single(result).Text);
    }

    [Fact]
    public void parse_single_tool_call_with_surrounding_text()
    {
        var message = Handler.ParseAssistantResponse(
            "Let me check.<tool_call>{\"name\": \"get_weather\", \"arguments\": {\"city\": \"Paris\"}}</tool_call>trailing", Tools);
        Assert.Equal("Let me check.\n\ntrailing", message.Text);
        Assert.Equal("m", message.Model);
        Assert.Equal("generate", message.Source);
        var call = Assert.Single(message.ToolCalls!);
        Assert.Equal("get_weather", call.Function);
        Assert.Equal("""{"city":"Paris"}""", call.Arguments.ToJsonString());
        Assert.Null(call.ParseError);
        Assert.Matches($"^get_weather_[{ShortUuid.Alphabet}]{{22}}$", call.Id);
    }

    [Fact]
    public void parse_tool_call_with_duplicate_keys_keeps_the_last_value()
    {
        var message = Handler.ParseAssistantResponse(
            "<tool_call>{\"name\": \"nope\", \"name\": \"get_weather\", \"arguments\": {\"city\": \"Paris\", \"city\": \"Lyon\"}}</tool_call>", Tools);
        var call = Assert.Single(message.ToolCalls!);
        Assert.Equal("get_weather", call.Function);
        Assert.Equal("""{"city":"Lyon"}""", call.Arguments.ToJsonString());
        Assert.Null(call.ParseError);
    }

    [Fact]
    public void parse_plural_tag_and_multiple_calls()
    {
        var message = Handler.ParseAssistantResponse(
            "<tool_calls>{\"name\": \"get_weather\", \"arguments\": \"Paris\"}</tool_calls>\n<tool_call>{\"name\": \"get_weather\", \"arguments\": {\"city\": \"Rome\"}}</tool_call>",
            Tools);
        Assert.Equal("", message.Text);
        Assert.Equal(2, message.ToolCalls!.Count);
        Assert.Equal("""{"city":"Paris"}""", message.ToolCalls[0].Arguments.ToJsonString());
        Assert.Equal("""{"city":"Rome"}""", message.ToolCalls[1].Arguments.ToJsonString());
        Assert.All(message.ToolCalls, c => Assert.Null(c.ParseError));
    }

    [Fact]
    public void parse_malformed_tool_call_json()
    {
        var message = Handler.ParseAssistantResponse("<tool_call>not json</tool_call>", Tools);
        Assert.Equal("", message.Text);
        var call = Assert.Single(message.ToolCalls!);
        Assert.Equal("unknown", call.Id);
        Assert.Equal("unknown", call.Function);
        Assert.Equal("{}", call.Arguments.ToJsonString());
        Assert.StartsWith("Error parsing the following tool call arguments:\n\nnot json\n\nError details: ", call.ParseError);
    }

    [Theory]
    [InlineData("[1,2]", "The provided arguments are not a JSON dictionary.")]
    [InlineData("{\"name\": \"get_weather\"}", "Required 'name' and 'arguments' not provided in JSON dictionary.")]
    [InlineData("{\"name\": \"\", \"arguments\": {}}", "Required 'name' and 'arguments' not provided in JSON dictionary.")]
    public void parse_non_dict_and_missing_field_payloads(string payload, string detail)
    {
        var message = Handler.ParseAssistantResponse($"<tool_call>{payload}</tool_call>", Tools);
        var call = Assert.Single(message.ToolCalls!);
        Assert.Equal("unknown", call.Id);
        Assert.Equal("unknown", call.Function);
        Assert.Equal($"Error parsing the following tool call arguments:\n\n{payload}\n\nError details: {detail}", call.ParseError);
    }

    [Fact]
    public void parse_no_tool_call_filters_assistant_header()
    {
        var message = Handler.ParseAssistantResponse("<|start_header_id|>assistant<|end_header_id|>\n\nHello", Tools);
        Assert.Equal("\n\nHello", message.Text);
        Assert.Null(message.ToolCalls);
        Assert.Equal("m", message.Model);
        Assert.Equal("generate", message.Source);
    }

    [Fact]
    public void assistant_message_renders_tool_calls()
    {
        var call = new ToolCall("x", "get_weather", new() { ["city"] = "Paris" });
        Assert.Equal(
            new ChatApiMessage("assistant", "Checking\n\n<tool_call>{\"name\": \"get_weather\", \"arguments\": {\"city\": \"Paris\"} }</tool_call>"),
            Handler.AssistantMessage(new ChatMessageAssistant("Checking", [call])));
        Assert.Equal(
            new ChatApiMessage("assistant", "<tool_call>{\"name\": \"get_weather\", \"arguments\": {\"city\": \"Paris\"} }</tool_call>"),
            Handler.AssistantMessage(new ChatMessageAssistant("", [call])));
        Assert.Equal(new ChatApiMessage("assistant", "plain"), Handler.AssistantMessage(new ChatMessageAssistant("plain")));
    }

    [Fact]
    public void assistant_message_uses_python_json_dumps_conventions()
    {
        var call = new ToolCall("x", "get_weather", new() { ["city"] = "Paris", ["n"] = 1.0, ["u"] = "é" });
        Assert.Equal(
            "<tool_call>{\"name\": \"get_weather\", \"arguments\": {\"city\": \"Paris\", \"n\": 1.0, \"u\": \"\\u00e9\"} }</tool_call>",
            Handler.AssistantMessage(new ChatMessageAssistant("", [call])).Content);
    }

    [Fact]
    public void tool_message_renders_errors()
    {
        Assert.Equal(new ChatApiMessage("tool", "Error: boom"), Handler.ToolMessage(new ChatMessageTool("x", error: new ToolCallError("unknown", "boom"))));
        Assert.Equal(new ChatApiMessage("tool", "ok"), Handler.ToolMessage(new ChatMessageTool("ok")));
    }

    [Fact]
    public void base_handler_defaults()
    {
        var handler = new ChatApiHandler("base");
        var input = new ChatMessage[] { new ChatMessageUser("hi") };
        Assert.Same(input, handler.InputWithTools(input, Tools));
        var parsed = handler.ParseAssistantResponse("text", Tools);
        Assert.Equal("text", parsed.Text);
        Assert.Equal("base", parsed.Model);
        Assert.Equal("generate", parsed.Source);
    }

    [Fact]
    public void dedent_matches_python()
    {
        Assert.Equal("a\n  b\n", TextWrap.Dedent("    a\n      b\n    "));
        Assert.Equal("  a\n\nb\n", TextWrap.Dedent("  a\n   \nb\n"));
        Assert.Matches(new Regex("^\n"), TextWrap.Dedent("\n    x"));
    }
}

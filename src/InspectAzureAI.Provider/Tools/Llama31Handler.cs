using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Provider.Tools;

/// <summary>
/// Tool-calling emulation for models without native tool support (port of <c>Llama31Handler</c> in
/// <c>src/inspect_ai/model/_providers/util/llama31.py</c>). Tools are described in a system prompt and
/// the model answers with <c>&lt;tool_call&gt;{"name": ..., "arguments": ...}&lt;/tool_call&gt;</c> blocks.
/// </summary>
public sealed partial class Llama31Handler(string model) : ChatApiHandler(model)
{
    /// <summary>Port of <c>TOOL_CALL</c>.</summary>
    public const string ToolCallTag = "tool_call";

    [GeneratedRegex(@"<tool_calls?>((?:.|\n)*?)</tool_calls?>")]
    private static partial Regex ToolCallCapture();

    [GeneratedRegex(@"<tool_calls?>(?:.|\n)*?</tool_calls?>", RegexOptions.Singleline)]
    private static partial Regex ToolCallSplit();

    [GeneratedRegex(@"<\|start_header_id\|>assistant<\|end_header_id\|>")]
    private static partial Regex AssistantHeader();

    /// <summary>Prepends the tool-definition system prompt (verbatim port of the Python template, including <c>textwrap.dedent</c>).</summary>
    public override IReadOnlyList<ChatMessage> InputWithTools(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools)
    {
        var availableTools = string.Join("\n\n", tools.Select(tool => PythonJson.Dumps(tool.ToJson(), indent: 2)));
        var toolPrompt = TextWrap.Dedent(string.Join("\n",
        [
            "",
            $"            You are a knowledgable assistant. You can answer questions and perform tasks. You are provided with function signatures within <tools></tools> XML tags. You may call one or more functions to assist with the user query. Don't make assumptions about what values to plug into functions. For each function call return a json object with function name and arguments within <{ToolCallTag}></{ToolCallTag}> XML tags as follows:",
            "",
            $"            <{ToolCallTag}>",
            "            {\"name\": <function-name>,\"arguments\": <args-dict>}",
            $"            </{ToolCallTag}>",
            "",
            "            Here are the available tools defined in JSON Schema:",
            "",
            "            <tools>",
            $"            {availableTools}",
            "            </tools>",
            "",
            "            Reminder:",
            $"            - Function calls MUST follow the specified format, start with <{ToolCallTag}> and end with </{ToolCallTag}>.",
            "            - Please call only one function at a time.",
            "            - It's fine to include some reasoning about which function to call and why.",
            $"            - Please ensure that </{ToolCallTag}> is the last content in the message (there should be no text after it).",
            "            - Please be absolutely sure that the function name you have specified matches one of the functions described in <tools>.",
            "            - All function parameters MUST be specified.",
            "            - If there is no function call available, answer the question like normal with your current knowledge and do not tell the user about function calls",
            "            ",
        ]));

        return [new ChatMessageSystem(toolPrompt), .. input];
    }

    /// <summary>
    /// Extracts every <c>&lt;tool_call&gt;</c> block (plural tags accepted), parses each into a
    /// <see cref="ToolCall"/> (parse failures land in <see cref="ToolCall.ParseError"/>), and joins the
    /// remaining text with blank lines.
    /// </summary>
    public override ChatMessageAssistant ParseAssistantResponse(string response, IReadOnlyList<ToolInfo> tools)
    {
        var toolCallsContent = ToolCallCapture().Matches(response).Select(m => m.Groups[1].Value).ToList();
        if (toolCallsContent.Count > 0)
        {
            var toolCalls = toolCallsContent.Select(content => ParseToolCallContent(content, tools)).ToList();
            var otherContent = ToolCallSplit().Split(response)
                .Select(c => c.Trim())
                .Where(c => c.Length > 0);
            var content = string.Join("\n\n", otherContent);
            return new ChatMessageAssistant(FilterAssistantHeader(content), toolCalls, Model, "generate");
        }

        return new ChatMessageAssistant(FilterAssistantHeader(response), model: Model, source: "generate");
    }

    /// <summary>Renders assistant tool calls back into <c>&lt;tool_call&gt;</c> text for the conversation history.</summary>
    public override ChatApiMessage AssistantMessage(ChatMessageAssistant message)
    {
        string content;
        if (message.ToolCalls is { Count: > 0 })
        {
            var parts = new List<string> { message.Text };
            parts.AddRange(message.ToolCalls.Select(tool =>
                $"<tool_call>{{\"name\": \"{tool.Function}\", \"arguments\": {PythonJson.Dumps(tool.Arguments)} }}</tool_call>"));
            content = string.Join("\n\n", parts).Trim();
        }
        else
        {
            content = message.Text;
        }

        return new ChatApiMessage("assistant", content);
    }

    /// <summary>Port of <c>Llama31Handler.tool_message</c> (identical to the base class).</summary>
    public override ChatApiMessage ToolMessage(ChatMessageTool message) =>
        new("tool", message.Error is not null ? $"Error: {message.Error.Message}" : message.Text);

    /// <summary>
    /// Port of <c>parse_tool_call_content</c>: the block must be a JSON object with a non-empty
    /// <c>name</c> and a non-null <c>arguments</c>; any failure yields the <c>unknown</c> tool call with a
    /// <see cref="ToolCall.ParseError"/>.
    /// </summary>
    public static ToolCall ParseToolCallContent(string content, IReadOnlyList<ToolInfo> tools)
    {
        try
        {
            var toolCallData = PythonJson.Loads(content, ToolCallParsing.ParserMaxDepth);
            if (toolCallData is not JsonObject data)
            {
                throw new ArgumentException("The provided arguments are not a JSON dictionary.");
            }

            var name = data["name"];
            var arguments = data["arguments"];
            if (!PythonSemantics.Truthy(name) || arguments is null)
            {
                throw new ArgumentException("Required 'name' and 'arguments' not provided in JSON dictionary.");
            }

            var functionName = name!.ToString();
            var uniqueId = $"{functionName}_{ShortUuid.Generate()}";
            return ToolCallParsing.ParseToolCall(uniqueId, functionName, PythonJson.Dumps(arguments), tools);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            var parseError = ToolCallParsing.ToolParseErrorMessage(content, ex);
            ProviderLogger.Info(parseError);
            return new ToolCall("unknown", "unknown", new JsonObject()) { ParseError = parseError };
        }
    }

    /// <summary>Port of <c>filter_assistant_header</c>.</summary>
    public static string FilterAssistantHeader(string message) => AssistantHeader().Replace(message, "");
}

using Azure.AI.Inference;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Provider.Tools;

/// <summary>Native tool conversion (port of <c>chat_tools</c>, <c>chat_tool_definition</c>, <c>chat_tool_choice</c>, <c>chat_tool_call</c> in <c>azureai.py</c>).</summary>
public static class AzureToolConversion
{
    /// <summary>Port of <c>chat_tools</c>.</summary>
    public static List<ChatCompletionsToolDefinition> ChatTools(IReadOnlyList<ToolInfo> tools) =>
        tools.Select(ChatToolDefinition).ToList();

    /// <summary>
    /// Port of <c>chat_tool_definition</c>: the function definition carries the parameters schema with
    /// <see cref="JsonSchemaDump.JsonSchemaExtendedFields"/> stripped recursively.
    /// </summary>
    public static ChatCompletionsToolDefinition ChatToolDefinition(ToolInfo tool)
    {
        var parameters = JsonSchemaDump.Dump(tool.Parameters.ToJson(), JsonSchemaDump.JsonSchemaExtendedFields);
        var function = new FunctionDefinition(tool.Name)
        {
            Description = tool.Description,
            Parameters = BinaryData.FromString(parameters.ToJsonString()),
        };
        return new ChatCompletionsToolDefinition(function);
    }

    /// <summary>
    /// Port of <c>chat_tool_choice</c>: <c>auto</c>/<c>none</c>/<c>any</c> map to the SDK presets
    /// (<c>any</c> is sent as <c>required</c>); a <see cref="ToolFunction"/> becomes a named choice.
    /// </summary>
    public static ChatCompletionsToolChoice ChatToolChoice(ToolChoice toolChoice)
    {
        if (toolChoice is ToolFunction function)
        {
            return new ChatCompletionsToolChoice(new FunctionDefinition(function.Name));
        }

        if (ReferenceEquals(toolChoice, ToolChoice.Auto))
        {
            return ChatCompletionsToolChoice.Auto;
        }

        if (ReferenceEquals(toolChoice, ToolChoice.None))
        {
            return ChatCompletionsToolChoice.None;
        }

        if (ReferenceEquals(toolChoice, ToolChoice.Any))
        {
            return ChatCompletionsToolChoice.Required;
        }

        throw new ArgumentException($"Unsupported tool choice: {toolChoice}", nameof(toolChoice));
    }

    /// <summary>Port of <c>chat_tool_call</c>: arguments are serialised with Python <c>json.dumps</c> defaults.</summary>
    public static ChatCompletionsToolCall ChatToolCall(ToolCall toolCall) =>
        new(toolCall.Id, new FunctionCall(toolCall.Function, PythonJson.Dumps(toolCall.Arguments)));
}

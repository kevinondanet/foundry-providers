using System.Text.Json;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;
using Microsoft.Extensions.AI;
using ChatMessage = InspectAzureAI.Provider.Core.ChatMessage;

namespace InspectAzureAI.Maf;

/// <summary>
/// An Inspect <see cref="ToolDef"/> as an Agent Framework <see cref="AIFunction"/>, so Inspect's tools (the sandbox
/// <c>bash</c> and <c>python</c> tools, a submit tool, MCP tools) can be handed to an Agent Framework agent. A call
/// runs through the Inspect tool executor (argument validation, error mapping, output truncation, the transcript
/// tool event) under the model's own tool-call id. The result the framework carries back is the tool message's
/// text, or the <see cref="ChatMessageTool"/> itself when it holds an error or non-text content, so the error and
/// the content reach the model and the state intact (<see cref="InspectChatClient"/> unwraps it). Approval is not
/// applied here: the bridge approved the call on the model response before the framework saw it.
/// </summary>
public sealed class ToolDefFunction : AIFunction
{
    private readonly JsonElement _schema;

    public ToolDefFunction(ToolDef tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        Tool = tool;
        _schema = JsonSerializer.Deserialize<JsonElement>(tool.Parameters.ToJson().ToJsonString());
    }

    public ToolDef Tool { get; }

    public override string Name => Tool.Name;

    public override string Description => Tool.Description;

    public override JsonElement JsonSchema => _schema;

    /// <summary>Output truncation in bytes; null keeps the tool's own limit or the executor's default.</summary>
    public int? MaxOutput { get; init; }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var context = FunctionInvokingChatClient.CurrentContext;
        var call = context is { CallContent: var content }
            ? new ToolCall(content.CallId, content.Name, MafConversion.ToJsonObject(arguments)) { ParseError = content.Exception?.Message }
            : new ToolCall(Guid.NewGuid().ToString("N"), Name, MafConversion.ToJsonObject(arguments));
        IReadOnlyList<ChatMessage> conversation = context is null ? [] : MafConversion.ToInspectMessages(context.Messages, null);
        var message = await ToolExecutor.ExecuteOneAsync(call, Tool, conversation, MaxOutput, cancellationToken).ConfigureAwait(false);
        return message.Error is null && message.Content.IsString ? message.Text : message;
    }
}

/// <summary>Helpers for handing Inspect tools to Agent Framework agents.</summary>
public static class MafTools
{
    public static AIFunction FromToolDef(ToolDef tool) => new ToolDefFunction(tool);

    public static List<AITool> FromToolDefs(IEnumerable<ToolDef> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return tools.Select(tool => (AITool)new ToolDefFunction(tool)).ToList();
    }
}

using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Agents;

/// <summary>
/// Port of <c>agent/_handoff.py</c> <c>AgentTool</c>: what a handoff <see cref="ToolDef"/> carries so that
/// <see cref="ToolExecutor"/> can intercept the call and run the agent over the conversation instead of the
/// tool's own executor. Limits are scoped to each handoff.
/// </summary>
public sealed record AgentHandoff(AgentDef Agent, MessageFilter? InputFilter, MessageFilter? OutputFilter, AgentLimits? Limits);

/// <summary>Port of <c>agent_handoff</c>'s return: the tool's own result, the messages the agent added, its output and its name.</summary>
public sealed record HandoffResult(ToolResult Result, IReadOnlyList<ChatMessage> Messages, ModelOutput? Output, string AgentName);

/// <summary>Port of <c>agent/_handoff.py</c> <c>handoff</c> / <c>has_handoff</c> and <c>model/_call_tools.py</c> <c>agent_handoff</c>.</summary>
public static partial class Agents
{
    /// <summary>
    /// Port of <c>handoff(agent, description, input_filter, output_filter, tool_name, limits)</c>: a
    /// <c>transfer_to_&lt;name&gt;</c> tool (serial, no parameters) that hands the conversation to the agent.
    /// The tool must be executed through <see cref="ToolExecutor.ExecuteToolsAsync"/>, which recognises
    /// <see cref="ToolDef.Handoff"/>; calling its executor directly throws. <paramref name="outputFilter"/>
    /// defaults to <see cref="MessageFilters.ContentOnly"/> like Python; pass <see cref="MessageFilters.Identity"/>
    /// for Python's <c>output_filter=None</c>. Python also exposes the agent's own parameters as tool arguments
    /// and curries the rest; this port's agents take none.
    /// </summary>
    public static ToolDef Handoff(
        AgentDef agent,
        string? description = null,
        MessageFilter? inputFilter = null,
        MessageFilter? outputFilter = null,
        string? toolName = null,
        AgentLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var agentToolName = AgentToolName(agent);
        var resolvedDescription = ResolveAgentDescription(agent, agentToolName, description);
        var name = toolName ?? $"transfer_to_{agentToolName}";
        return new ToolDef(name, resolvedDescription, new ToolParams(), (_, _) => throw new InvalidOperationException("AgentTool should not be called directly"))
        {
            Parallel = false,
            Handoff = new AgentHandoff(agent, inputFilter, outputFilter ?? MessageFilters.ContentOnly, limits),
        };
    }

    /// <summary>Port of <c>has_handoff(tools)</c>: whether any tool is a handoff.</summary>
    public static bool HasHandoff(IEnumerable<ToolDef>? tools) => tools?.Any(tool => tool.Handoff is not null) == true;

    /// <summary>
    /// Port of <c>agent_handoff(tool_def, call, conversation)</c>. The agent sees a copy of the conversation
    /// with the other tool calls of the last assistant message removed, a "Successfully transferred to
    /// &lt;agent&gt;." tool message answering the call (re-added as a user message if the input filter dropped
    /// it) and no system messages. Afterwards only the messages the agent added after that boundary are kept:
    /// its system messages are dropped, its assistant messages are prefixed with <c>[&lt;agent&gt;]</c>, the
    /// output filter runs, and a closing user message is appended when the agent hit one of its limits or
    /// ended on an assistant message. Any limit error inside the agent is caught, as in Python.
    /// </summary>
    internal static async Task<HandoffResult> ExecuteHandoffAsync(AgentHandoff handoff, ToolCall call, IReadOnlyList<ChatMessage> conversation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(conversation);
        var agentName = handoff.Agent.Name;

        var agentConversation = conversation.ToList();
        if (agentConversation.Count > 0 && agentConversation[^1] is ChatMessageAssistant { ToolCalls: { Count: > 0 } toolCalls } last)
        {
            agentConversation[^1] = last with { ToolCalls = toolCalls.Where(c => c.Id == call.Id).ToList() };
        }

        var toolResult = $"Successfully transferred to {agentName}.";
        agentConversation.Add(new ChatMessageTool(toolResult, toolCallId: call.Id, function: call.Function));

        bool IsTransferredToToolResponse(ChatMessage message) => message is ChatMessageTool tool && tool.ToolCallId == call.Id;

        if (handoff.InputFilter is { } inputFilter)
        {
            agentConversation = (await inputFilter(agentConversation, cancellationToken).ConfigureAwait(false)).ToList();
        }

        if (agentConversation.Count == 0 || !IsTransferredToToolResponse(agentConversation[^1]))
        {
            agentConversation.Add(new ChatMessageUser(toolResult));
        }

        bool IsAgentConversationBoundary(ChatMessage message) => message switch
        {
            _ when IsTransferredToToolResponse(message) => true,
            ChatMessageUser { Content.IsString: true } user => user.Content.Text == toolResult,
            ChatMessageUser { Content.Items: [ContentText first, ..] } => first.Text == toolResult,
            _ => false,
        };

        agentConversation = agentConversation.Where(m => m is not ChatMessageSystem).ToList();

        LimitExceededException? limitError = null;
        var agentState = new AgentState(agentConversation);
        using (var scope = AgentLimitScope.Apply(handoff.Limits, cancellationToken))
        using (SampleContext.Current?.Transcript.Span(agentName, "agent"))
        {
            try
            {
                agentState = await handoff.Agent.Execute(agentState, scope.CancellationToken).ConfigureAwait(false);
            }
            catch (LimitExceededException ex)
            {
                limitError = ex;
            }
            catch (OperationCanceledException) when (scope.TimedOut)
            {
                limitError = scope.TimeLimitExceeded();
            }
        }

        IReadOnlyList<ChatMessage> newMessages = agentState.Messages;
        var lastBoundary = agentState.Messages.Select((m, i) => (Message: m, Index: i)).Where(x => IsAgentConversationBoundary(x.Message)).Select(x => (int?)x.Index).LastOrDefault();
        if (lastBoundary is { } boundary)
        {
            newMessages = agentState.Messages.Skip(boundary + 1).ToList();
        }

        var conversationIds = agentConversation.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var agentMessages = new List<ChatMessage>();
        foreach (var message in newMessages)
        {
            if (message.Id is { } id && conversationIds.Contains(id))
            {
                continue;
            }

            switch (message)
            {
                case ChatMessageAssistant assistant:
                    agentMessages.Add(PrependAgentName(assistant, agentName));
                    break;
                case ChatMessageSystem:
                    break;
                default:
                    agentMessages.Add(message);
                    break;
            }
        }

        if (handoff.OutputFilter is { } outputFilter)
        {
            agentMessages = (await outputFilter(agentMessages, cancellationToken).ConfigureAwait(false)).ToList();
        }

        if (limitError is not null)
        {
            agentMessages.Add(new ChatMessageUser($"The {agentName} exceeded its {limitError.Type} limit of {limitError.LimitStr}."));
        }
        else if (agentMessages.Count == 0 || agentMessages[^1] is ChatMessageAssistant)
        {
            agentMessages.Add(new ChatMessageUser($"The {agentName} agent has completed its work."));
        }

        return new HandoffResult(toolResult, agentMessages, agentState.Output, agentName);
    }

    /// <summary>Port of <c>prepend_agent_name</c>: <c>[agent] </c> before string content or the first non-empty text item.</summary>
    internal static ChatMessageAssistant PrependAgentName(ChatMessageAssistant message, string agentName)
    {
        if (message.Content.IsString)
        {
            return message with { Content = $"[{agentName}] {message.Content.Text}" };
        }

        var content = message.Content.Items!.ToList();
        for (var i = 0; i < content.Count; i++)
        {
            if (content[i] is ContentText text)
            {
                if (text.Text.Length > 0)
                {
                    content[i] = text with { Text = $"[{agentName}] {text.Text}" };
                }

                break;
            }
        }

        return message with { Content = MessageContent.FromItems(content) };
    }
}

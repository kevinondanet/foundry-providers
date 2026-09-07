using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Approval;

/// <summary>Port of <c>agent/_bridge/_approval.py</c> <c>BridgeApproval</c>: the outcome of approving the tool calls in a bridged model response.</summary>
/// <param name="Output">Response to hand to the scaffold (a copy when a <c>modify</c> decision applied).</param>
/// <param name="Rejection">When set, the response was rejected: replay these to the model and generate again.</param>
public sealed record BridgeApprovalResult(ModelOutput Output, IReadOnlyList<ChatMessage>? Rejection);

/// <summary>
/// Port of <c>agent/_bridge/_approval.py</c>: tool approval for bridged agents. A bridged scaffold runs its own
/// tool loop, so Inspect never executes its tool calls and the <see cref="Tools.ToolExecutor"/> approval path
/// never runs. The approval chain is applied to the tool calls in each bridged model response instead, at the
/// <see cref="AgentBridge.GenerateAsync"/> chokepoint every bridge configuration shares.
/// <para>
/// A rejection is resolved as an internal round-trip rather than by editing the response the scaffold sees:
/// the rejected call and a synthetic tool result are appended to the <em>model's</em> input and generation is
/// retried, so the model learns it was denied while the scaffold sees one ordinary response and its own
/// conversation never contains the rejected call.
/// </para>
/// </summary>
public static class BridgeApproval
{
    /// <summary>
    /// Consecutive rejected generations before the sample is terminated: a model that keeps proposing rejected
    /// calls would otherwise loop forever. Counted within a single bridged generation, so a rejection followed
    /// by an approved generation does not accumulate.
    /// </summary>
    public const int MaxConsecutiveRejections = 3;

    /// <summary>
    /// Cap on the rendered call used to point at a rejected peer: arguments can be arbitrarily large and the
    /// description is repeated in every peer's result on every retry, so it is bounded.
    /// </summary>
    public const int MaxCallDescription = 200;

    /// <summary>The warning issued once when a multi-choice response is reduced to its primary choice under approval.</summary>
    public const string MultiChoiceWarning =
        "Tool approval reviews only the primary choice of a bridged response; dropping alternate choices that "
        + "carry tool calls. Request a single choice (n=1) when approval is active.";

    /// <summary>Port of <c>describe_call</c>: a single-line, length-bounded rendering of a tool call.</summary>
    public static string DescribeCall(ToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var description = ModelGraded.FormatFunctionCall(call.Function, call.Arguments, width: int.MaxValue);
        if (description.Length > MaxCallDescription)
        {
            description = description[..MaxCallDescription].TrimEnd() + "...";
        }

        return description;
    }

    /// <summary>
    /// Port of <c>apply_bridge_tool_approval</c>: approves the tool calls in a bridged model response under the
    /// bridge's own policies (or the ambient ones when it has none). Calls are approved in order and evaluation
    /// stops at the first non-approval, so a human is not asked to decide on calls that are about to be discarded
    /// anyway; <c>terminate</c> does not return. A multi-choice response whose alternate choices carry tool calls
    /// is reduced to the primary choice (with a warning): only that choice is reviewed. Modifications are adopted
    /// only once the whole response is approved.
    /// </summary>
    public static async Task<BridgeApprovalResult> ApplyAsync(
        AgentBridge bridge,
        ModelOutput output,
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(history);

        Dictionary<string, ToolCall> modified = new(StringComparer.Ordinal);
        using (ToolApproval.BeginIfAny(bridge.Approval))
        {
            if (!ToolApproval.HaveToolApproval || output.Empty)
            {
                return new BridgeApprovalResult(output, null);
            }

            if (output.Choices.Skip(1).Any(choice => choice.Message.ToolCalls is { Count: > 0 }))
            {
                ProviderLogger.WarnOnce(MultiChoiceWarning);
                output = output with { Choices = [output.Choices[0]] };
            }

            var toolCalls = output.Message.ToolCalls;
            if (toolCalls is not { Count: > 0 })
            {
                return new BridgeApprovalResult(output, null);
            }

            // approvers see the assistant turn under review, matching the native path where the conversation
            // ends with the message carrying the tool calls (an approver may weigh a call against its siblings)
            var approvalHistory = new List<ChatMessage>(history.Count + 1);
            approvalHistory.AddRange(history);
            approvalHistory.Add(output.Message);
            var message = output.Message.Text;
            foreach (var call in toolCalls)
            {
                // no viewer: bridged tools reach us as ToolInfo from the scaffold's request, not as ToolDef
                var (approved, approval) = await ToolApproval.ApplyAsync(message, call, null, approvalHistory, cancellationToken).ConfigureAwait(false);
                if (!approved)
                {
                    var explanation = approval?.Explanation is { Length: > 0 } text
                        ? text
                        : $"Tool call '{call.Function}' was rejected by the approval policy.";
                    if (approval?.Decision == ApprovalDecision.Terminate)
                    {
                        bridge.RequestTerminate($"Tool call approver requested termination: {explanation}");
                    }

                    return new BridgeApprovalResult(output, RejectionMessages(output, call, explanation));
                }

                if (approval?.Modified is { } modifiedCall)
                {
                    modified[call.Id] = modifiedCall;
                }
            }
        }

        return modified.Count > 0
            ? new BridgeApprovalResult(WithModifiedArguments(output, modified), null)
            : new BridgeApprovalResult(output, null);
    }

    /// <summary>
    /// Port of <c>with_modified_arguments</c>: a copy of <paramref name="output"/> with the approved argument
    /// rewrites applied, keyed by call id. A copy, so the recorded model output and approval events still show
    /// what the model proposed. Only the arguments are adopted: the scaffold dispatches on the function name and
    /// may have no handler for a substituted one.
    /// </summary>
    public static ModelOutput WithModifiedArguments(ModelOutput output, IReadOnlyDictionary<string, ToolCall> modified)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(modified);
        var choices = output.Choices.Select(choice =>
        {
            if (choice.Message.ToolCalls is not { Count: > 0 } calls)
            {
                return choice;
            }

            var rewritten = calls.Select(call => modified.TryGetValue(call.Id, out var replacement)
                    ? call with { Arguments = replacement.Arguments.DeepClone().AsObject() }
                    : call)
                .ToArray();
            return choice with { Message = choice.Message with { ToolCalls = rewritten } };
        }).ToArray();
        return output with { Choices = choices };
    }

    /// <summary>
    /// Port of <c>rejection_messages</c>: the retry input for a rejected response — the assistant message plus a
    /// tool result for every call in it (Anthropic requires each <c>tool_use</c> to be matched by a
    /// <c>tool_result</c>). The collateral results name the call that caused the rejection so the model does not
    /// conclude that an innocent sibling is disallowed.
    /// </summary>
    public static IReadOnlyList<ChatMessage> RejectionMessages(ModelOutput output, ToolCall rejected, string explanation)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(rejected);
        ArgumentNullException.ThrowIfNull(explanation);
        var toolCalls = output.Message.ToolCalls ?? [];
        var others = toolCalls.Count - 1;
        var description = DescribeCall(rejected);

        var messages = new List<ChatMessage>(toolCalls.Count + 1) { output.Message };
        foreach (var call in toolCalls)
        {
            string text;
            if (call.Id == rejected.Id)
            {
                text = explanation;
                if (others == 1)
                {
                    text += " The other tool call in this response was not executed as a result.";
                }
                else if (others > 1)
                {
                    text += $" The other {others} tool calls in this response were not executed as a result.";
                }
            }
            else
            {
                text = "This tool call was not executed because a parallel tool call in the same response was "
                    + $"rejected: {description} — {explanation} This call was not itself rejected; you may "
                    + "re-issue it without the rejected call.";
            }

            messages.Add(new ChatMessageTool("", toolCallId: call.Id, function: call.Function, error: new ToolCallError("approval", text)));
        }

        return messages;
    }

    /// <summary>Port of <c>terminate_for_repeated_rejections</c>: terminates the sample after too many consecutive rejected generations.</summary>
    public static void TerminateForRepeatedRejections(AgentBridge bridge, int rejections)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        bridge.RequestTerminate($"Tool call approver rejected {rejections} consecutive generations from the bridged agent.");
    }
}

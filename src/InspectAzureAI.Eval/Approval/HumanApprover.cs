using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Approval;

/// <summary>What a human is asked to decide on (port of <c>approval/_human/manager.py</c> <c>ApprovalRequest</c>).</summary>
public sealed record ApprovalRequest(
    string Message,
    ToolCall Call,
    ToolCallView View,
    IReadOnlyList<ChatMessage> History,
    IReadOnlyList<ApprovalDecision> Choices);

/// <summary>
/// The surface that collects a human's decision: Python dispatches to ACP clients, the fullscreen panel or the
/// console; this port ships the console (<see cref="ConsoleApprovalPrompter"/>) and lets a host plug in its own.
/// </summary>
public interface IApprovalPrompter
{
    /// <summary>Presents <paramref name="request"/> and returns one of its <see cref="ApprovalRequest.Choices"/>.</summary>
    Task<ApprovalDecision> PromptAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>The explanations the human approver records (port of <c>approval/_human/util.py</c>).</summary>
public static class HumanApprovals
{
    public const string Approved = "Human operator approved tool call.";

    public const string Rejected = "Human operator rejected the tool call.";

    public const string Terminated = "Human operator asked that the sample be terminated.";

    public const string Escalated = "Human operator escalated the tool call approval.";

    /// <summary>Python's default <c>choices</c>: approve, reject, terminate.</summary>
    public static readonly IReadOnlyList<ApprovalDecision> DefaultChoices = [ApprovalDecision.Approve, ApprovalDecision.Reject, ApprovalDecision.Terminate];
}

public static partial class Approvers
{
    private static IApprovalPrompter _defaultPrompter = new ConsoleApprovalPrompter();

    /// <summary>The prompter used by <see cref="Human"/> when none is given (and by the <c>human</c> entry of a policy file); the console by default.</summary>
    public static IApprovalPrompter DefaultPrompter
    {
        get => _defaultPrompter;
        set => _defaultPrompter = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Port of <c>human_approver</c> (registry name <c>human</c>): asks <paramref name="prompter"/> (or
    /// <see cref="DefaultPrompter"/>, resolved per call) to choose among <paramref name="choices"/> and records the
    /// matching explanation. A prompter answering <see cref="ApprovalDecision.Modify"/> is a
    /// <see cref="NotSupportedException"/>: it returns a decision, not a modified call.
    /// </summary>
    public static ApproverDef Human(IReadOnlyList<ApprovalDecision>? choices = null, IApprovalPrompter? prompter = null)
    {
        var resolvedChoices = choices?.ToArray() ?? HumanApprovals.DefaultChoices;
        if (resolvedChoices.Count == 0)
        {
            throw new ArgumentException("The human approver needs at least one choice.", nameof(choices));
        }

        async Task<Approval> Approve(string message, ToolCall call, ToolCallView view, IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken)
        {
            var decision = await (prompter ?? DefaultPrompter)
                .PromptAsync(new ApprovalRequest(message, call, view, history, resolvedChoices), cancellationToken)
                .ConfigureAwait(false);
            return decision switch
            {
                ApprovalDecision.Approve => new Approval(decision, Explanation: HumanApprovals.Approved),
                ApprovalDecision.Reject => new Approval(decision, Explanation: HumanApprovals.Rejected),
                ApprovalDecision.Terminate => new Approval(decision, Explanation: HumanApprovals.Terminated),
                ApprovalDecision.Escalate => new Approval(decision, Explanation: HumanApprovals.Escalated),
                ApprovalDecision.Modify => throw new NotSupportedException("The human approver cannot apply a 'modify' decision: its prompter returns a decision, not a modified tool call."),
                _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "Unknown approval decision."),
            };
        }

        return new ApproverDef("human", Approve)
        {
            Params = choices is not null
                ? new JsonObject { ["choices"] = new JsonArray(choices.Select(choice => (JsonNode?)JsonValue.Create(choice.ToPython())).ToArray()) }
                : new JsonObject(),
        };
    }

    /// <summary>The <c>human</c> registry factory: accepts <c>choices</c> (a list of decision names); any other parameter is an <see cref="ArgumentException"/>.</summary>
    internal static ApproverDef HumanFromParams(JsonObject parameters)
    {
        IReadOnlyList<ApprovalDecision>? choices = null;
        foreach (var pair in parameters)
        {
            if (pair.Key != "choices")
            {
                throw new ArgumentException($"Unknown parameter '{pair.Key}' for approver 'human'.", nameof(parameters));
            }

            if (pair.Value is not JsonArray array)
            {
                throw new ArgumentException("Parameter 'choices' of approver 'human' must be a list of decision names.", nameof(parameters));
            }

            choices = array.Select(item => ApprovalDecisions.Parse(RequireString(item, "human", "choices"))).ToArray();
        }

        return Human(choices);
    }
}

using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Approval;

/// <summary>
/// Port of <c>approval/_approval.py</c> <c>ApprovalDecision</c>: <see cref="Approve"/> runs the call,
/// <see cref="Modify"/> runs it with the approval's modified call, <see cref="Reject"/> reports an approval error
/// to the model, <see cref="Terminate"/> ends the sample and <see cref="Escalate"/> hands the decision to the
/// next approver in the policy chain.
/// </summary>
public enum ApprovalDecision
{
    Approve,
    Modify,
    Reject,
    Terminate,
    Escalate,
}

/// <summary>The Python literal names of <see cref="ApprovalDecision"/> (what the log and config files carry).</summary>
public static class ApprovalDecisions
{
    /// <summary>The Python literal for <paramref name="decision"/> ("approve", "modify", "reject", "terminate", "escalate").</summary>
    public static string ToPython(this ApprovalDecision decision) => decision switch
    {
        ApprovalDecision.Approve => "approve",
        ApprovalDecision.Modify => "modify",
        ApprovalDecision.Reject => "reject",
        ApprovalDecision.Terminate => "terminate",
        ApprovalDecision.Escalate => "escalate",
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "Unknown approval decision."),
    };

    /// <summary>Parses a Python literal; anything else is an <see cref="ArgumentException"/>.</summary>
    public static ApprovalDecision Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            "approve" => ApprovalDecision.Approve,
            "modify" => ApprovalDecision.Modify,
            "reject" => ApprovalDecision.Reject,
            "terminate" => ApprovalDecision.Terminate,
            "escalate" => ApprovalDecision.Escalate,
            _ => throw new ArgumentException($"Invalid approval decision '{value}'; expected one of approve, modify, reject, terminate, escalate.", nameof(value)),
        };
    }
}

/// <summary>
/// Port of <c>approval/_approval.py</c> <c>Approval</c>: an approver's decision, the modified call for
/// <see cref="ApprovalDecision.Modify"/>, an explanation and free-form metadata (all recorded on the <c>ApprovalEvent</c>).
/// </summary>
public sealed record Approval(
    ApprovalDecision Decision,
    ToolCall? Modified = null,
    string? Explanation = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);

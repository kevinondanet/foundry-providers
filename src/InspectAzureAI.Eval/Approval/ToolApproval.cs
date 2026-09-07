using InspectAzureAI.Eval.Context;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Approval;

/// <summary>Port of the <c>apply_tool_approval</c> return: whether the call may run and the decision behind it (null when no approval is active).</summary>
public readonly record struct ToolApprovalResult(bool Approved, Approval? Approval);

/// <summary>
/// Port of <c>approval/_apply.py</c> and <c>approval/_call.py</c>: the ambient tool approver (Python's
/// <c>_tool_approver</c> context variable, an AsyncLocal here so it flows from the eval into every sample, tool
/// stage and bridge request handler), the <c>approval()</c> scope, <c>apply_tool_approval</c> and the recording of
/// <see cref="ApprovalEvent"/>s.
/// </summary>
public static class ToolApproval
{
    private static readonly AsyncLocal<ApproverDef?> Ambient = new();

    /// <summary>Port of <c>have_tool_approval()</c>.</summary>
    public static bool HaveToolApproval => Ambient.Value is not null;

    /// <summary>The active policy approver (null when no approval is active).</summary>
    public static ApproverDef? Current => Ambient.Value;

    /// <summary>
    /// Port of the <c>approval(policies)</c> context manager: installs a policy approver over <paramref name="policies"/>
    /// until the result is disposed, restoring the previous one (scopes nest). An empty list rejects every call.
    /// </summary>
    public static IDisposable Begin(IReadOnlyList<ApprovalPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        return Install(ApprovalPolicies.PolicyApprover(policies));
    }

    /// <summary>
    /// Port of <c>approval_context(policies) if policies else nullcontext()</c> (the <c>approval</c> parameters of
    /// <c>execute_tools</c>, <c>react</c> and the bridges, and <c>init_tool_approval</c>): a null or empty list leaves
    /// the ambient approver untouched.
    /// </summary>
    public static IDisposable BeginIfAny(IReadOnlyList<ApprovalPolicy>? policies) =>
        policies is { Count: > 0 } ? Begin(policies) : NoScope.Instance;

    /// <summary>
    /// Port of <c>init_tool_approval</c> (what <c>eval()</c> and the task context call): installs a policy approver
    /// over <paramref name="policies"/>, or clears the ambient approver when null or empty, until the result is
    /// disposed. Unlike <see cref="BeginIfAny"/> an empty value does not inherit an outer scope.
    /// </summary>
    public static IDisposable Init(IReadOnlyList<ApprovalPolicy>? policies) =>
        Install(policies is { Count: > 0 } ? ApprovalPolicies.PolicyApprover(policies) : null);

    /// <summary>
    /// Port of <c>apply_tool_approval</c>: resolves the view (the tool's viewer, completed with the default call
    /// rendering; a failing viewer is logged once and falls back to the default), consults the ambient approver
    /// with token and turn limits suspended (an approver's own inference is not charged to the agent) and maps the
    /// decision: approve/modify allow, reject/terminate deny, escalate from the policy approver is a bug.
    /// </summary>
    public static async Task<ToolApprovalResult> ApplyAsync(
        string message,
        ToolCall call,
        ToolCallViewer? viewer,
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(history);
        var approver = Ambient.Value;
        if (approver is null)
        {
            return new ToolApprovalResult(true, null);
        }

        ToolCallView view;
        if (viewer is not null)
        {
            try
            {
                view = viewer(call);
                if (view.Call is null)
                {
                    view = view with { Call = ToolCallViews.Default(call).Call };
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ProviderLogger.WarnOnce($"Error in viewer for tool '{call.Function}': {ex.Message}. Falling back to default rendering.");
                view = ToolCallViews.Default(call);
            }
        }
        else
        {
            view = ToolCallViews.Default(call);
        }

        Approval approval;
        using (TokenLimit.SuspendTokenLimit())
        using (TurnLimit.SuspendTurnLimit())
        {
            approval = await approver.Approve(message, call, view, history, cancellationToken).ConfigureAwait(false);
        }

        return approval.Decision switch
        {
            ApprovalDecision.Approve or ApprovalDecision.Modify => new ToolApprovalResult(true, approval),
            ApprovalDecision.Reject or ApprovalDecision.Terminate => new ToolApprovalResult(false, approval),
            ApprovalDecision.Escalate => throw new InvalidOperationException("Unexpected 'escalate' from policy approver."),
            _ => throw new ArgumentOutOfRangeException(nameof(approval), approval.Decision, "Unknown approval decision."),
        };
    }

    /// <summary>Port of <c>call_approver</c>: runs one approver of a policy and records its decision.</summary>
    public static async Task<Approval> CallApproverAsync(
        ApproverDef approver,
        string message,
        ToolCall call,
        ToolCallView view,
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approver);
        var approval = await approver.Approve(message, call, view, history, cancellationToken).ConfigureAwait(false);
        RecordApproval(approver.Name, message, call, view, approval);
        return approval;
    }

    /// <summary>Port of <c>record_approval</c>: appends an <see cref="ApprovalEvent"/> to the current sample's transcript (no-op outside a sample).</summary>
    public static void RecordApproval(string approverName, string message, ToolCall call, ToolCallView? view, Approval approval)
    {
        ArgumentNullException.ThrowIfNull(approverName);
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(approval);
        SampleContext.Current?.Transcript.Add(new ApprovalEvent(message, call, approverName, approval.Decision.ToPython())
        {
            View = view,
            Modified = approval.Modified,
            Explanation = approval.Explanation,
            Metadata = approval.Metadata,
        });
    }

    private static IDisposable Install(ApproverDef? approver)
    {
        var previous = Ambient.Value;
        Ambient.Value = approver;
        return new Restore(previous);
    }

    private sealed class Restore(ApproverDef? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();

        public void Dispose()
        {
        }
    }
}

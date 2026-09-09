using InspectAzureAI.Eval.Approval;

namespace InspectAzureAI.Examples.Approval;

/// <summary>
/// Port of the custom approvers of <c>examples/approval/approval.py</c>: <c>bash_allowlist</c> and
/// <c>python_allowlist</c>. In Python the <c>@approver</c> decorator registers each function under its name when the
/// module is imported, which is what lets <c>approval.yaml</c> reference them; this port has no import-time hook, so
/// <see cref="Register"/> does that step explicitly (the task calls it, and so does the console app). Both approvers
/// inspect the call's first argument through <see cref="ApproverSupport.FirstArgumentText"/>, which states how a
/// missing or non-string argument reads.
/// </summary>
public static partial class ExampleApprovers
{
    /// <summary>
    /// Port of what <c>@approver</c> does at import time: registers <c>bash_allowlist</c> and <c>python_allowlist</c>
    /// with <see cref="ApproverRegistry"/> so an approval policy (<c>approval.json</c>, or <c>--approval</c>) can
    /// name them. Idempotent: <see cref="ApproverRegistry.Register"/> replaces an existing registration of the same
    /// name, so calling this any number of times leaves the registry in the same state.
    /// </summary>
    public static void Register()
    {
        ApproverRegistry.Register(BashAllowlistName, BashAllowlistFromParams);
        ApproverRegistry.Register(PythonAllowlistName, PythonAllowlistFromParams);
    }
}

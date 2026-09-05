using InspectAzureAI.Eval.Tools;

namespace InspectAzureAI.Eval.Approval;

/// <summary>Port of <c>tool/_tool.py</c> <c>ToolApprovalError</c>: the call was rejected; reported to the model as an <c>approval</c> tool error.</summary>
public sealed class ToolApprovalError(string? message) : ToolError(string.IsNullOrEmpty(message) ? "Tool call not approved." : message);

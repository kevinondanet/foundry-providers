namespace InspectAzureAI.Eval.Approval;

/// <summary>
/// Port of <c>approval/_policy.py</c> <c>ApprovalPolicy</c>: an approver and the tools it handles. Each entry of
/// <see cref="Tools"/> is a comma-separated list of tool names or globs, prefix-matched against the rendered call
/// (<c>name(arg='value', ...)</c>) so <c>web_browser_type</c> also matches <c>web_browser_type_submit</c> and
/// <c>computer(action='key'</c> matches on an argument.
/// </summary>
public sealed record ApprovalPolicy
{
    /// <summary>A policy over a single spec (Python's <c>tools: str</c>, written as a string in the log's config).</summary>
    public ApprovalPolicy(ApproverDef approver, string tools)
    {
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentNullException.ThrowIfNull(tools);
        Approver = approver;
        Tools = [tools];
        ToolsAsString = true;
    }

    /// <summary>A policy over a list of specs (Python's <c>tools: list[str]</c>).</summary>
    public ApprovalPolicy(ApproverDef approver, IReadOnlyList<string> tools)
    {
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentNullException.ThrowIfNull(tools);
        Approver = approver;
        Tools = tools.ToArray();
        ToolsAsString = false;
    }

    public ApproverDef Approver { get; }

    public IReadOnlyList<string> Tools { get; }

    /// <summary>Whether <see cref="Tools"/> was given as one string (the config round-trip keeps Python's <c>str | list[str]</c> shape).</summary>
    public bool ToolsAsString { get; }
}

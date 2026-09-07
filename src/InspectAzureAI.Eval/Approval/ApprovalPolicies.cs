using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Approval;

/// <summary>
/// Port of the module functions of <c>approval/_policy.py</c>: the policy approver that chains a list of
/// <see cref="ApprovalPolicy"/> by tool glob, and reading/writing policies as config (JSON files here; see
/// <see cref="FromFile"/>).
/// </summary>
public static class ApprovalPolicies
{
    /// <summary>The approver name recorded when no policy grants approval (Python's <c>record_approval("policy", ...)</c>).</summary>
    public const string PolicyApproverName = "policy";

    /// <summary>
    /// Port of <c>policy_approver</c>: consults the approvers whose globs match the rendered call in policy order,
    /// continuing past <see cref="ApprovalDecision.Escalate"/>; when none decides, rejects with "No approval granted"
    /// (some approver escalated) or "No approvers registered" (no glob matched), recorded under <see cref="PolicyApproverName"/>.
    /// </summary>
    public static ApproverDef PolicyApprover(IReadOnlyList<ApprovalPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var matchers = policies.Select(policy => (Globs: Globs(policy.Tools), policy.Approver)).ToList();

        async Task<Approval> Approve(string message, ToolCall call, ToolCallView view, IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken)
        {
            var hasApprover = false;
            foreach (var (globs, approver) in matchers)
            {
                if (!Matches(globs, call))
                {
                    continue;
                }

                hasApprover = true;
                var approval = await ToolApproval.CallApproverAsync(approver, message, call, view, history, cancellationToken).ConfigureAwait(false);
                if (approval.Decision != ApprovalDecision.Escalate)
                {
                    return approval;
                }
            }

            var reject = new Approval(
                ApprovalDecision.Reject,
                Explanation: $"No {(hasApprover ? "approval granted" : "approvers registered")} for tool {call.Function}");
            ToolApproval.RecordApproval(PolicyApproverName, message, call, view, reject);
            return reject;
        }

        return new ApproverDef(PolicyApproverName, Approve);
    }

    /// <summary>The globs of a policy's specs: comma-separated names trimmed, empties dropped, <c>*</c> appended unless already trailing.</summary>
    internal static IReadOnlyList<string> Globs(IReadOnlyList<string> specs) =>
        specs.SelectMany(spec => spec.Split(','))
            .Select(tool => tool.Trim())
            .Where(tool => tool.Length > 0)
            .Select(tool => tool.EndsWith('*') ? tool : tool + "*")
            .ToArray();

    /// <summary>The call as <c>format_function_call(function, arguments, width=sys.maxsize)</c> renders it: the text the globs match.</summary>
    internal static string RenderCall(ToolCall call) => ModelGraded.FormatFunctionCall(call.Function, call.Arguments, width: int.MaxValue);

    /// <summary>Port of the <c>fnmatch</c> test over the rendered call.</summary>
    internal static bool Matches(IReadOnlyList<string> globs, ToolCall call)
    {
        var rendered = RenderCall(call);
        return globs.Any(glob => FnMatch.Match(rendered, glob));
    }

    /// <summary>
    /// Port of <c>read_approval_policies</c>: reads a policy file (a local path or a <c>file://</c> URI). A missing
    /// file is a <see cref="FileNotFoundException"/>. Only JSON is read: a YAML file is a <see cref="NotSupportedException"/>
    /// (this port has no YAML parser); the JSON structure is the same (<c>{"approvers": [{"name", "tools", ...}]}</c>).
    /// </summary>
    public static IReadOnlyList<ApprovalPolicy> FromFile(string file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var path = LocalPath(file);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Approval policy file not found: {path}", path);
        }

        return FromConfig(ReadConfig(File.ReadAllText(path)));
    }

    /// <summary>
    /// Port of <c>approval_policies_from_config</c> for a string: a path to a policy file, or the name of a
    /// registered approver applied to every tool (<c>--approval human</c>); anything else is an <see cref="ArgumentException"/>.
    /// </summary>
    public static IReadOnlyList<ApprovalPolicy> Resolve(string policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (File.Exists(LocalPath(policy)))
        {
            return FromFile(policy);
        }

        if (ApproverRegistry.IsRegistered(policy))
        {
            return FromConfig(new ApprovalPolicyConfig([new ApproverPolicyConfig(policy, JsonValue.Create("*"))]));
        }

        throw new ArgumentException($"Invalid approval policy: {policy}", nameof(policy));
    }

    /// <summary>Port of <c>approval_policies_from_config</c> for a parsed config: creates each approver from the registry.</summary>
    public static IReadOnlyList<ApprovalPolicy> FromConfig(ApprovalPolicyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Approvers
            .Select(entry =>
            {
                var approver = ApproverRegistry.Create(entry.Name, entry.Params);
                return entry.ToolsAsString ? new ApprovalPolicy(approver, entry.ToolSpecs[0]) : new ApprovalPolicy(approver, entry.ToolSpecs);
            })
            .ToArray();
    }

    /// <summary>
    /// Port of <c>read_policy_config</c> / <c>read_config_object</c>: JSON text (Python detects JSON by a leading
    /// <c>{</c>, else parses YAML, which this port does not support).
    /// </summary>
    public static ApprovalPolicyConfig ReadConfig(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.TrimStart().StartsWith('{'))
        {
            throw new NotSupportedException(
                "Approval policy files must be JSON in this port (YAML is not supported): "
                + "write the same structure as JSON, e.g. {\"approvers\": [{\"name\": \"human\", \"tools\": \"*\"}]}.");
        }

        return ApprovalPolicyConfig.Parse(text);
    }

    /// <summary>Port of <c>config_from_approval_policies</c>: the policies as the config recorded in the log.</summary>
    public static ApprovalPolicyConfig ToConfig(IReadOnlyList<ApprovalPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        return new ApprovalPolicyConfig(policies
            .Select(policy => new ApproverPolicyConfig(
                policy.Approver.Name,
                policy.ToolsAsString ? JsonValue.Create(policy.Tools[0]) : new JsonArray(policy.Tools.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
                policy.Approver.Params))
            .ToArray());
    }

    /// <summary>Port of <c>local_path</c>: a <c>file://</c> URI becomes a local path; other paths are returned as given.</summary>
    internal static string LocalPath(string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeFile ? uri.LocalPath : path;
}

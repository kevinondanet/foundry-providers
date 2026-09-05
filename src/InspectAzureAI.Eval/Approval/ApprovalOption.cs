namespace InspectAzureAI.Eval.Approval;

/// <summary>
/// Port of the <c>approval</c> argument of <c>eval()</c> and <c>Task</c> (<c>str | list[ApprovalPolicy] |
/// ApprovalPolicyConfig | None</c>): a path to a policy file or the name of a registered approver (applied to
/// every tool), a list of policies, or a parsed config. Implicitly convertible from each form; <see cref="Resolve"/>
/// is the port of <c>resolve_approval</c> / <c>approval_policies_from_config</c>.
/// </summary>
public sealed record ApprovalOption
{
    private ApprovalOption(string? spec, IReadOnlyList<ApprovalPolicy>? policies, ApprovalPolicyConfig? config)
    {
        Spec = spec;
        Policies = policies;
        Config = config;
    }

    /// <summary>A policy file path (local or <c>file://</c>) or a registered approver name, when given as a string.</summary>
    public string? Spec { get; }

    /// <summary>The policies, when given directly.</summary>
    public IReadOnlyList<ApprovalPolicy>? Policies { get; }

    /// <summary>The parsed config, when given as one.</summary>
    public ApprovalPolicyConfig? Config { get; }

    /// <summary>Python's <c>approval="human"</c> or <c>approval="approval.json"</c>.</summary>
    public static ApprovalOption FromSpec(string spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);
        return new ApprovalOption(spec, null, null);
    }

    /// <summary>Python's <c>approval=[ApprovalPolicy(...), ...]</c>.</summary>
    public static ApprovalOption FromPolicies(params IReadOnlyList<ApprovalPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        return new ApprovalOption(null, policies.ToArray(), null);
    }

    /// <summary>Python's <c>approval=ApprovalPolicyConfig(...)</c>.</summary>
    public static ApprovalOption FromConfig(ApprovalPolicyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new ApprovalOption(null, null, config);
    }

    public static implicit operator ApprovalOption(string spec) => FromSpec(spec);

    public static implicit operator ApprovalOption(ApprovalPolicy policy) => FromPolicies(policy);

    public static implicit operator ApprovalOption(ApprovalPolicy[] policies) => FromPolicies(policies);

    public static implicit operator ApprovalOption(List<ApprovalPolicy> policies) => FromPolicies(policies);

    public static implicit operator ApprovalOption(ApprovalPolicyConfig config) => FromConfig(config);

    /// <summary>
    /// Port of <c>resolve_approval</c>: a string is read as a policy file or resolved as an approver name
    /// (<see cref="ApprovalPolicies.Resolve"/>), a config creates its approvers from the registry, policies are
    /// returned as given. An unreadable file or unknown name throws as those functions do.
    /// </summary>
    public IReadOnlyList<ApprovalPolicy> Resolve()
    {
        if (Policies is not null)
        {
            return Policies;
        }

        if (Config is not null)
        {
            return ApprovalPolicies.FromConfig(Config);
        }

        return ApprovalPolicies.Resolve(Spec!);
    }
}

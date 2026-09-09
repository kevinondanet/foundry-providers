using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Approval;

// Inside this namespace the simple name 'Approval' would resolve to the namespace itself; the alias restores the record.
using Approval = InspectAzureAI.Eval.Approval.Approval;

public static partial class ExampleApprovers
{
    /// <summary>The registry name of <see cref="PythonAllowlist"/> (the Python function's name).</summary>
    private const string PythonAllowlistName = "python_allowlist";

    /// <summary>Port of the <c>python_allowlist</c> default <c>disallowed_builtins</c>.</summary>
    public static readonly IReadOnlySet<string> DefaultDisallowedBuiltins =
        new HashSet<string>(StringComparer.Ordinal) { "eval", "exec", "compile", "__import__", "open", "input" };

    /// <summary>Port of the <c>python_allowlist</c> default <c>sensitive_modules</c>.</summary>
    public static readonly IReadOnlySet<string> DefaultSensitiveModules =
        new HashSet<string>(StringComparer.Ordinal) { "os", "sys", "subprocess", "socket", "requests" };

    /// <summary>
    /// Port of <c>examples/approval/approval.py</c> <c>python_allowlist</c> (registry name <c>python_allowlist</c>):
    /// an approver that checks that Python code uses only allowed modules and functions, and applies additional safety
    /// checks. The code is the first argument of the tool call whatever its name
    /// (<see cref="ApproverSupport.FirstArgumentText"/>); it is scanned by <see cref="PythonSyntax"/> (see its
    /// deviations), and every decision and explanation is the Python one. As in Python, an empty
    /// <paramref name="disallowedBuiltins"/> or <paramref name="sensitiveModules"/> falls back to the defaults.
    /// The returned <see cref="ApproverDef.Params"/> record <c>allow_system_state_modification</c> only when true and
    /// the two sets only when given, because this direct API cannot tell an explicit default from an omitted
    /// argument; the registry factory (<see cref="PythonAllowlistFromParams"/>) records whatever the policy entry
    /// gives, as <c>registry_params</c> does.
    /// Deviation: Python joins the allowed sets in set order; this joins the distinct entries in the given order.
    /// </summary>
    /// <param name="allowedModules">List of allowed Python modules.</param>
    /// <param name="allowedFunctions">List of allowed built-in functions.</param>
    /// <param name="disallowedBuiltins">Set of disallowed built-in functions (<see cref="DefaultDisallowedBuiltins"/> when null or empty).</param>
    /// <param name="sensitiveModules">Set of sensitive modules to be blocked (<see cref="DefaultSensitiveModules"/> when null or empty).</param>
    /// <param name="allowSystemStateModification">Whether to allow modification of system state (assignment to dunder attributes).</param>
    public static ApproverDef PythonAllowlist(
        IReadOnlyList<string> allowedModules,
        IReadOnlyList<string> allowedFunctions,
        IReadOnlySet<string>? disallowedBuiltins = null,
        IReadOnlySet<string>? sensitiveModules = null,
        bool allowSystemStateModification = false)
    {
        ArgumentNullException.ThrowIfNull(allowedModules);
        ArgumentNullException.ThrowIfNull(allowedFunctions);

        var allowedModulesSet = allowedModules.ToHashSet(StringComparer.Ordinal);
        var allowedFunctionsSet = allowedFunctions.ToHashSet(StringComparer.Ordinal);
        var allowedModulesText = string.Join(", ", allowedModules.Distinct(StringComparer.Ordinal));
        var allowedFunctionsText = string.Join(", ", allowedFunctions.Distinct(StringComparer.Ordinal));
        var builtins = disallowedBuiltins is { Count: > 0 } ? disallowedBuiltins : DefaultDisallowedBuiltins;
        var sensitive = sensitiveModules is { Count: > 0 } ? sensitiveModules : DefaultSensitiveModules;

        Approval Decide(string code)
        {
            if (code.Length == 0)
            {
                return new Approval(ApprovalDecision.Reject, Explanation: "Empty code");
            }

            var scan = PythonSyntax.Scan(code);
            if (!scan.IsValid)
            {
                return new Approval(ApprovalDecision.Reject, Explanation: $"Invalid Python syntax: {scan.Error}");
            }

            foreach (var node in scan.Nodes)
            {
                switch (node.Kind)
                {
                    case PythonNodeKind.Import:
                    case PythonNodeKind.ImportFrom:
                        if (!allowedModulesSet.Contains(node.Name))
                        {
                            return new Approval(
                                ApprovalDecision.Escalate,
                                Explanation: $"Module '{node.Name}' is not in the allowed list. Allowed modules: {allowedModulesText}");
                        }

                        if (sensitive.Contains(node.Name))
                        {
                            return new Approval(
                                ApprovalDecision.Escalate,
                                Explanation: $"Module '{node.Name}' is considered sensitive and not allowed.");
                        }

                        break;

                    case PythonNodeKind.Call:
                        if (!allowedFunctionsSet.Contains(node.Name))
                        {
                            return new Approval(
                                ApprovalDecision.Escalate,
                                Explanation: $"Function '{node.Name}' is not in the allowed list. Allowed functions: {allowedFunctionsText}");
                        }

                        if (builtins.Contains(node.Name))
                        {
                            return new Approval(
                                ApprovalDecision.Escalate,
                                Explanation: $"Built-in function '{node.Name}' is not allowed for security reasons.");
                        }

                        break;

                    case PythonNodeKind.DunderAssignment:
                        if (!allowSystemStateModification)
                        {
                            return new Approval(
                                ApprovalDecision.Escalate,
                                Explanation: "Modification of system state (dunder attributes) is not allowed.");
                        }

                        break;
                }
            }

            return new Approval(ApprovalDecision.Approve, Explanation: "Python code is approved.");
        }

        Task<Approval> Approve(string message, ToolCall call, ToolCallView view, IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken) =>
            Task.FromResult(Decide(ApproverSupport.FirstArgumentText(call).Trim()));

        var parameters = new JsonObject
        {
            ["allowed_modules"] = ApproverSupport.ToJsonArray(allowedModules),
            ["allowed_functions"] = ApproverSupport.ToJsonArray(allowedFunctions),
        };
        if (disallowedBuiltins is not null)
        {
            parameters["disallowed_builtins"] = ApproverSupport.ToJsonArray(disallowedBuiltins);
        }

        if (sensitiveModules is not null)
        {
            parameters["sensitive_modules"] = ApproverSupport.ToJsonArray(sensitiveModules);
        }

        if (allowSystemStateModification)
        {
            parameters["allow_system_state_modification"] = true;
        }

        return new ApproverDef(PythonAllowlistName, Approve) { Params = parameters };
    }

    /// <summary>
    /// The <c>python_allowlist</c> registry factory (what a policy file's entry resolves through): accepts
    /// <c>allowed_modules</c> and <c>allowed_functions</c> (required string arrays), <c>disallowed_builtins</c> and
    /// <c>sensitive_modules</c> (string arrays) and <c>allow_system_state_modification</c> (bool); any other parameter
    /// or a wrongly typed one is an <see cref="ArgumentException"/>. A JSON <c>null</c> for an optional parameter is
    /// Python's <c>None</c>: the default applies. Every parameter the entry gives is recorded in
    /// <see cref="ApproverDef.Params"/>, an explicit <c>false</c> or <c>null</c> included
    /// (<see cref="ApproverSupport.RecordGiven"/>).
    /// </summary>
    internal static ApproverDef PythonAllowlistFromParams(JsonObject parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        IReadOnlyList<string>? allowedModules = null;
        IReadOnlyList<string>? allowedFunctions = null;
        IReadOnlySet<string>? disallowedBuiltins = null;
        IReadOnlySet<string>? sensitiveModules = null;
        bool? allowSystemStateModification = null;
        foreach (var pair in parameters)
        {
            switch (pair.Key)
            {
                case "allowed_modules":
                    allowedModules = ApproverSupport.RequireStringList(pair.Value, PythonAllowlistName, pair.Key);
                    break;
                case "allowed_functions":
                    allowedFunctions = ApproverSupport.RequireStringList(pair.Value, PythonAllowlistName, pair.Key);
                    break;
                case "disallowed_builtins":
                    disallowedBuiltins = pair.Value is null ? null : ApproverSupport.RequireStringList(pair.Value, PythonAllowlistName, pair.Key).ToHashSet(StringComparer.Ordinal);
                    break;
                case "sensitive_modules":
                    sensitiveModules = pair.Value is null ? null : ApproverSupport.RequireStringList(pair.Value, PythonAllowlistName, pair.Key).ToHashSet(StringComparer.Ordinal);
                    break;
                case "allow_system_state_modification":
                    allowSystemStateModification = pair.Value is null ? null : ApproverSupport.RequireBool(pair.Value, PythonAllowlistName, pair.Key);
                    break;
                default:
                    throw new ArgumentException($"Unknown parameter '{pair.Key}' for approver '{PythonAllowlistName}'.", nameof(parameters));
            }
        }

        var approver = PythonAllowlist(
            allowedModules ?? throw ApproverSupport.Required(PythonAllowlistName, "allowed_modules"),
            allowedFunctions ?? throw ApproverSupport.Required(PythonAllowlistName, "allowed_functions"),
            disallowedBuiltins,
            sensitiveModules,
            allowSystemStateModification ?? false);
        ApproverSupport.RecordGiven(approver.Params, parameters);
        return approver;
    }
}

using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Approval;

// Declared inside the namespace so that `Approval` names the record: from here the bare name would otherwise
// bind to this very namespace (an enclosing namespace's members win over the compilation unit's usings).
using Approval = InspectAzureAI.Eval.Approval.Approval;

public static partial class ExampleApprovers
{
    /// <summary>The registry name of <see cref="BashAllowlist"/> (the Python function's name).</summary>
    private const string BashAllowlistName = "bash_allowlist";

    /// <summary>Python's <c>dangerous_chars</c>, in its order (the rejection lists the ones present in this order).</summary>
    private static readonly char[] BashDangerousCharacters = ['&', '|', ';', '>', '<', '`', '$', '(', ')'];

    /// <summary>
    /// Port of <c>examples/approval/approval.py</c> <c>bash_allowlist</c> (registry name <c>bash_allowlist</c>): reads
    /// the call's first argument as the command whatever its name (<see cref="ApproverSupport.FirstArgumentText"/>),
    /// rejects an empty command, a command <see cref="Shlex"/> cannot split, one containing a shell metacharacter
    /// (<c>&amp; | ; &gt; &lt; ` $ ( )</c>) and <c>sudo</c> unless <paramref name="allowSudo"/>; escalates a base
    /// command outside <paramref name="allowedCommands"/> or a first argument outside the command's entry in
    /// <paramref name="commandSpecificRules"/>; approves everything else. Decision explanations are Python's.
    /// The returned <see cref="ApproverDef.Params"/> always carry <c>allowed_commands</c>, <c>allow_sudo</c> only
    /// when true and <c>command_specific_rules</c> only when given, because this direct API cannot tell an explicit
    /// default from an omitted argument; the registry factory (<see cref="BashAllowlistFromParams"/>) records
    /// whatever the policy entry gives, as <c>registry_params</c> does.
    /// Deviation: Python lists the allowed commands from an unordered set; this port lists them in the order given
    /// (duplicates dropped).
    /// </summary>
    public static ApproverDef BashAllowlist(
        IReadOnlyList<string> allowedCommands,
        bool allowSudo = false,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? commandSpecificRules = null)
    {
        ArgumentNullException.ThrowIfNull(allowedCommands);

        var allowedSet = new HashSet<string>(allowedCommands, StringComparer.Ordinal);
        var allowedText = string.Join(", ", allowedCommands.Distinct(StringComparer.Ordinal));
        var rules = commandSpecificRules?.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.ToArray(), StringComparer.Ordinal)
            ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        Approval Decide(ToolCall call)
        {
            var command = ApproverSupport.FirstArgumentText(call).Trim();
            if (command.Length == 0)
            {
                return Reject("Empty command");
            }

            IReadOnlyList<string> tokens;
            try
            {
                tokens = Shlex.Split(command);
            }
            catch (FormatException e)
            {
                return Reject($"Invalid command syntax: {e.Message}");
            }

            var dangerous = BashDangerousCharacters.Where(c => command.Contains(c)).ToArray();
            if (dangerous.Length > 0)
            {
                return Reject($"Command contains potentially dangerous characters: {string.Join(", ", dangerous)}");
            }

            // A non-blank command splits into at least one token (every non-blank character starts a word).
            var baseCommand = tokens[0];

            // Handle sudo
            if (baseCommand == "sudo")
            {
                if (!allowSudo)
                {
                    return Reject("sudo is not allowed");
                }

                if (tokens.Count < 2)
                {
                    return Reject("Invalid sudo command");
                }

                tokens = tokens.Skip(1).ToArray();
                baseCommand = tokens[0];
            }

            if (!allowedSet.Contains(baseCommand))
            {
                return Escalate($"Command '{baseCommand}' is not in the allowed list. Allowed commands: {allowedText}");
            }

            // Check command-specific rules
            if (rules.TryGetValue(baseCommand, out var allowedSubcommands)
                && tokens.Count > 1
                && !allowedSubcommands.Contains(tokens[1], StringComparer.Ordinal))
            {
                return Escalate($"{baseCommand} subcommand '{tokens[1]}' is not allowed. Allowed subcommands: {string.Join(", ", allowedSubcommands)}");
            }

            return new Approval(ApprovalDecision.Approve, Explanation: $"Command '{command}' is approved.");
        }

        Task<Approval> Approve(string message, ToolCall call, ToolCallView view, IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken) =>
            Task.FromResult(Decide(call));

        var parameters = new JsonObject { ["allowed_commands"] = ApproverSupport.ToJsonArray(allowedCommands) };
        if (allowSudo)
        {
            parameters["allow_sudo"] = true;
        }

        if (commandSpecificRules is not null)
        {
            var rulesJson = new JsonObject();
            foreach (var (name, subcommands) in commandSpecificRules)
            {
                rulesJson[name] = ApproverSupport.ToJsonArray(subcommands);
            }

            parameters["command_specific_rules"] = rulesJson;
        }

        return new ApproverDef(BashAllowlistName, Approve) { Params = parameters };

        static Approval Reject(string explanation) => new(ApprovalDecision.Reject, Explanation: explanation);

        static Approval Escalate(string explanation) => new(ApprovalDecision.Escalate, Explanation: explanation);
    }

    /// <summary>
    /// The <c>bash_allowlist</c> registry factory (what an approval policy file's entry resolves through): accepts
    /// <c>allowed_commands</c> (required, a list of strings), <c>allow_sudo</c> (a boolean) and
    /// <c>command_specific_rules</c> (an object mapping a command to a list of allowed subcommands); any other
    /// parameter, a missing <c>allowed_commands</c> or a wrongly typed value is an <see cref="ArgumentException"/>.
    /// A JSON <c>null</c> for an optional parameter is Python's <c>None</c>: the default applies. Every parameter
    /// the entry gives is recorded in <see cref="ApproverDef.Params"/>, an explicit <c>false</c> or <c>null</c>
    /// included (<see cref="ApproverSupport.RecordGiven"/>).
    /// </summary>
    internal static ApproverDef BashAllowlistFromParams(JsonObject parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        IReadOnlyList<string>? allowedCommands = null;
        bool? allowSudo = null;
        IReadOnlyDictionary<string, IReadOnlyList<string>>? commandSpecificRules = null;
        foreach (var pair in parameters)
        {
            switch (pair.Key)
            {
                case "allowed_commands":
                    allowedCommands = ApproverSupport.RequireStringList(pair.Value, BashAllowlistName, pair.Key);
                    break;
                case "allow_sudo":
                    allowSudo = pair.Value is null ? null : ApproverSupport.RequireBool(pair.Value, BashAllowlistName, pair.Key);
                    break;
                case "command_specific_rules":
                    commandSpecificRules = pair.Value is null ? null : ApproverSupport.RequireStringLists(pair.Value, BashAllowlistName, pair.Key);
                    break;
                default:
                    throw new ArgumentException($"Unknown parameter '{pair.Key}' for approver '{BashAllowlistName}'.", nameof(parameters));
            }
        }

        var approver = BashAllowlist(
            allowedCommands ?? throw ApproverSupport.Required(BashAllowlistName, "allowed_commands"),
            allowSudo ?? false,
            commandSpecificRules);
        ApproverSupport.RecordGiven(approver.Params, parameters);
        return approver;
    }
}

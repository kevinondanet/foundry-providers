using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Approval;

/// <summary>The built-in approvers of <c>inspect_ai.approval</c>: <see cref="Auto"/> and <see cref="Human"/>.</summary>
public static partial class Approvers
{
    /// <summary>The explanation the auto approver records.</summary>
    public const string AutomaticDecision = "Automatic decision.";

    /// <summary>
    /// Port of <c>auto_approver</c> (registry name <c>auto</c>): applies <paramref name="decision"/> to every call
    /// it is asked about (approve when null, which also leaves the recorded params empty as Python does for a
    /// defaulted argument).
    /// </summary>
    public static ApproverDef Auto(ApprovalDecision? decision = null)
    {
        var resolved = decision ?? ApprovalDecision.Approve;
        Task<Approval> Approve(string message, ToolCall call, ToolCallView view, IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken) =>
            Task.FromResult(new Approval(resolved, Explanation: AutomaticDecision));

        return new ApproverDef("auto", Approve)
        {
            Params = decision is { } given ? new JsonObject { ["decision"] = given.ToPython() } : new JsonObject(),
        };
    }

    /// <summary>The <c>auto</c> registry factory: accepts <c>decision</c>; any other parameter is an <see cref="ArgumentException"/>.</summary>
    internal static ApproverDef AutoFromParams(JsonObject parameters)
    {
        ApprovalDecision? decision = null;
        foreach (var pair in parameters)
        {
            decision = pair.Key == "decision"
                ? ApprovalDecisions.Parse(RequireString(pair.Value, "auto", pair.Key))
                : throw new ArgumentException($"Unknown parameter '{pair.Key}' for approver 'auto'.", nameof(parameters));
        }

        return Auto(decision);
    }

    private static string RequireString(JsonNode? value, string approver, string parameter) =>
        value is JsonValue json && json.TryGetValue<string>(out var text)
            ? text
            : throw new ArgumentException($"Parameter '{parameter}' of approver '{approver}' must be a string.", parameter);
}

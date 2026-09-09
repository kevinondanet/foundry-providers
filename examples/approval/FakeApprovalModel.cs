using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Approval;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The model behind <c>--fake</c>: a <see cref="ScriptedModelApi"/> that plays the model of
/// <c>examples/approval/approval.py</c> without any network access. Every turn is computed from the conversation
/// (the scripted api serves its turns from one global queue, and the two samples run concurrently), so each sample
/// gets its own script whatever the scheduling order: the bash sample runs <c>ls -la</c> (approved by
/// <c>bash_allowlist</c>) and <c>rm -rf /tmp/demo</c> (escalated to the human approver), the python sample runs
/// <c>print</c>, <c>math.factorial</c> (both approved by <c>python_allowlist</c>) and <c>shutil.rmtree</c>
/// (escalated); each then submits, which ends the react loop. A rejected call comes back as a tool error and the
/// script simply moves on to its next step.
/// </summary>
internal static class FakeApprovalModel
{
    public const string ModelName = "approval-scripted";

    /// <summary>More turns than the two scripts need (3 + 4); past its script a sample always submits.</summary>
    private const int TurnBudget = 32;

    /// <summary>Sample 1: the bash tool, one call per turn as the react prompt asks.</summary>
    private static readonly (string Thought, string Function, object Arguments)[] BashScript =
    [
        ("First the ls command: a long listing of the working directory.", "bash", new { cmd = "ls -la" }),
        ("Now the rm command: removing a scratch directory.", "bash", new { cmd = "rm -rf /tmp/demo" }),
    ];

    /// <summary>Sample 2: the python tool, one call per turn.</summary>
    private static readonly (string Thought, string Function, object Arguments)[] PythonScript =
    [
        ("First the print function.", "python", new { code = "print('hello')" }),
        ("Next math.factorial.", "python", new { code = "import math\nprint(math.factorial(5))" }),
        ("Finally shutil.rmtree on a scratch directory.", "python", new { code = "import shutil\nshutil.rmtree('/tmp/demo')" }),
    ];

    public static Model Create() => new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(Respond), TurnBudget), ModelName));

    private static ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> _)
    {
        // The first user message is the sample input; it names the tool the sample is about.
        var input = messages.OfType<ChatMessageUser>().FirstOrDefault()?.Text ?? "";
        var script = input.Contains("bash tool", StringComparison.OrdinalIgnoreCase) ? BashScript : PythonScript;
        var step = messages.Count(message => message is ChatMessageAssistant);

        ModelOutput output;
        if (step < script.Length)
        {
            var (thought, function, arguments) = script[step];
            output = ScriptedTurn.ToolCall(function, arguments, text: thought).Output!;
        }
        else
        {
            var calls = messages.OfType<ChatMessageTool>().Count();
            var rejected = messages.OfType<ChatMessageTool>().Count(message => message.Error is not null);
            var answer = $"Demonstrated {calls} tool call{(calls == 1 ? "" : "s")}; {rejected} {(rejected == 1 ? "was" : "were")} not permitted by the approval policy.";
            output = ScriptedTurn.ToolCall(Agents.DefaultSubmitName, new { answer }, text: "That completes the demonstration.").Output!;
        }

        return WithUsage(messages, output);
    }

    /// <summary>A rough token count so the run reports usage the way a real model would.</summary>
    private static ModelOutput WithUsage(IReadOnlyList<ChatMessage> messages, ModelOutput output)
    {
        var inputTokens = messages.Sum(message => message.Text.Length) / 4 + 1;
        var completion = output.Completion.Length + (output.Message.ToolCalls?.Sum(call => call.Arguments.ToJsonString().Length) ?? 0);
        var outputTokens = completion / 4 + 1;
        return output with { Usage = new ModelUsage(inputTokens, outputTokens, inputTokens + outputTokens) };
    }
}

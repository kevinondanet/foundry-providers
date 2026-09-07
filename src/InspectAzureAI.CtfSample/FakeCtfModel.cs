using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.CtfSample;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The model behind <c>--fake</c>: a <see cref="ScriptedModelApi"/> that plays a competent CTF player without any
/// network access. Every turn is computed from the conversation so far, so the run is deterministic and still
/// exercises the real pipeline: the commands it issues run inside the Docker sandbox, the tool output comes back
/// through the transcript, and the flag it submits is whatever it actually found in the container.
/// </summary>
internal static partial class FakeCtfModel
{
    public const string ModelName = "ctf-scripted";

    private const int TurnBudget = 64;

    [GeneratedRegex(@"picoCTF\{[^}]*\}")]
    private static partial Regex FlagPattern();

    /// <summary>Recon first, then one sweep that tries the raw bytes, base64 and gzip of every file under <c>challenge/</c>.</summary>
    private static readonly (string Thought, string Command)[] Playbook =
    [
        ("Let me look at the challenge directory, including hidden files.", "ls -laR challenge"),
        ("I'll scan every file for the flag format directly, then try the common encodings (base64, gzip).",
            "for f in $(find challenge -type f); do echo \"== $f\"; "
            + "grep -ao 'picoCTF{[^}]*}' \"$f\"; "
            + "base64 -d < \"$f\" 2>/dev/null | grep -ao 'picoCTF{[^}]*}'; "
            + "gzip -dc \"$f\" 2>/dev/null | grep -ao 'picoCTF{[^}]*}'; done; true"),
    ];

    public static Model Create() => new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(Respond), TurnBudget), ModelName));

    private static ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> _)
    {
        var step = messages.Count(message => message is ChatMessageAssistant);
        var lastToolOutput = messages.OfType<ChatMessageTool>().LastOrDefault()?.Text ?? "";
        var found = FlagPattern().Match(lastToolOutput);

        ModelOutput output;
        if (found.Success)
        {
            output = ScriptedTurn.ToolCall(Solvers.BasicAgentSubmitName, new { answer = found.Value }, text: $"Found the flag {found.Value}; submitting it.").Output!;
        }
        else if (step < Playbook.Length)
        {
            var (thought, command) = Playbook[step];
            output = ScriptedTurn.ToolCall("bash", new { cmd = command }, text: thought).Output!;
        }
        else
        {
            output = ScriptedTurn.ToolCall(Solvers.BasicAgentSubmitName, new { answer = "I could not find the flag." }, text: "Giving up.").Output!;
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

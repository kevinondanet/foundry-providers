using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.EvalsInEval;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The model behind <c>--fake</c>: a <see cref="ScriptedModelApi"/> whose every turn is computed from the conversation,
/// so one script serves the outer <c>evals_in_eval</c> task (reached through the sandbox agent bridge by the
/// <see cref="FakeClaudeCli"/>) and the two inner tasks (<c>file_probe</c> makes a <c>list_files</c> call and answers
/// Yes/No from the listing; <c>bash_task</c> makes a <c>bash</c> call and repeats what it printed).
/// </summary>
internal static class FakeEvalsInEvalModel
{
    public const string ModelName = "evals-in-eval-scripted";

    /// <summary>The plan the stand-in CLI gets for the outer task's prompt.</summary>
    public const string Plan = "I'll run both evaluations with the inspect CLI now: `inspect eval file_probe.py` and then `inspect eval bash_task.py`.";

    /// <summary>The report the stand-in CLI gets once the (canned) eval output is in the conversation.</summary>
    public const string Report = "Both evaluations completed. Accuracy scores:\n- file_probe: accuracy 1.0\n- bash_task: accuracy 1.0";

    /// <summary>The outer task needs two turns, the inner ones two each; the budget leaves room to spare.</summary>
    private const int TurnBudget = 12;

    public static Model Create() => new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(Respond), TurnBudget), ModelName));

    private static ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> _)
    {
        var input = messages.OfType<ChatMessageUser>().FirstOrDefault()?.Text ?? "";
        var lastUser = messages.OfType<ChatMessageUser>().LastOrDefault()?.Text ?? "";
        var results = messages.OfType<ChatMessageTool>().ToList();
        return input switch
        {
            FileProbe.Input => results.Count == 0
                ? Call("list_files", new { dir = "." }, "Listing the current directory.")
                : Text(results[0].Error is null && results[0].Text.Split('\n').Contains("foo.txt") ? "Yes" : "No"),
            BashTask.Input => results.Count == 0
                ? Call("bash", new { cmd = "echo 'hello world'" }, "Printing with bash.")
                : Text(results[0].Text.Trim()),
            EvalsInEvalExample.Input => Text(lastUser.StartsWith("Command output:", StringComparison.Ordinal) ? Report : Plan),
            _ => Text("I only know the prompts of the evals_in_eval example."),
        };
    }

    private static ModelOutput Call(string function, object arguments, string thought) => ScriptedTurn.ToolCall(function, arguments, text: thought).Output!;

    private static ModelOutput Text(string text) => ModelOutput.FromContent(ModelName, text);
}

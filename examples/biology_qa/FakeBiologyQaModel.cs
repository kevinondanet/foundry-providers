using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools.Builtin;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.BiologyQa;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The model behind <c>--fake</c> for <c>examples/biology_qa.py</c>: a <see cref="ScriptedModelApi"/> that plays
/// both the evaluated model and the grader without any network. Every turn is computed from the conversation, so
/// concurrent samples stay deterministic: on the question it calls <c>web_search</c> with the question as the query;
/// once the tool result is in the conversation it answers with what the search returned (the scripted Tavily
/// service answers with the dataset's target); and the <c>model_graded_qa</c> prompt (<c>[Criterion]:</c> /
/// <c>[Submission]:</c>) is graded for real, <c>GRADE: C</c> when the submission contains the criterion and
/// <c>GRADE: I</c> otherwise.
/// </summary>
internal static class FakeBiologyQaModel
{
    public const string ModelName = "biology-qa-scripted";

    /// <summary>The tool the task offers, called by name as a real model would.</summary>
    public const string SearchTool = "web_search";

    /// <summary>What the script answers when the search found nothing.</summary>
    public const string NoAnswer = "I could not find the answer.";

    /// <summary>The Tavily provider's text for a response with results but no answer.</summary>
    private const string TavilyNoAnswer = "No answer found.";

    /// <summary>Plenty for 20 samples x 3 calls (search, answer, grade), epochs included.</summary>
    private const int TurnBudget = 1024;

    public static Model Create() => new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(Respond), TurnBudget), ModelName));

    /// <summary>One turn: search, answer from the search, or grade.</summary>
    public static ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> tools)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var prompt = messages.OfType<ChatMessageUser>().LastOrDefault()?.Text ?? "";
        ModelOutput output;
        if (IsGradingPrompt(prompt))
        {
            output = ModelOutput.FromContent(ModelName, Grade(prompt));
        }
        else if (messages.OfType<ChatMessageTool>().LastOrDefault() is { } result)
        {
            var found = result.Error is null && result.Text.Length > 0 && result.Text != TavilyNoAnswer && result.Text != BuiltinTools.WebSearchNoResults;
            output = ModelOutput.FromContent(ModelName, found ? result.Text : NoAnswer);
        }
        else
        {
            output = ScriptedTurn.ToolCall(SearchTool, new { query = prompt }, text: "Let me search for that.").Output!;
        }

        return WithUsage(messages, output);
    }

    /// <summary>Whether <paramref name="prompt"/> is the <c>model_graded_qa</c> template.</summary>
    public static bool IsGradingPrompt(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return prompt.Contains("[Criterion]:", StringComparison.Ordinal) && prompt.Contains("[Submission]:", StringComparison.Ordinal);
    }

    /// <summary>Grades a <c>model_graded_qa</c> prompt: C when the submission contains the criterion, else I.</summary>
    public static string Grade(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var criterion = Section(prompt, "[Criterion]:");
        var submission = Section(prompt, "[Submission]:");
        var correct = criterion.Length > 0 && submission.Contains(criterion, StringComparison.OrdinalIgnoreCase);
        return correct
            ? $"The submission states '{criterion}', which meets the criterion.\n\nGRADE: C"
            : $"The submission does not state '{criterion}', so it does not meet the criterion.\n\nGRADE: I";
    }

    /// <summary>The text after <paramref name="label"/> up to the template's next <c>***</c> separator line.</summary>
    private static string Section(string prompt, string label)
    {
        var start = prompt.IndexOf(label, StringComparison.Ordinal);
        if (start < 0)
        {
            return "";
        }

        start += label.Length;
        var end = prompt.IndexOf("\n***", start, StringComparison.Ordinal);
        return (end < 0 ? prompt[start..] : prompt[start..end]).Trim();
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

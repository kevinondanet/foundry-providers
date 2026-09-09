using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.TheoryOfMind;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The model behind <c>--fake</c> for <c>examples/theory_of_mind.py</c>: a <see cref="ScriptedModelApi"/> that
/// plays both the evaluated model and the grader without any network. Every turn is computed from the conversation
/// (samples run concurrently, so a fixed queue of answers would not do): the prompt's template text says which of
/// the four calls of the task it is. The <c>chain_of_thought</c> prompt and the <c>self_critique</c> improved-answer
/// prompt (<c>[Critique]:</c>) get <c>ANSWER: &lt;target&gt;</c>, the target being looked up from the bundled
/// dataset by the question text embedded in the prompt; the critique prompt (<c>[Answer]:</c>, ending
/// <c>Critique: </c>) gets the template's own "already correct" phrase; the <c>model_graded_fact</c> prompt
/// (<c>[Expert]:</c> / <c>[Submission]:</c>) is graded for real, <c>GRADE: C</c> when the submission contains the
/// expert answer and <c>GRADE: I</c> otherwise.
/// </summary>
internal static class FakeTheoryOfMindModel
{
    public const string ModelName = "theory-of-mind-scripted";

    /// <summary>Port of the <c>DEFAULT_CRITIQUE_TEMPLATE</c> instruction: what a critic says of a correct answer.</summary>
    public const string CritiqueReply = "The original answer is fully correct";

    /// <summary>What the script answers when the prompt carries no question of the dataset.</summary>
    public const string UnknownAnswer = "unknown";

    /// <summary>Plenty for 100 samples x 4 calls (answer, critique, improved answer, grade), epochs included.</summary>
    private const int TurnBudget = 4096;

    private static readonly Lazy<IReadOnlyList<(string Question, string Target)>> Answers = new(LoadAnswers, LazyThreadSafetyMode.ExecutionAndPublication);

    public static Model Create() => new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(Respond), TurnBudget), ModelName));

    /// <summary>One turn: classifies the last user message and answers it.</summary>
    public static ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> tools)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var prompt = messages.OfType<ChatMessageUser>().LastOrDefault()?.Text ?? "";
        var reply = Classify(prompt) switch
        {
            PromptKind.Grade => Grade(prompt),
            PromptKind.Critique => CritiqueReply,
            PromptKind.ImprovedAnswer => $"ANSWER: {TargetFor(prompt)}",
            _ => "Tracking each character's beliefs step by step: a character only knows about the moves they were present for, so their belief is the last location they saw, while the object really is wherever it was last moved.\n\n"
                + $"ANSWER: {TargetFor(prompt)}",
        };
        return WithUsage(messages, ModelOutput.FromContent(ModelName, reply));
    }

    /// <summary>Which of the task's four calls <paramref name="prompt"/> is, by its template text.</summary>
    public static PromptKind Classify(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (prompt.Contains("[Expert]:", StringComparison.Ordinal) && prompt.Contains("[Submission]:", StringComparison.Ordinal))
        {
            return PromptKind.Grade;
        }

        if (prompt.Contains("[Critique]:", StringComparison.Ordinal))
        {
            return PromptKind.ImprovedAnswer;
        }

        if (prompt.Contains("[Answer]:", StringComparison.Ordinal) && prompt.TrimEnd().EndsWith("Critique:", StringComparison.Ordinal))
        {
            return PromptKind.Critique;
        }

        return PromptKind.ChainOfThought;
    }

    /// <summary>The dataset target of the question embedded in <paramref name="prompt"/> (the longest question the prompt contains), or <see cref="UnknownAnswer"/>.</summary>
    public static string TargetFor(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        (string Question, string Target)? best = null;
        foreach (var entry in Answers.Value)
        {
            if (prompt.Contains(entry.Question, StringComparison.Ordinal) && (best is null || entry.Question.Length > best.Value.Question.Length))
            {
                best = entry;
            }
        }

        return best?.Target ?? UnknownAnswer;
    }

    /// <summary>Grades a <c>model_graded_fact</c> prompt: C when the submission contains the expert answer, else I.</summary>
    public static string Grade(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var expert = Section(prompt, "[Expert]:");
        var submission = Section(prompt, "[Submission]:");
        var correct = expert.Length > 0 && submission.Contains(expert, StringComparison.OrdinalIgnoreCase);
        return correct
            ? $"The submission names '{expert}', which is the expert answer.\n\nGRADE: C"
            : $"The submission does not name '{expert}', the expert answer.\n\nGRADE: I";
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

    private static IReadOnlyList<(string Question, string Target)> LoadAnswers()
    {
        var dataset = Datasets.Example("theory_of_mind");
        var answers = new List<(string, string)>(dataset.Count);
        foreach (var sample in dataset)
        {
            answers.Add((sample.Input.ToString(), sample.Target.Text));
        }

        return answers;
    }

    /// <summary>A rough token count so the run reports usage the way a real model would.</summary>
    private static ModelOutput WithUsage(IReadOnlyList<ChatMessage> messages, ModelOutput output)
    {
        var inputTokens = messages.Sum(message => message.Text.Length) / 4 + 1;
        var outputTokens = output.Completion.Length / 4 + 1;
        return output with { Usage = new ModelUsage(inputTokens, outputTokens, inputTokens + outputTokens) };
    }

    /// <summary>The four prompts of the task, in the order a sample sees them (the grade last).</summary>
    public enum PromptKind
    {
        ChainOfThought,
        Critique,
        ImprovedAnswer,
        Grade,
    }
}

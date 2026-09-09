using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Popularity;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The model behind <c>--fake</c> for <c>examples/popularity.py</c>: a <see cref="ScriptedModelApi"/> that obeys the
/// system message (it replies exactly "Yes" or "No") without any network access. Every turn is computed from the
/// conversation, so concurrent samples stay deterministic: the answer to a question is fixed by a stable hash of its
/// text (<see cref="Answer"/>), which lands on the dataset's <c>answer_matching_behavior</c> for about half of the
/// questions, or is one fixed value when the example is given <c>-T fake_answer=Yes|No</c>.
/// </summary>
public static class FakePopularityModel
{
    /// <summary>The scripted model's name, as it appears in the banner and the log.</summary>
    public const string ModelName = "popularity-scripted";

    /// <summary>More turns than any run needs (one per sample and epoch).</summary>
    private const int TurnBudget = 10_000;

    /// <summary>Creates the model; <paramref name="fixedAnswer"/> (Yes or No) replaces the per-question rule when given.</summary>
    public static Model Create(string? fixedAnswer = null)
    {
        return FakeModels.Scripted(ModelName, (messages, _) => Respond(messages, fixedAnswer), TurnBudget);
    }

    /// <summary>
    /// The scripted answer to <paramref name="question"/>: "Yes" when the FNV-1a hash of the text is even, else "No".
    /// A stable hash (not <see cref="string.GetHashCode()"/>, which is randomised per process) keeps the run
    /// reproducible across processes and platforms.
    /// </summary>
    public static string Answer(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var hash = 2166136261u;
        foreach (var ch in question)
        {
            unchecked
            {
                hash ^= ch;
                hash *= 16777619u;
            }
        }

        return (hash & 1) == 0 ? "Yes" : "No";
    }

    private static ModelOutput Respond(IReadOnlyList<ChatMessage> messages, string? fixedAnswer)
    {
        // The last user message is the question (the system message precedes it).
        var question = messages.OfType<ChatMessageUser>().LastOrDefault()?.Text ?? "";
        var answer = fixedAnswer ?? Answer(question);
        return FakeModels.Output(ModelName, answer, messages);
    }
}

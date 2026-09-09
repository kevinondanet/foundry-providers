using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.SecurityGuide;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The model behind <c>--fake</c> for <c>examples/security_guide.py</c>: a <see cref="ScriptedModelApi"/> that plays
/// both the model under evaluation and the <c>model_graded_fact()</c> grader (in Python, too, the grader is the same
/// model unless a grader model is given) without any network access. Every turn is computed from the conversation, so
/// concurrent samples stay deterministic. Asked a question it answers with the expert answer the bundled dataset
/// holds for it, except for the abbreviated questions (<c>sqli</c>, <c>xss</c>, <c>cmd injection</c>), which it does
/// not recognise and answers vaguely (<see cref="Answer"/>); asked to grade (a prompt carrying the
/// <c>[BEGIN DATA]</c> block) it compares the submission with the expert answer and replies <c>GRADE: C</c> or
/// <c>GRADE: I</c> (<see cref="Grade"/>), so the run scores 13 of 16.
/// </summary>
public static partial class FakeSecurityGuideModel
{
    /// <summary>The scripted model's name, as it appears in the banner and the log.</summary>
    public const string ModelName = "security-guide-scripted";

    /// <summary>What the scripted model says to a question it does not recognise.</summary>
    public const string VagueAnswer = "input validation and keeping your dependencies patched";

    /// <summary>The abbreviations the scripted model does not know; the questions using them get <see cref="VagueAnswer"/>.</summary>
    public static readonly IReadOnlyList<string> UnknownAbbreviations = ["sqli", "xss", "cmd injection"];

    /// <summary>More turns than any run needs (two per sample and epoch: the answer and the grade).</summary>
    private const int TurnBudget = 10_000;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> ExpertAnswers = new(LoadExpertAnswers);

    /// <summary>Creates the model.</summary>
    public static Model Create() => FakeModels.Scripted(ModelName, Respond, TurnBudget);

    /// <summary>The scripted answer to <paramref name="question"/>: the dataset's expert answer, or <see cref="VagueAnswer"/> for an abbreviated or unknown question.</summary>
    public static string Answer(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (UnknownAbbreviations.Any(abbreviation => question.Contains(abbreviation, StringComparison.OrdinalIgnoreCase)))
        {
            return VagueAnswer;
        }

        return ExpertAnswers.Value.TryGetValue(question.Trim(), out var answer) ? answer : VagueAnswer;
    }

    /// <summary>
    /// The scripted grader's reply to a <c>model_graded_fact</c> prompt: a one-line comparison of the
    /// <c>[Submission]</c> with the <c>[Expert]</c> answer, then <c>GRADE: C</c> when they say the same thing
    /// (ignoring case, surrounding whitespace and a trailing full stop) and <c>GRADE: I</c> otherwise.
    /// </summary>
    public static string Grade(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var expert = Section(prompt, "Expert");
        var submission = Section(prompt, "Submission");
        var correct = expert is not null && submission is not null && Normalize(expert) == Normalize(submission);
        return correct
            ? "The submission states the same content as the expert answer.\n\nGRADE: C"
            : "The submission does not contain the content of the expert answer.\n\nGRADE: I";
    }

    /// <summary>Whether <paramref name="message"/> is a <c>model_graded_fact</c> grading prompt rather than a question.</summary>
    public static bool IsGradingPrompt(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Contains("[BEGIN DATA]", StringComparison.Ordinal) && message.Contains("[END DATA]", StringComparison.Ordinal);
    }

    private static ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> _)
    {
        // A question arrives as [system, user]; a grading prompt as a single user message (the grader gets no system message).
        var request = messages.OfType<ChatMessageUser>().LastOrDefault()?.Text ?? "";
        var reply = IsGradingPrompt(request) ? Grade(request) : Answer(request);
        return FakeModels.Output(ModelName, reply, messages);
    }

    /// <summary>The text of the <c>[{name}]: ...</c> line of the grading prompt, up to the next <c>************</c> separator.</summary>
    private static string? Section(string prompt, string name)
    {
        var match = Regex.Match(prompt, $@"\[{name}\]:(?<text>.*?)\n\*{{12}}", RegexOptions.Singleline);
        return match.Success ? match.Groups["text"].Value.Trim() : null;
    }

    private static string Normalize(string text) => WhitespaceRuns().Replace(text.Trim().TrimEnd('.'), " ").ToLowerInvariant();

    private static IReadOnlyDictionary<string, string> LoadExpertAnswers()
    {
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sample in Datasets.Example(SecurityGuideExample.DatasetName))
        {
            answers[sample.Input.ToString().Trim()] = sample.Target.Text;
        }

        return answers;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRuns();
}

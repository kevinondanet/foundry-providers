using InspectAzureAI.Eval.Solvers;

namespace InspectAzureAI.Eval.Scorers;

/// <summary>
/// Port of the <c>AnswerPattern</c> enum of <c>scorer/_answer.py</c> (<c>_util/pattern.py</c>): regular expressions
/// for output prefixed with <c>ANSWER:</c>. Each matches the <em>last</em> occurrence (models sometimes self-correct,
/// and CoT prompts put the answer on the last line).
/// </summary>
public static class AnswerPattern
{
    /// <summary><c>ANSWER_PATTERN_LETTER</c>: a single letter (multiple choice).</summary>
    public const string Letter = @"(?is)ANSWER\s*:\s*([A-Za-z])(?:[^\w]|\n|$)(?!.*ANSWER\s*:)";

    /// <summary><c>ANSWER_PATTERN_WORD</c>: one run of non-whitespace characters, e.g. a yes/no answer.</summary>
    public const string Word = @"(?is)ANSWER\s*:\s*(\S+?)(?=[.,;:!?]?\s*(?:\n|$))(?!.*ANSWER\s*:)";

    /// <summary>
    /// <c>ANSWER_PATTERN_LINE</c>: the rest of the line after <c>ANSWER:</c>, anchored to the end of the output
    /// (Python's <c>\Z</c> is .NET's <c>\z</c>). The prompt should ask for the answer on a separate last line.
    /// </summary>
    public const string Line = @"(?i)ANSWER\s*:\s*([^\n]+)\s*\z";
}

/// <summary>Ports of <c>choice()</c> (<c>scorer/_choice.py</c>) and <c>answer()</c> (<c>scorer/_answer.py</c>).</summary>
public static partial class Scorers
{
    private static readonly string[] AnswerPatternNames = ["letter", "word", "line"];

    /// <summary>
    /// Port of <c>choice()</c>: scores the choices the <see cref="Solvers.Solvers.MultipleChoice"/> solver marked
    /// against a target of answer letters (<c>"A"</c>, <c>["B", "C"]</c>, <c>"A,10"</c>). Shuffled choices are
    /// unshuffled before comparison and the explanation then shows what the model actually saw. A target that
    /// references a position beyond the choices is a dataset error (<see cref="ArgumentException"/>); a sample
    /// without choices scores incorrect.
    /// </summary>
    public static ScorerDef Choice() => new("choice", ScoreChoice, DefaultMetrics());

    /// <summary>
    /// Port of <c>answer(pattern)</c>: a <see cref="Pattern"/> scorer over the <see cref="AnswerPattern"/> named by
    /// <paramref name="pattern"/> — "letter" (multiple choice), "word" (e.g. yes/no) or "line".
    /// </summary>
    public static ScorerDef Answer(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var regex = pattern switch
        {
            "letter" => AnswerPattern.Letter,
            "word" => AnswerPattern.Word,
            "line" => AnswerPattern.Line,
            _ => throw new ArgumentException($"Unknown answer pattern '{pattern}' (expected one of {string.Join(", ", AnswerPatternNames)}).", nameof(pattern)),
        };
        return new("answer", MatchScorers.Pattern(regex, ignoreCase: true, matchAll: false), DefaultMetrics());
    }

    private static Task<Score> ScoreChoice(TaskState state, Target target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(target);
        var choices = state.Choices;

        // Not a multiple choice sample at all (e.g. re-scoring a free-text log): the target is likely prose, not labels.
        if (choices.Count == 0)
        {
            return Task.FromResult(new Score(ScoreConstants.Incorrect) { Answer = "", Explanation = state.Output.Completion });
        }

        string explanation;
        if (ChoicesAreShuffled(choices))
        {
            explanation = ShuffledExplanation(choices);
            choices = Solvers.Solvers.UnshuffleChoices(choices);
        }
        else
        {
            explanation = state.Output.Completion;
        }

        var (targetPositions, answers) = ScoreTarget(target, choices);
        var generatedSelected = Enumerable.Range(0, choices.Count).Where(i => choices[i].Correct == true);
        var matches = generatedSelected.SequenceEqual(targetPositions.Order());

        return Task.FromResult(new Score(matches ? ScoreConstants.Correct : ScoreConstants.Incorrect)
        {
            Answer = string.Join(", ", answers),
            Explanation = explanation,
        });
    }

    /// <summary>Port of <c>_choices_are_shuffled</c>.</summary>
    private static bool ChoicesAreShuffled(Choices choices) => choices.Where((choice, i) => i != choice.OriginalPosition).Any();

    /// <summary>
    /// Port of <c>_score_target</c>: the target's answer positions (letters split per character, numbers kept whole,
    /// commas and whitespace ignored) and the letters of the choices the model selected.
    /// </summary>
    private static (List<int> TargetPositions, List<string> Answers) ScoreTarget(Target target, Choices choices)
    {
        var targetAnswers = new List<string>();
        foreach (var value in target.Values)
        {
            foreach (var token in value.Replace(',', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.All(char.IsNumber))
                {
                    targetAnswers.Add(token);
                }
                else
                {
                    targetAnswers.AddRange(token.Select(c => c.ToString()));
                }
            }
        }

        var targetPositions = targetAnswers.Select(AnswerLabels.Index).ToList();

        // A "10" target in a 30-option task resolves to index 35: a dataset error, so fail loudly rather than
        // silently scoring incorrect forever.
        var outOfRange = targetAnswers.Where((_, i) => targetPositions[i] >= choices.Count).ToList();
        if (outOfRange.Count > 0)
        {
            throw new ArgumentException(
                $"Choice scorer target references answer(s) beyond the task's {choices.Count} choices: {string.Join(", ", outOfRange)}",
                nameof(target));
        }

        var answers = Enumerable.Range(0, choices.Count).Where(i => choices[i].Correct == true).Select(AnswerLabels.Character).ToList();
        return (targetPositions, answers);
    }

    /// <summary>Port of <c>_shuffled_explanation</c>.</summary>
    private static string ShuffledExplanation(Choices choices)
    {
        var generated = Enumerable.Range(0, choices.Count).Where(i => choices[i].Correct == true).Select(AnswerLabels.Character);
        return "Choices were shuffled before generating a response, the following was sent to the model:\n\n"
            + $"{Solvers.Solvers.AnswerOptions(choices)}\nShuffled answer:\nANSWER: {string.Join(", ", generated)}";
    }
}

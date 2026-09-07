using System.Collections;
using System.Globalization;

namespace InspectAzureAI.Eval.Solvers;

/// <summary>
/// Port of <c>solver/_task_state.py</c> <c>Choice</c>: one choice of a multiple choice question. Only relevant to
/// the <c>multiple_choice</c> solver and the <c>choice</c> scorer.
/// </summary>
/// <param name="Value">The original value of the choice from the sample.</param>
/// <param name="Correct">Did the model think this choice satisfies the question? Null means not set yet.</param>
/// <param name="OriginalPosition">Choices may be re-ordered during processing; this is the position in the sample's list.</param>
public sealed record Choice(string Value, bool? Correct, int OriginalPosition);

/// <summary>
/// Port of <c>solver/_task_state.py</c> <c>Choices</c>: the ordered choices of a sample with the
/// <see cref="Shuffle"/> and <see cref="MarkChoice"/> operations the <c>multiple_choice</c> solver relies on.
/// </summary>
public sealed class Choices : IReadOnlyList<Choice>
{
    private List<Choice> _choices;

    /// <summary>Wraps the sample's choice strings; each gets its index as <see cref="Choice.OriginalPosition"/>.</summary>
    public Choices(IEnumerable<string> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        _choices = choices.Select((value, i) => new Choice(value, null, i)).ToList();
    }

    /// <summary>Wraps already-built choices (the Python <c>list[Choice]</c> constructor form).</summary>
    public Choices(IEnumerable<Choice> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        _choices = choices.ToList();
    }

    public int Count => _choices.Count;

    public Choice this[int index] => _choices[index];

    /// <summary>Port of <c>mark_choice</c>: records whether the model selected the choice at <paramref name="index"/>.</summary>
    public void MarkChoice(int index, bool correct) => _choices[index] = _choices[index] with { Correct = correct };

    /// <summary>
    /// Port of <c>shuffle</c>: reorders the choices, preserving <see cref="Choice.OriginalPosition"/> so they can be
    /// mapped back. Uses Python's <c>random.shuffle</c> algorithm (Fisher–Yates from the end, <c>j = randbelow(i + 1)</c>)
    /// over <paramref name="rand"/> (default <see cref="Random.Shared"/>), so the same draws give the same order as
    /// Python; the generators themselves differ, so a seed does not reproduce Python's order.
    /// </summary>
    public void Shuffle(Random? rand = null)
    {
        var positions = Enumerable.Range(0, _choices.Count).ToList();
        AnswerLabels.ShuffleInPlace(positions, rand ?? Random.Shared);
        _choices = positions.Select(p => _choices[p]).ToList();
    }

    /// <summary>Port of <c>Choices.prompt</c>: formats <paramref name="question"/> and these choices through <paramref name="template"/>.</summary>
    public string Prompt(string question, string template) => Solvers.FormatMultipleChoicePrompt(question, this, template);

    /// <summary>An independent copy (the choices are immutable records, so a list copy is a deep copy).</summary>
    internal Choices Clone() => new(_choices);

    public IEnumerator<Choice> GetEnumerator() => _choices.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Port of <c>_util/answer.py</c>: the letter (then number) labels of multiple choice answers.</summary>
internal static class AnswerLabels
{
    /// <summary>Port of <c>answer_character</c>: 0 → "A" … 25 → "Z", then 26 → "1", 27 → "2", ….</summary>
    public static string Character(int index) =>
        index < 26 ? ((char)('A' + index)).ToString() : (index - 25).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Port of <c>answer_index</c>: a letter → its 0-based index, a number → <c>25 + n</c>; anything else (a separator,
    /// an empty string, several letters) is an <see cref="ArgumentException"/>, as Python raises.
    /// </summary>
    public static int Index(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (answer.Length > 0 && answer.All(char.IsLetter))
        {
            if (answer.Length != 1)
            {
                throw new ArgumentException($"Unexpected multiple choice answer: {answer} (must be a single letter or a number)", nameof(answer));
            }

            return char.ToUpperInvariant(answer[0]) - 'A';
        }

        if (answer.Length > 0 && answer.All(char.IsNumber))
        {
            if (!answer.All(char.IsDigit))
            {
                // Python's str.isnumeric accepts e.g. "½" but int() then rejects it.
                throw new ArgumentException($"invalid literal for int() with base 10: '{answer}'", nameof(answer));
            }

            var value = 0;
            foreach (var c in answer)
            {
                value = checked((value * 10) + CharUnicodeInfo.GetDecimalDigitValue(c));
            }

            return 25 + value;
        }

        throw new ArgumentException($"Unexpected multiple choice answer: {answer} (must be a letter or number)", nameof(answer));
    }

    /// <summary>Python's <c>random.shuffle</c>: Fisher–Yates from the end with <c>j = randbelow(i + 1)</c>.</summary>
    public static void ShuffleInPlace<T>(IList<T> list, Random random)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}

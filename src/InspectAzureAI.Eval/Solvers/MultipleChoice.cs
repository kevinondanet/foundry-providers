using System.Text.RegularExpressions;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Solvers;

/// <summary>
/// Port of the <c>MultipleChoiceTemplate</c> enum of <c>solver/_multiple_choice.py</c>: the prompt templates of
/// the <see cref="Solvers.MultipleChoice"/> solver, verbatim. Based on the multiple choice template in openai
/// simple evals (<c>mmlu_eval.py</c>). Each takes <c>{question}</c>, <c>{choices}</c> and <c>{letters}</c>.
/// </summary>
public static class MultipleChoiceTemplate
{
    /// <summary><c>SINGLE_ANSWER_TEMPLATE</c>.</summary>
    public const string SingleAnswer =
        "Answer the following multiple choice question. The entire content of your response should be of the following format: 'ANSWER: $LETTER' (without quotes) where LETTER is one of {letters}.\n\n{question}\n\n{choices}";

    /// <summary><c>SINGLE_ANSWER_TEMPLATE_COT</c>.</summary>
    public const string SingleAnswerCot =
        "Answer the following multiple choice question. The last line of your response should be of the following format: 'ANSWER: $LETTER' (without quotes) where LETTER is one of {letters}. Think step by step before answering.\n\n{question}\n\n{choices}";

    /// <summary><c>MULTIPLE_ANSWER_TEMPLATE</c>.</summary>
    public const string MultipleAnswer =
        "Answer the following multiple choice question where multiple answers may be correct. The entire content of your response should be of the following format: 'ANSWER: $LETTERS' (without quotes) where LETTERS is one or more of {letters}.\n\n{question}\n\n{choices}";

    /// <summary><c>MULTIPLE_ANSWER_TEMPLATE_COT</c>.</summary>
    public const string MultipleAnswerCot =
        "Answer the following multiple choice question where multiple answers may be correct. The last line of your response should be of the following format: 'ANSWER: $LETTERS' (without quotes) where LETTERS is one or more of {letters}. Think step by step before answering.\n\n{question}\n\n{choices}";
}

/// <summary>Port of <c>solver/_multiple_choice.py</c>: the <c>multiple_choice</c> solver and its answer parsing.</summary>
public static partial class Solvers
{
    /// <summary>
    /// Python's strict <c>^ANSWER: ...</c> line match (<c>re.MULTILINE</c>); the CoT templates put the answer on the
    /// last line, so the last match wins.
    /// </summary>
    private static readonly Regex StrictAnswerPattern = new(
        @"^ANSWER\s*:\s*([A-Za-z\d ,]+)\s*(?:$|\n|\.)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>The less strict fallback kept for backward compatibility (an <c>ANSWER:</c> anywhere).</summary>
    private static readonly Regex LenientAnswerPattern = new(
        @"ANSWER\s*:\s*([A-Za-z\d ,]+)(?:[^\w]|\n|$|\.)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Port of <c>multiple_choice()</c>: formats the sample's choices into a multiple choice prompt, calls
    /// <c>generate()</c>, parses the <c>ANSWER: $LETTER(S)</c> reply and marks <see cref="TaskState.Choices"/>
    /// accordingly. The sample must have choices; the only built-in compatible scorer is <c>choice</c>; generate
    /// is called internally.
    /// </summary>
    /// <param name="template">
    /// Template for the question; defaults per <paramref name="cot"/> and <paramref name="multipleCorrect"/> from
    /// <see cref="MultipleChoiceTemplate"/>. Must contain <c>{question}</c> and <c>{choices}</c> (which becomes an
    /// <c>A) … B) …</c> list); <c>{letters}</c> (e.g. <c>A,B,C</c>) is optional. An empty string means the default.
    /// </param>
    /// <param name="cot">Ask for step-by-step reasoning before the answer (no effect with a custom template).</param>
    /// <param name="multipleCorrect">Allow several answers, e.g. <c>ANSWER: B, C</c> (no effect with a custom template).</param>
    /// <param name="maxTokens">Passed to generate as <c>max_tokens</c>.</param>
    /// <param name="shuffle">
    /// Port of the deprecated <c>shuffle</c> argument: shuffles the choices with this generator before prompting and,
    /// once an answer is parsed, rewrites the prompt and reply in the message history as if unshuffled (the state's
    /// choices stay shuffled). Prefer <see cref="Dataset.IDataset.ShuffleChoices"/> at dataset load time.
    /// </param>
    public static Solver MultipleChoice(
        string? template = null,
        bool cot = false,
        bool multipleCorrect = false,
        int? maxTokens = null,
        Random? shuffle = null)
    {
        if (!string.IsNullOrEmpty(template) && !ValidMultipleChoiceTemplate(template))
        {
            throw new ArgumentException("The template must contain '{question}' and '{choices}' placeholders for string substitution.", nameof(template));
        }

        if (string.IsNullOrEmpty(template))
        {
            template = multipleCorrect
                ? (cot ? MultipleChoiceTemplate.MultipleAnswerCot : MultipleChoiceTemplate.MultipleAnswer)
                : (cot ? MultipleChoiceTemplate.SingleAnswerCot : MultipleChoiceTemplate.SingleAnswer);
        }

        var config = maxTokens is null ? null : new GenerateConfig { MaxTokens = maxTokens };
        return async (state, generate, cancellationToken) =>
        {
            if (state.Choices.Count == 0)
            {
                throw new InvalidOperationException("The multiple_choice solver requires samples with choices");
            }

            if (shuffle is not null)
            {
                state.Choices.Shuffle(shuffle);
            }

            // The raw question is needed again if the history is rewritten to hide the shuffle.
            var originalQuestion = state.UserPrompt.Text;
            SetUserPromptText(state, FormatMultipleChoicePrompt(originalQuestion, state.Choices, template));

            state = await generate(state, ToolCallsMode.Loop, config, cancellationToken).ConfigureAwait(false);

            var answers = ParseAnswers(state, multipleCorrect);
            if (answers.Count > 0)
            {
                SetChoicesBasedOnGeneratedResponse(state, answers);
                if (shuffle is not null)
                {
                    PretendWeDidntShuffle(state, originalQuestion, template);
                }
            }

            return state;
        };
    }

    /// <summary>Port of <c>valid_template</c>: the template has the <c>{question}</c> and <c>{choices}</c> placeholders.</summary>
    internal static bool ValidMultipleChoiceTemplate(string template) =>
        template.Contains("{question}", StringComparison.Ordinal) && template.Contains("{choices}", StringComparison.Ordinal);

    /// <summary>Port of <c>unshuffle_choices</c>: the choices sorted back by original position.</summary>
    internal static Choices UnshuffleChoices(Choices choices) => new(choices.OrderBy(choice => choice.OriginalPosition));

    /// <summary>Port of <c>answer_options</c>: <c>"A) choice 1\nB) choice 2\nC) choice 3"</c>.</summary>
    internal static string AnswerOptions(Choices choices) =>
        string.Join("\n", choices.Select((choice, i) => $"{AnswerLabels.Character(i)}) {choice.Value}"));

    /// <summary>Port of <c>prompt()</c>: fills <c>{choices}</c>, <c>{letters}</c> and <c>{question}</c> into <paramref name="template"/> (Python <c>str.format</c>, so any other placeholder is an error).</summary>
    internal static string FormatMultipleChoicePrompt(string question, Choices choices, string template)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(template);
        var letters = string.Join(",", Enumerable.Range(0, choices.Count).Select(AnswerLabels.Character));
        return PythonFormat.Format(template, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["choices"] = AnswerOptions(choices),
            ["letters"] = letters,
            ["question"] = question,
        });
    }

    /// <summary>
    /// Port of <c>parse_answers</c>: the letters after the last <c>ANSWER:</c> (a strict whole-line match first, then
    /// the lenient one); "AB", "A,B", "A B" and "A and B" are all accepted for multiple answers. Anything that is not
    /// exactly the allowed labels — "None of the above", "Don't know", a stray letter — yields an empty set and the
    /// choices are left unmarked, which the choice scorer then scores incorrect.
    /// </summary>
    internal static IReadOnlySet<string> ParseAnswers(TaskState state, bool multipleCorrect)
    {
        var completion = state.Output.Completion;
        var matches = StrictAnswerPattern.Matches(completion);
        if (matches.Count == 0)
        {
            matches = LenientAnswerPattern.Matches(completion);
        }

        if (matches.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        // Strip a trailing full stop and upper-case so "ANSWER: b" is accepted (the regexes are case-insensitive).
        var matched = matches[^1].Groups[1].Value.Trim().TrimEnd('.').ToUpperInvariant();
        var allowed = Enumerable.Range(0, state.Choices.Count).Select(AnswerLabels.Character).ToHashSet(StringComparer.Ordinal);

        if (multipleCorrect)
        {
            // Separators may be commas, spaces, the word "and", or nothing at all.
            matched = matched.Replace(" AND ", ",", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);

            // Empty tokens come from a trailing or Oxford comma.
            var splitComma = matched.Split(',').Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal);
            if (splitComma.IsSubsetOf(allowed))
            {
                return splitComma;
            }

            var splitNothing = matched.Select(c => c.ToString()).ToHashSet(StringComparer.Ordinal);
            if (splitNothing.IsSubsetOf(allowed))
            {
                return splitNothing;
            }
        }
        else
        {
            // A single allowed label, tolerating a stray comma ("ANSWER: A,") like the multiple-answer branch.
            var tokens = matched.Split(',').Where(t => t.Length > 0).ToList();
            if (tokens.Count == 1 && allowed.Contains(tokens[0]))
            {
                return new HashSet<string>(StringComparer.Ordinal) { tokens[0] };
            }
        }

        return new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Port of <c>set_choices_based_on_generated_response</c>: marks every choice as selected or not.</summary>
    private static void SetChoicesBasedOnGeneratedResponse(TaskState state, IReadOnlySet<string> answers)
    {
        var trueAnswers = answers.Select(AnswerLabels.Index).ToHashSet();
        for (var i = 0; i < state.Choices.Count; i++)
        {
            state.Choices.MarkChoice(i, trueAnswers.Contains(i));
        }
    }

    /// <summary>
    /// Port of <c>pretend_we_didnt_shuffle</c>: rewrites the prompt and the reply in the message history (and
    /// <see cref="TaskState.Output"/>) as if the choices had not been shuffled, so the log matches the sample's
    /// target; <see cref="TaskState.Choices"/> is left shuffled so scoring can still explain what the model saw.
    /// </summary>
    private static void PretendWeDidntShuffle(TaskState state, string originalQuestion, string template)
    {
        SetUserPromptText(state, FormatMultipleChoicePrompt(originalQuestion, UnshuffleChoices(state.Choices), template));

        var answerText = string.Join(", ", state.Choices
            .Where(choice => choice.Correct == true)
            .Select(choice => AnswerLabels.Character(choice.OriginalPosition))
            .Order(StringComparer.Ordinal));
        var pretendAnswer = $"ANSWER: {answerText}";

        var last = state.Messages[^1];
        var rewritten = last with { Content = pretendAnswer };
        state.Messages[^1] = rewritten;

        // In Python the appended message and output.message are the same object; keep the output's first choice in step.
        var output = state.Output with { Completion = pretendAnswer };
        if (output.Choices.Count > 0 && ReferenceEquals(output.Choices[0].Message, last) && rewritten is ChatMessageAssistant assistant)
        {
            output = output with { Choices = [output.Choices[0] with { Message = assistant }, .. output.Choices.Skip(1)] };
        }

        state.Output = output;
    }

    /// <summary>Port of the <c>state.user_prompt.text = …</c> setter: replaces the last user message's text in place.</summary>
    private static void SetUserPromptText(TaskState state, string text)
    {
        var prompt = state.UserPrompt;
        var index = state.Messages.FindLastIndex(m => ReferenceEquals(m, prompt));
        state.Messages[index] = prompt with { Content = WithText(prompt.Content, text) };
    }
}

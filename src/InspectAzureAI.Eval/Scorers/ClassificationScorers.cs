using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Solvers;

namespace InspectAzureAI.Eval.Scorers;

/// <summary>Port of <c>scorer/_classification.py</c>: the <c>f1</c> and <c>exact</c> scorers.</summary>
public static partial class Scorers
{
    /// <summary>
    /// Port of <c>f1(answer_fn, stop_words)</c>: the SQuAD-style token F1 between the completion (or
    /// <paramref name="answerFn"/> of it) and the best-matching target, rounded to two decimals; registered with
    /// <c>[mean(), stderr()]</c>.
    /// </summary>
    public static ScorerDef F1(Func<string, string>? answerFn = null, IReadOnlyList<string>? stopWords = null) =>
        new("f1", (state, target, _) =>
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(target);
            var answer = answerFn is not null ? answerFn(state.Output.Completion) : state.Output.Completion;
            return Task.FromResult(new Score(Classification.MaxF1Score(answer, target.Values, stopWords)) { Answer = answer });
        }, [Metrics.Mean(), Metrics.Stderr()]);

    /// <summary>
    /// Port of <c>exact()</c>: CORRECT when the normalized completion equals a normalized target (word order and
    /// count preserved); registered with <c>[mean(), stderr()]</c>.
    /// </summary>
    public static ScorerDef Exact() =>
        new("exact", (state, target, _) =>
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(target);
            var answer = state.Output.Completion;
            var exact = Classification.MaxExactScore(answer, target.Values);
            return Task.FromResult(new Score(exact == 1.0 ? ScoreConstants.Correct : ScoreConstants.Incorrect) { Answer = answer });
        }, [Metrics.Mean(), Metrics.Stderr()]);
}

/// <summary>The normalization and F1 arithmetic of <c>scorer/_classification.py</c>.</summary>
internal static partial class Classification
{
    /// <summary>Python's <c>string.punctuation</c>.</summary>
    private const string Punctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    /// <summary>Python's <c>string.whitespace + string.punctuation</c>, the characters <c>strip_punctuation</c> trims.</summary>
    private static readonly char[] StripChars = (" \t\n\r\v\f" + Punctuation).ToCharArray();

    /// <summary>Port of <c>max_f1_score</c>: the best F1 over the non-blank targets, rounded to two decimals.</summary>
    public static double MaxF1Score(string answer, IReadOnlyList<string> targets, IReadOnlyList<string>? stopWords = null)
    {
        var maxF1 = 0.0;
        foreach (var target in targets)
        {
            if (target.Trim().Length > 0)
            {
                maxF1 = Math.Max(maxF1, ComputeF1(answer, target, stopWords));
            }
        }

        return Math.Round(maxF1, 2, MidpointRounding.ToEven);
    }

    /// <summary>Port of <c>max_exact_score</c>: 1 when the normalized answer equals a normalized non-blank target.</summary>
    public static double MaxExactScore(string answer, IReadOnlyList<string> targets)
    {
        var maxExact = 0.0;
        var answerNorm = Normalize(answer);
        foreach (var target in targets)
        {
            if (target.Trim().Length > 0 && string.Equals(Normalize(target), answerNorm, StringComparison.Ordinal))
            {
                maxExact = 1.0;
            }
        }

        return maxExact;
    }

    /// <summary>Port of <c>compute_f1</c>: the SQuAD F1 between the word bags of the answer and the target.</summary>
    public static double ComputeF1(string answer, string target, IReadOnlyList<string>? stopWords = null) =>
        F1(ToWords(answer, stopWords), ToWords(target, stopWords));

    private static HashSet<string> ToWords(string text, IReadOnlyList<string>? stopWords) =>
        PythonText.SplitWhitespace(Normalize(text, stopWords)).ToHashSet(StringComparer.Ordinal);

    private static double F1(HashSet<string> answerWords, HashSet<string> targetWords)
    {
        var intersection = answerWords.Intersect(targetWords, StringComparer.Ordinal).Count();
        var precision = answerWords.Count == 0 ? 1.0 : intersection / (double)answerWords.Count;
        var recall = targetWords.Count == 0 ? 1.0 : intersection / (double)targetWords.Count;
        return precision == 0.0 && recall == 0.0 ? 0.0 : 2 * precision * recall / (precision + recall);
    }

    /// <summary>Port of <c>_normalize</c>: tokens split on spaces and hyphens, each normalized, stop words and blanks dropped.</summary>
    public static string Normalize(string text, IReadOnlyList<string>? stopWords = null)
    {
        var foldedStopWords = (stopWords ?? []).Select(NormalizeToken).ToHashSet(StringComparer.Ordinal);
        var tokens = new List<string>();
        foreach (var raw in Tokenizer().Split(text))
        {
            var token = NormalizeToken(raw);
            if (!foldedStopWords.Contains(token))
            {
                tokens.Add(token);
            }
        }

        return string.Join(" ", tokens.Where(t => t.Trim().Length > 0)).Trim();
    }

    /// <summary>Port of <c>_normalize_token</c>: case fold, punctuation removal, number canonicalisation, article removal, whitespace collapse.</summary>
    private static string NormalizeToken(string token)
    {
        token = RemovePunctuation(token.ToLowerInvariant());
        token = NormalizeNumber(token);
        token = Articles().Replace(token, " ");
        return string.Join(" ", PythonText.SplitWhitespace(token));
    }

    /// <summary>Port of <c>_remove_punc</c>: a number keeps its punctuation (boundary punctuation is stripped when that rescues a number).</summary>
    private static string RemovePunctuation(string text)
    {
        if (PythonText.TryParseFiniteFloat(text, out _))
        {
            return text;
        }

        var stripped = text.Trim(StripChars);
        if (PythonText.TryParseFiniteFloat(stripped, out _))
        {
            return stripped;
        }

        return new string(text.Where(ch => !Punctuation.Contains(ch, StringComparison.Ordinal)).ToArray());
    }

    /// <summary>Port of <c>_normalize_number</c>: <c>str(float(text))</c> for a finite number.</summary>
    private static string NormalizeNumber(string text) =>
        PythonText.TryParseFiniteFloat(text, out var value) ? PythonText.FloatRepr(value) : text;

    [GeneratedRegex(" |-")]
    private static partial Regex Tokenizer();

    [GeneratedRegex(@"\b(a|an|the)\b")]
    private static partial Regex Articles();
}
